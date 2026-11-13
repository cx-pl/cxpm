using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cxpm.Core.Models;

namespace Cxpm.Core;

public sealed partial class PackageManager
{
    private static readonly HttpClient RepositoryHttpClient = new();

    internal interface IPackageRepository
    {
        string Location { get; }
        void EnsureExists();
        List<SemVersion> GetVersions(string packageId);
        string GetPackageArchive(string packageId, SemVersion version);
        string Publish(string archivePath, string displayId, string normalizedId, string version);
    }

    internal sealed class FolderPackageRepository(string path) : IPackageRepository
    {
        public string Location { get; } = Path.GetFullPath(path);

        public void EnsureExists()
        {
            if (!Directory.Exists(Location))
                throw new CxpmException($"Repository folder '{Location}' does not exist.");
        }

        public List<SemVersion> GetVersions(string packageId)
        {
            var packageRoot = Path.Combine(Location, packageId.ToLowerInvariant());
            if (!Directory.Exists(packageRoot))
                return [];
            var result = new List<SemVersion>();
            foreach (var versionDirectory in Directory.EnumerateDirectories(packageRoot))
            {
                var versionName = Path.GetFileName(versionDirectory);
                if (!SemVersion.TryParse(versionName, out var version))
                    continue;
                if (Directory.EnumerateFiles(versionDirectory, "*.zip").Any())
                    result.Add(version);
            }
            return result;
        }

        public string GetPackageArchive(string packageId, SemVersion version)
        {
            var versionDirectory = Path.Combine(Location, packageId.ToLowerInvariant(), version.ToString());
            if (!Directory.Exists(versionDirectory))
                throw new CxpmException($"Package '{packageId}@{version}' is missing from repository '{Location}'.");
            return Directory.EnumerateFiles(versionDirectory, "*.zip")
                .OrderBy(candidate => candidate, StringComparer.Ordinal).FirstOrDefault()
                ?? throw new CxpmException($"Package '{packageId}@{version}' has no ZIP archive in repository '{Location}'.");
        }

        public string Publish(string archivePath, string displayId, string normalizedId, string version)
        {
            var destinationDirectory = Path.Combine(Location, normalizedId, version);
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(archivePath));
            if (File.Exists(destination))
                throw new CxpmException($"Package '{displayId}@{version}' is already published; package versions are immutable.");
            File.Copy(archivePath, destination);
            return destination;
        }
    }

    internal sealed class MultiPackageRepository(IReadOnlyList<IPackageRepository> feeds) : IPackageRepository
    {
        private readonly Dictionary<(string PackageId, string Version), IPackageRepository> _sources = [];

        public string Location => string.Join("; ", feeds.Select(feed => feed.Location));

        public void EnsureExists()
        {
            foreach (var feed in feeds)
                feed.EnsureExists();
        }

        public List<SemVersion> GetVersions(string packageId)
        {
            var versions = new List<SemVersion>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var feed in feeds)
            {
                foreach (var version in feed.GetVersions(packageId))
                {
                    var key = version.ToString();
                    if (!seen.Add(key))
                        continue;
                    versions.Add(version);
                    _sources[(packageId.ToLowerInvariant(), key)] = feed;
                }
            }
            return versions;
        }

        public string GetPackageArchive(string packageId, SemVersion version)
        {
            var key = (packageId.ToLowerInvariant(), version.ToString());
            if (_sources.TryGetValue(key, out var source))
                return source.GetPackageArchive(packageId, version);

            foreach (var feed in feeds)
            {
                if (!feed.GetVersions(packageId).Any(candidate => candidate.Equals(version)))
                    continue;
                _sources[key] = feed;
                return feed.GetPackageArchive(packageId, version);
            }

            throw new CxpmException($"Package '{packageId}@{version}' was not found in any configured feed: {Location}.");
        }

        public string Publish(string archivePath, string displayId, string normalizedId, string version) =>
            throw new CxpmException("Publishing requires one destination feed. Pass --feed <path-or-url> to select it.");
    }

    internal sealed class HttpPackageRepository(Uri baseUri, HttpClient httpClient, string? cacheDirectory = null)
        : IPackageRepository
    {
        private readonly Uri _baseUri = new(baseUri.AbsoluteUri.TrimEnd('/') + "/");
        private readonly HttpClient _httpClient = httpClient;
        private readonly string _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cxpm", "http-cache",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseUri.AbsoluteUri.TrimEnd('/')))).ToLowerInvariant());

        public string Location => _baseUri.AbsoluteUri.TrimEnd('/');

        public void EnsureExists()
        {
            // Static feeds have no root index; availability is checked on package lookup.
        }

        public List<SemVersion> GetVersions(string packageId)
        {
            try
            {
                var indexResponse = Send(new HttpRequestMessage(HttpMethod.Get, GetIndexUri(packageId)));
                using (indexResponse)
                {
                    if (indexResponse.StatusCode == HttpStatusCode.NotFound)
                        return [];
                    EnsureSuccess(indexResponse);
                    var index = JsonSerializer.Deserialize<PackageVersionIndex>(indexResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult())
                        ?? throw new CxpmException($"Package index for '{packageId}' at '{Location}' is empty.");
                    if (index.Versions is null)
                        throw new CxpmException($"Package index for '{packageId}' at '{Location}' has no 'versions' list.");

                    var result = new List<SemVersion>();
                    foreach (var text in index.Versions.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!SemVersion.TryParse(text, out var version))
                            continue;
                        result.Add(version);
                    }
                    return result;
                }
            }
            catch (JsonException exception)
            {
                throw new CxpmException($"Package index for '{packageId}' at '{Location}' is invalid: {exception.Message}");
            }
        }

        public string GetPackageArchive(string packageId, SemVersion version) =>
            DownloadPackage(packageId, version);

        public string Publish(string archivePath, string displayId, string normalizedId, string version)
        {
            var versionValue = SemVersion.Parse(version);
            var packageUri = GetPackageUri(normalizedId, versionValue);
            var initialIndex = ReadVersionIndex(normalizedId);
            if (initialIndex.Versions.Contains(version, StringComparer.OrdinalIgnoreCase))
            {
                VerifyExistingPackage(packageUri, archivePath, displayId, version);
                return packageUri.AbsoluteUri;
            }
            if (initialIndex.Exists && initialIndex.ETag is null)
                throw new CxpmException($"Repository '{Location}' must return an ETag for existing package indexes to publish safely.");

            using (var request = CreateRequest(HttpMethod.Put, packageUri))
            {
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                request.Content = new StreamContent(File.OpenRead(archivePath));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                using var response = Send(request);
                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                    VerifyExistingPackage(packageUri, archivePath, displayId, version);
                else
                    EnsureSuccess(response);
            }

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var (versions, etag, indexExists) = ReadVersionIndex(normalizedId);
                if (versions.Contains(version, StringComparer.OrdinalIgnoreCase))
                    return packageUri.AbsoluteUri;
                versions.Add(version);
                versions = versions.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(item => SemVersion.TryParse(item, out _))
                    .OrderBy(item => SemVersion.Parse(item)).Select(item => SemVersion.Parse(item).ToString()).ToList();

                using var request = CreateRequest(HttpMethod.Put, GetIndexUri(normalizedId));
                if (!indexExists)
                    request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                else if (etag is not null)
                    request.Headers.IfMatch.Add(etag);
                else
                    throw new CxpmException($"Repository '{Location}' must return an ETag for existing package indexes to publish safely.");
                request.Content = JsonContent.Create(new PackageVersionIndex { Versions = versions });
                using var response = Send(request);
                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                    continue;
                EnsureSuccess(response);
                return packageUri.AbsoluteUri;
            }
            throw new CxpmException($"Could not update the package index for '{normalizedId}' at '{Location}' because it changed during publication.");
        }

        private void VerifyExistingPackage(Uri packageUri, string archivePath, string displayId, string version)
        {
            using var request = CreateRequest(HttpMethod.Get, packageUri);
            using var response = Send(request);
            EnsureSuccess(response);
            using var content = response.Content.ReadAsStream();
            var existingHash = Convert.ToHexString(SHA256.HashData(content));
            var requestedHash = HashFile(archivePath);
            if (!existingHash.Equals(requestedHash, StringComparison.OrdinalIgnoreCase))
                throw new CxpmException($"Package '{displayId}@{version}' is already published with different content; package versions are immutable.");
        }

        private (List<string> Versions, EntityTagHeaderValue? ETag, bool Exists) ReadVersionIndex(string packageId)
        {
            using var response = Send(new HttpRequestMessage(HttpMethod.Get, GetIndexUri(packageId)));
            if (response.StatusCode == HttpStatusCode.NotFound)
                return ([], null, false);
            EnsureSuccess(response);
            PackageVersionIndex index;
            try
            {
                index = JsonSerializer.Deserialize<PackageVersionIndex>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
                    ?? throw new CxpmException($"Package index for '{packageId}' at '{Location}' is empty.");
            }
            catch (JsonException exception)
            {
                throw new CxpmException($"Package index for '{packageId}' at '{Location}' is invalid: {exception.Message}");
            }
            return (index.Versions ?? throw new CxpmException($"Package index for '{packageId}' at '{Location}' has no 'versions' list."),
                response.Headers.ETag, true);
        }

        private string DownloadPackage(string packageId, SemVersion version)
        {
            var normalizedId = packageId.ToLowerInvariant();
            var fileName = $"{normalizedId}.{version}.zip";
            var destinationDirectory = Path.Combine(_cacheDirectory, normalizedId, version.ToString());
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(destinationDirectory, fileName);
            if (File.Exists(destination))
                return destination;

            using var response = Send(new HttpRequestMessage(HttpMethod.Get, GetPackageUri(normalizedId, version)));
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength > MaxCompressedArchiveBytes)
                throw new CxpmException($"Package archive from '{Location}' exceeds the {MaxCompressedArchiveBytes / (1024 * 1024)} MiB download limit.");
            var temporaryPath = Path.Combine(destinationDirectory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                using var input = response.Content.ReadAsStream();
                using (var output = File.Create(temporaryPath))
                    CopyStreamWithLimit(input, output, MaxCompressedArchiveBytes,
                        $"Package archive from '{Location}'");
                File.Move(temporaryPath, destination, overwrite: true);
                return destination;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        private Uri GetIndexUri(string packageId) => new(_baseUri,
            $"{Uri.EscapeDataString(packageId.ToLowerInvariant())}/index.json");

        private Uri GetPackageUri(string packageId, SemVersion version) => new(_baseUri,
            $"{Uri.EscapeDataString(packageId.ToLowerInvariant())}/{Uri.EscapeDataString(version.ToString())}/{Uri.EscapeDataString(packageId.ToLowerInvariant())}.{Uri.EscapeDataString(version.ToString())}.zip");

        private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
        {
            var request = new HttpRequestMessage(method, uri);
            var token = Environment.GetEnvironmentVariable("CXPM_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            return request;
        }

        private HttpResponseMessage Send(HttpRequestMessage request)
        {
            try
            {
                return _httpClient.Send(request);
            }
            catch (HttpRequestException exception)
            {
                throw new CxpmException($"Could not contact repository '{Location}': {exception.Message}");
            }
            catch (TaskCanceledException exception)
            {
                throw new CxpmException($"Request to repository '{Location}' timed out: {exception.Message}");
            }
        }

        private static void EnsureSuccess(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
                throw new CxpmException($"Repository request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) for '{response.RequestMessage?.RequestUri}'.");
        }

        private sealed class PackageVersionIndex
        {
            [JsonPropertyName("versions")]
            public List<string>? Versions { get; set; }
        }
    }

    private static void CopyStreamWithLimit(Stream input, Stream output, long limit, string description)
    {
        var buffer = new byte[81920];
        long copied = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
                return;
            copied += read;
            if (copied > limit)
                throw new CxpmException($"{description} exceeds the {limit / (1024 * 1024)} MiB download limit.");
            output.Write(buffer, 0, read);
        }
    }

    private static IPackageRepository CreatePackageRepository(string location)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
            return new HttpPackageRepository(uri, RepositoryHttpClient);
        return new FolderPackageRepository(location);
    }

    private static IPackageRepository CreatePackageRepository(IReadOnlyList<string> feedPaths) =>
        feedPaths.Count == 1
            ? CreatePackageRepository(feedPaths[0])
            : new MultiPackageRepository(feedPaths.Select(CreatePackageRepository).ToArray());

    private static string NormalizeRepositoryLocation(string location, string? baseDirectory = null)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
                throw new CxpmException("Repository URLs cannot contain credentials, a query, or a fragment. Set CXPM_TOKEN for bearer authentication.");
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }
        if (location.Contains("://", StringComparison.Ordinal))
            throw new CxpmException("Repository URLs must use HTTP or HTTPS.");
        return baseDirectory is null
            ? Path.GetFullPath(location)
            : Path.GetFullPath(location, baseDirectory);
    }
}
