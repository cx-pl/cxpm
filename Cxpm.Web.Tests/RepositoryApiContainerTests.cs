using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Cxpm.Web.Tests;

public sealed class RepositoryApiContainerTests : IAsyncLifetime
{
    private const string WriteToken = "integration-test-token";
    private const ushort ContainerPort = 8080;
    private readonly string _imageName = $"cxpm-web-tests:{Guid.NewGuid():N}";
    private IContainer? _container;
    private HttpClient? _client;

    public RepositoryApiContainerTests()
    {
    }

    public async Task InitializeAsync()
    {
        var repositoryRoot = FindRepositoryRoot();
        await RunDockerAsync(repositoryRoot, "build", "--tag", _imageName, "--file", "Cxpm.Web/Dockerfile", ".");
        _container = new ContainerBuilder(_imageName)
            .WithPortBinding(ContainerPort, assignRandomHostPort: true)
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", ContainerPort.ToString())
            .WithEnvironment("RepositoryStorage__Path", "/tmp/cxpm-test-packages")
            .WithEnvironment("CXPM_TOKEN", WriteToken)
            .Build();
        await _container.StartAsync();

        _client = new HttpClient
        {
            BaseAddress = new UriBuilder("http", _container.Hostname, _container.GetMappedPublicPort(ContainerPort)).Uri,
            Timeout = TimeSpan.FromSeconds(10),
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using var response = await _client.GetAsync("/api/health", timeout.Token);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested)
            {
                // Wait for Kestrel to finish starting inside the container.
            }
            await Task.Delay(500, timeout.Token);
        }

        throw new TimeoutException("Cxpm.Web did not become ready in the Testcontainers instance.");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_container is not null)
            await _container.DisposeAsync();
        await RunDockerAsync(FindRepositoryRoot(), ["image", "rm", _imageName], allowFailure: true);
    }

    [Fact]
    public async Task ContainerServesThePackageFeedPublishProtocolAndCatalog()
    {
        var packageId = $"webtest-{Guid.NewGuid():N}";
        const string version = "1.2.3";
        var archive = CreatePackageArchive(packageId, version);
        var archiveName = $"{packageId}.{version}.zip";
        var archiveRoute = $"/api/{packageId}/{version}/{archiveName}";

        using var unauthorized = new HttpRequestMessage(HttpMethod.Put, archiveRoute)
        {
            Content = new ByteArrayContent(archive),
        };
        unauthorized.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        using var unauthorizedResponse = await _client!.SendAsync(unauthorized);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedResponse.StatusCode);

        using var upload = new HttpRequestMessage(HttpMethod.Put, archiveRoute)
        {
            Content = new ByteArrayContent(archive),
        };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WriteToken);
        upload.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        using var uploadResponse = await _client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);

        using var duplicateUpload = new HttpRequestMessage(HttpMethod.Put, archiveRoute)
        {
            Content = new ByteArrayContent(archive),
        };
        duplicateUpload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WriteToken);
        duplicateUpload.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        using var duplicateResponse = await _client.SendAsync(duplicateUpload);
        Assert.Equal(HttpStatusCode.PreconditionFailed, duplicateResponse.StatusCode);

        using var incompleteIndex = new HttpRequestMessage(HttpMethod.Put, $"/api/{packageId}/index.json")
        {
            Content = JsonContent.Create(new { versions = new[] { version, "2.0.0" } }),
        };
        incompleteIndex.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WriteToken);
        incompleteIndex.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        using var incompleteResponse = await _client.SendAsync(incompleteIndex);
        Assert.Equal(HttpStatusCode.Conflict, incompleteResponse.StatusCode);

        using var publishIndex = new HttpRequestMessage(HttpMethod.Put, $"/api/{packageId}/index.json")
        {
            Content = JsonContent.Create(new { versions = new[] { version } }),
        };
        publishIndex.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WriteToken);
        publishIndex.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        using var indexResponse = await _client.SendAsync(publishIndex);
        Assert.Equal(HttpStatusCode.NoContent, indexResponse.StatusCode);

        using var getIndex = await _client.GetAsync($"/api/{packageId}/index.json");
        Assert.Equal(HttpStatusCode.OK, getIndex.StatusCode);
        Assert.True(getIndex.Headers.ETag is not null);
        using var indexJson = JsonDocument.Parse(await getIndex.Content.ReadAsStringAsync());
        Assert.Equal(version, indexJson.RootElement.GetProperty("versions")[0].GetString());

        using var updateIndex = new HttpRequestMessage(HttpMethod.Put, $"/api/{packageId}/index.json")
        {
            Content = JsonContent.Create(new { versions = new[] { version } }),
        };
        updateIndex.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WriteToken);
        updateIndex.Headers.IfMatch.Add(new EntityTagHeaderValue("\"stale-tag\""));
        using var staleResponse = await _client.SendAsync(updateIndex);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);

        using var getArchive = await _client.GetAsync(archiveRoute);
        Assert.Equal(HttpStatusCode.OK, getArchive.StatusCode);
        Assert.Equal(archive, await getArchive.Content.ReadAsByteArrayAsync());

        using var catalog = JsonDocument.Parse(await _client.GetStringAsync("/api/packages"));
        Assert.Contains(catalog.RootElement.EnumerateArray(), item =>
            item.GetProperty("id").GetString() == packageId);

        using var details = JsonDocument.Parse(await _client.GetStringAsync($"/api/packages/{packageId}"));
        Assert.Equal(packageId, details.RootElement.GetProperty("id").GetString());
        Assert.Equal(version, details.RootElement.GetProperty("latestVersion").GetString());
        Assert.Equal("Container test package", details.RootElement.GetProperty("description").GetString());
        Assert.Equal("cxpm integration tests", details.RootElement.GetProperty("author").GetString());
    }

    private static byte[] CreatePackageArchive(string packageId, string version)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("package.cxpm");
            using var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false));
            writer.Write($"name: {packageId}\nversion: {version}\ndescription: Container test package\nauthor: cxpm integration tests\ndependencies: []\n");
        }
        return buffer.ToArray();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Cxpm.slnx"))
                && File.Exists(Path.Combine(directory.FullName, "Cxpm.Web", "Dockerfile")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate Cxpm.slnx from the test output directory.");
    }

    private static async Task RunDockerAsync(string workingDirectory, params string[] arguments)
    {
        await RunDockerAsync(workingDirectory, arguments, allowFailure: false);
    }

    private static async Task RunDockerAsync(string workingDirectory, string[] arguments, bool allowFailure)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Docker CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(timeout.Token);
        var output = $"{await stdout}{await stderr}";
        if (!allowFailure && process.ExitCode != 0)
            throw new InvalidOperationException($"Docker CLI exited with {process.ExitCode}: {output}");
    }
}
