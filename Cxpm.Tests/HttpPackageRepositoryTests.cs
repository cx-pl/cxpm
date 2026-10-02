using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cxpm.Core;
using Xunit;

namespace Cxpm.Tests;

public sealed class HttpPackageRepositoryTests
{
    [Fact]
    public void VersionLookupDownloadsPackagesFromStaticFeedLayout()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-http-feed-test-").FullName;
        var packageBytes = Encoding.UTF8.GetBytes("test package archive");
        try
        {
            var handler = new StaticFeedHandler(packageBytes);
            var repository = new PackageManager.HttpPackageRepository(
                new Uri("https://feed.example/packages/"), new HttpClient(handler), root);

            var versions = repository.GetVersions("Example.Package");
            Assert.Equal("1.2.3", Assert.Single(versions).ToString());
            Assert.DoesNotContain(handler.Requests, request => request.EndsWith(".zip", StringComparison.Ordinal));
            var archivePath = repository.GetPackageArchive("Example.Package", versions[0]);

            Assert.Equal(packageBytes, File.ReadAllBytes(archivePath));
            Assert.Contains(handler.Requests, request => request == "GET /packages/example.package/index.json");
            Assert.Contains(handler.Requests, request => request == "GET /packages/example.package/1.2.3/example.package.1.2.3.zip");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PublishUploadsImmutableArchiveThenUpdatesVersionIndex()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-http-publish-test-").FullName;
        try
        {
            var archivePath = Path.Combine(root, "example.package.1.2.3.zip");
            File.WriteAllText(archivePath, "package bytes");
            var handler = new WritableFeedHandler();
            var repository = new PackageManager.HttpPackageRepository(
                new Uri("https://feed.example/packages"), new HttpClient(handler), Path.Combine(root, "cache"));

            var publishedUrl = repository.Publish(archivePath, "Example.Package", "example.package", "1.2.3");

            Assert.Equal("https://feed.example/packages/example.package/1.2.3/example.package.1.2.3.zip", publishedUrl);
            Assert.Equal(["1.2.3"], handler.PublishedVersions);
            Assert.Equal(publishedUrl, repository.Publish(archivePath, "Example.Package", "example.package", "1.2.3"));
            Assert.Contains(handler.Requests, request => request.StartsWith("PUT /packages/example.package/1.2.3/", StringComparison.Ordinal));
            Assert.Contains(handler.Requests, request => request == "PUT /packages/example.package/index.json");

            var secondArchivePath = Path.Combine(root, "example.package.2.0.0.zip");
            File.WriteAllText(secondArchivePath, "second package bytes");
            repository.Publish(secondArchivePath, "Example.Package", "example.package", "2.0.0");
            Assert.Equal(["1.2.3", "2.0.0"], handler.PublishedVersions);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StaticFeedHandler(byte[] packageBytes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}");
            if (path.EndsWith("/index.json", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"versions\":[\"1.2.3\",\"invalid\"]}");
            if (path.EndsWith("/example.package.1.2.3.zip", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(packageBytes) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class WritableFeedHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> PublishedVersions { get; private set; } = [];
        private readonly Dictionary<string, byte[]> _archives = new(StringComparer.Ordinal);

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}");
            if (request.Method == HttpMethod.Get && path.EndsWith("/index.json", StringComparison.Ordinal))
                return PublishedVersions.Count == 0
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : JsonWithEtag(JsonSerializer.Serialize(new { versions = PublishedVersions }));
            if (request.Method == HttpMethod.Put && path.EndsWith(".zip", StringComparison.Ordinal))
            {
                Assert.Contains(request.Headers.IfNoneMatch, tag => tag.Tag == "*");
                var bytes = request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
                return _archives.TryAdd(path, bytes)
                    ? new HttpResponseMessage(HttpStatusCode.Created)
                    : new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            }
            if (request.Method == HttpMethod.Get && path.EndsWith(".zip", StringComparison.Ordinal)
                && _archives.TryGetValue(path, out var archive))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
            if (request.Method == HttpMethod.Put && path.EndsWith("/index.json", StringComparison.Ordinal))
            {
                if (PublishedVersions.Count == 0)
                    Assert.Contains(request.Headers.IfNoneMatch, tag => tag.Tag == "*");
                else
                    Assert.Contains(request.Headers.IfMatch, tag => tag.Tag == "\"index-1\"");
                var body = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                PublishedVersions = JsonDocument.Parse(body).RootElement.GetProperty("versions")
                    .EnumerateArray().Select(item => item.GetString()!).ToList();
                var response = new HttpResponseMessage(HttpStatusCode.Created);
                response.Headers.ETag = new EntityTagHeaderValue("\"index-1\"");
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage JsonWithEtag(string body)
    {
        var response = Json(HttpStatusCode.OK, body);
        response.Headers.ETag = new EntityTagHeaderValue("\"index-1\"");
        return response;
    }
}
