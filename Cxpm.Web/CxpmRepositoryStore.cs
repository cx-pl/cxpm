using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cxpm.Core.Models;

namespace Cxpm.Web;

public sealed class CxpmRepositoryStore
{
    private const string ManifestName = "package.cxpm";
    private const long MaxArchiveSize = 512L * 1024 * 1024;
    private const int MaxArchiveEntryCount = 100_000;
    private const int MaxArchivePathLength = 1024;
    private const long MaxUncompressedEntrySize = 512L * 1024 * 1024;
    private const long MaxUncompressedPackageSize = 2L * 1024 * 1024 * 1024;
    private const int MaxIndexSize = 1024 * 1024;
    private static readonly Regex PackageIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IPackageObjectStorage _storage;

    public CxpmRepositoryStore(IPackageObjectStorage storage) => _storage = storage;

    public bool IsWriteAuthorizationConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CXPM_TOKEN"));

    public bool IsWriteAuthorized(string? authorization)
    {
        var token = Environment.GetEnvironmentVariable("CXPM_TOKEN");
        if (string.IsNullOrWhiteSpace(token)
            || !AuthenticationHeaderValue.TryParse(authorization, out var header)
            || !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(header.Parameter))
            return false;

        var expected = Encoding.UTF8.GetBytes(token.Trim());
        var supplied = Encoding.UTF8.GetBytes(header.Parameter);
        return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    public async Task<IReadOnlyList<RepositoryPackageSummary>> ListPackagesAsync(CancellationToken cancellationToken)
    {
        var packageIds = await _storage.ListPackageIdsAsync(cancellationToken);
        var packages = new List<RepositoryPackageSummary>();
        foreach (var packageId in packageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = await ReadIndexAsync(packageId, cancellationToken);
            if (index is { Versions.Count: > 0 })
                packages.Add(new RepositoryPackageSummary(packageId, index.Versions));
        }
        return packages;
    }

    public async Task<StoredPackageIndex?> GetIndexAsync(string packageId, CancellationToken cancellationToken)
    {
        var stored = await _storage.ReadAsync(GetIndexKey(packageId), cancellationToken);
        if (stored is null)
            return null;

        var index = DeserializeAndValidateIndex(stored.Content, packageId);
        return new StoredPackageIndex(stored.Content, stored.ETag, index.Versions);
    }

    public async Task<RepositoryPackageDetails?> GetPackageDetailsAsync(string packageId, CancellationToken cancellationToken)
    {
        var normalizedId = NormalizePackageId(packageId);
        var index = await ReadIndexAsync(normalizedId, cancellationToken);
        if (index is not { Versions.Count: > 0 })
            return null;

        var latestVersion = index.Versions[^1];
        var archiveStream = await _storage.OpenReadAsync(GetArchiveKey(normalizedId, latestVersion), cancellationToken);
        if (archiveStream is null)
            return null;

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"cxpm-{Guid.NewGuid():N}.zip");
        try
        {
            await using (archiveStream)
                await CopyWithLimitAsync(archiveStream, temporaryPath, cancellationToken);
            using var archive = ZipFile.OpenRead(temporaryPath);
            var manifestEntry = archive.GetEntry(ManifestName);
            if (manifestEntry is null || manifestEntry.Length > 1024 * 1024)
                throw new RepositoryRequestException(StatusCodes.Status500InternalServerError,
                    $"The package manifest for '{normalizedId}@{latestVersion}' is unavailable or invalid.");

            var project = CxProject.Parse(ReadManifest(manifestEntry), ManifestName);
            return new RepositoryPackageDetails(project.Name, project.Version, index.Versions,
                project.Description, project.Author, project.Licence, project.Website, project.Dependencies);
        }
        catch (InvalidDataException exception)
        {
            throw new RepositoryRequestException(StatusCodes.Status500InternalServerError,
                $"The package archive for '{normalizedId}@{latestVersion}' is invalid: {exception.Message}");
        }
        catch (CxpmException exception)
        {
            throw new RepositoryRequestException(StatusCodes.Status500InternalServerError, exception.Message);
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            throw new RepositoryRequestException(StatusCodes.Status500InternalServerError,
                $"The package manifest is invalid YAML: {exception.Message}");
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<Stream?> OpenArchiveAsync(string packageId, string version, string archiveName,
        CancellationToken cancellationToken)
    {
        var parsedVersion = ParseVersion(version);
        var normalizedId = NormalizePackageId(packageId);
        var expectedName = $"{normalizedId}.{parsedVersion}.zip";
        if (!archiveName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            throw new RepositoryRequestException(StatusCodes.Status404NotFound, "Package archive was not found.");

        return await _storage.OpenReadAsync(GetArchiveKey(normalizedId, parsedVersion.ToString()), cancellationToken);
    }

    public async Task StoreArchiveAsync(string packageId, string version, string archiveName, Stream content,
        long? contentLength, bool hasIfNoneMatch, CancellationToken cancellationToken)
    {
        var normalizedId = NormalizePackageId(packageId);
        var parsedVersion = ParseVersion(version);
        var canonicalVersion = parsedVersion.ToString();
        if (!version.Equals(canonicalVersion, StringComparison.Ordinal)
            || !archiveName.Equals($"{normalizedId}.{canonicalVersion}.zip", StringComparison.OrdinalIgnoreCase))
            throw new RepositoryRequestException(StatusCodes.Status404NotFound, "Package route is invalid.");
        if (!hasIfNoneMatch)
            throw new RepositoryRequestException(StatusCodes.Status428PreconditionRequired,
                "Package uploads must include If-None-Match: * so published versions remain immutable.");
        if (contentLength > MaxArchiveSize)
            throw new RepositoryRequestException(StatusCodes.Status413PayloadTooLarge, "Package archive exceeds the 512 MiB upload limit.");

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"cxpm-{Guid.NewGuid():N}.upload");
        try
        {
            await CopyWithLimitAsync(content, temporaryPath, cancellationToken);
            ValidateArchive(temporaryPath, normalizedId, canonicalVersion);
            await using var validatedArchive = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!await _storage.CreateIfAbsentAsync(GetArchiveKey(normalizedId, canonicalVersion),
                    validatedArchive, cancellationToken))
                throw new RepositoryRequestException(StatusCodes.Status412PreconditionFailed,
                    "Package version already exists and package versions are immutable.");
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<StoredPackageIndex> StoreIndexAsync(string packageId, IReadOnlyCollection<string> versions,
        string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        var normalizedId = NormalizePackageId(packageId);
        var validatedVersions = NormalizeVersions(versions);
        if (!ifNoneMatch && string.IsNullOrWhiteSpace(ifMatch))
            throw new RepositoryRequestException(StatusCodes.Status428PreconditionRequired,
                "Package index updates must include If-Match or If-None-Match: *.");
        var indexBytes = JsonSerializer.SerializeToUtf8Bytes(new PackageVersionIndex(validatedVersions), JsonOptions);
        var indexKey = GetIndexKey(normalizedId);
        var existingObject = await _storage.ReadAsync(indexKey, cancellationToken);
        if (ifNoneMatch)
        {
            if (existingObject is not null)
                throw new RepositoryRequestException(StatusCodes.Status412PreconditionFailed, "Package index already exists.");
        }
        else
        {
            if (existingObject is null || string.IsNullOrWhiteSpace(ifMatch))
                throw new RepositoryRequestException(StatusCodes.Status412PreconditionFailed,
                    "Package index changed since it was read.");
            var expectedETags = ifMatch.Split(',').Select(candidate => candidate.Trim()).ToArray();
            if (!expectedETags.Contains(existingObject.ETag.ToString(), StringComparer.Ordinal))
                throw new RepositoryRequestException(StatusCodes.Status412PreconditionFailed,
                    "Package index changed since it was read.");

            var currentIndex = DeserializeAndValidateIndex(existingObject.Content, normalizedId);
            if (currentIndex.Versions is null
                || currentIndex.Versions.Except(validatedVersions, StringComparer.OrdinalIgnoreCase).Any())
                throw new RepositoryRequestException(StatusCodes.Status400BadRequest,
                    "Published package versions cannot be removed from the package index.");
        }

        foreach (var version in validatedVersions)
            if (!await _storage.ExistsAsync(GetArchiveKey(normalizedId, version), cancellationToken))
                throw new RepositoryRequestException(StatusCodes.Status409Conflict,
                    $"Cannot list version '{version}' because its package archive has not been uploaded.");

        var etags = ifNoneMatch ? Array.Empty<string>() : ifMatch!.Split(',').Select(candidate => candidate.Trim()).ToArray();
        var result = await _storage.WriteConditionallyAsync(indexKey, indexBytes, etags, ifNoneMatch, cancellationToken);
        if (!result.Written)
            throw new RepositoryRequestException(StatusCodes.Status412PreconditionFailed,
                ifNoneMatch ? "Package index already exists." : "Package index changed since it was read.");
        return new StoredPackageIndex(indexBytes, result.ETag!, validatedVersions);
    }

    private async Task<PackageVersionIndex?> ReadIndexAsync(string packageId, CancellationToken cancellationToken)
    {
        var stored = await _storage.ReadAsync(GetIndexKey(packageId), cancellationToken);
        return stored is null ? null : DeserializeAndValidateIndex(stored.Content, packageId);
    }

    private PackageVersionIndex DeserializeAndValidateIndex(byte[] bytes, string packageId)
    {
        try
        {
            var index = JsonSerializer.Deserialize<PackageVersionIndex>(bytes, JsonOptions)
                ?? throw new RepositoryRequestException(StatusCodes.Status500InternalServerError,
                    $"The stored index for '{packageId}' is empty.");
            var normalizedVersions = NormalizeVersions(index.Versions);
            return new PackageVersionIndex(normalizedVersions);
        }
        catch (JsonException exception)
        {
            throw new RepositoryRequestException(StatusCodes.Status500InternalServerError,
                $"The stored index for '{packageId}' is invalid: {exception.Message}");
        }
    }

    private static List<string> NormalizeVersions(IEnumerable<string>? versions)
    {
        if (versions is null)
            throw new RepositoryRequestException(StatusCodes.Status400BadRequest, "Index must contain a 'versions' array.");
        var normalized = new List<(string Text, SemVersion Version)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in versions)
        {
            if (normalized.Count >= 10_000)
                throw new RepositoryRequestException(StatusCodes.Status400BadRequest, "A package index cannot contain more than 10,000 versions.");
            if (string.IsNullOrWhiteSpace(text) || text.Length > 256)
                throw new RepositoryRequestException(StatusCodes.Status400BadRequest, "Package versions must be between 1 and 256 characters.");
            if (!SemVersion.TryParse(text, out var version))
                throw new RepositoryRequestException(StatusCodes.Status400BadRequest, $"'{text}' is not a valid package version.");
            var canonical = version.ToString();
            if (!seen.Add(canonical))
                throw new RepositoryRequestException(StatusCodes.Status400BadRequest, $"Package version '{canonical}' is duplicated in the index.");
            normalized.Add((canonical, version));
        }
        if (normalized.Count == 0)
            throw new RepositoryRequestException(StatusCodes.Status400BadRequest, "Index must list at least one package version.");
        return normalized.OrderBy(item => item.Version).Select(item => item.Text).ToList();
    }

    private static string GetIndexKey(string packageId) => $"{NormalizePackageId(packageId)}/index.json";

    private static string GetArchiveKey(string packageId, string version) =>
        $"{NormalizePackageId(packageId)}/{version}/{NormalizePackageId(packageId)}.{version}.zip";

    private static string NormalizePackageId(string packageId)
    {
        var baseName = packageId?.Split('.')[0];
        var reservedWindowsName = baseName is not null
            && Regex.IsMatch(baseName, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (string.IsNullOrWhiteSpace(packageId) || packageId.Length > 100
            || packageId is "." or ".." || packageId.EndsWith(".", StringComparison.Ordinal)
            || reservedWindowsName || !PackageIdPattern.IsMatch(packageId))
            throw new RepositoryRequestException(StatusCodes.Status400BadRequest,
                "Package IDs must be 1–100 characters, start with a letter or digit, and may contain '.', '_' and '-'. Reserved path names are not allowed.");
        return packageId.ToLowerInvariant();
    }

    private static SemVersion ParseVersion(string version)
    {
        if (!SemVersion.TryParse(version, out var parsed))
            throw new RepositoryRequestException(StatusCodes.Status400BadRequest, $"'{version}' is not a valid SemVer package version.");
        return parsed;
    }

    private static async Task CopyWithLimitAsync(Stream source, string destination, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            total += read;
            if (total > MaxArchiveSize)
                throw new RepositoryRequestException(StatusCodes.Status413PayloadTooLarge,
                    "Package archive exceeds the 512 MiB upload limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total == 0)
            throw new RepositoryRequestException(StatusCodes.Status400BadRequest, "Package archive is empty.");
    }

    private static void ValidateArchive(string path, string expectedId, string expectedVersion)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > MaxArchiveEntryCount)
                throw new RepositoryRequestException(StatusCodes.Status400BadRequest,
                    $"Package archive contains more than {MaxArchiveEntryCount} entries.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry? manifestEntry = null;
            long declaredUncompressedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                ValidateEntryPath(entry, names);
                declaredUncompressedBytes = checked(declaredUncompressedBytes + entry.Length);
                if (declaredUncompressedBytes > MaxUncompressedPackageSize)
                    throw new RepositoryRequestException(StatusCodes.Status400BadRequest,
                        "Package archive expands beyond the 2048 MiB package limit.");
                if (entry.FullName.Equals(ManifestName, StringComparison.Ordinal))
                {
                    if (manifestEntry is not null)
                        throw new RepositoryRequestException(StatusCodes.Status400BadRequest, "Package archive contains multiple manifests.");
                    if (entry.Length > 1024 * 1024)
                        throw new RepositoryRequestException(StatusCodes.Status400BadRequest, "Package manifest exceeds the 1 MiB limit.");
                    manifestEntry = entry;
                }
            }
            if (manifestEntry is null)
                throw new RepositoryRequestException(StatusCodes.Status400BadRequest, "Package archive does not contain package.cxpm.");

            var project = CxProject.Parse(ReadManifest(manifestEntry), ManifestName);
            var projectId = NormalizePackageId(project.Name);
            var projectVersion = ParseVersion(project.Version).ToString();
            if (!projectId.Equals(expectedId, StringComparison.Ordinal)
                || !projectVersion.Equals(expectedVersion, StringComparison.Ordinal))
                throw new RepositoryRequestException(StatusCodes.Status400BadRequest,
                    $"Package manifest identity '{project.Name}@{project.Version}' does not match upload route '{expectedId}@{expectedVersion}'.");
        }
        catch (InvalidDataException exception)
        {
            throw new RepositoryRequestException(StatusCodes.Status400BadRequest, $"Package archive is not a valid ZIP file: {exception.Message}");
        }
        catch (CxpmException exception)
        {
            throw new RepositoryRequestException(StatusCodes.Status400BadRequest, exception.Message);
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            throw new RepositoryRequestException(StatusCodes.Status400BadRequest, $"Package manifest is invalid YAML: {exception.Message}");
        }
    }

    private static void ValidateEntryPath(ZipArchiveEntry entry, ISet<string> names)
    {
        var name = entry.FullName;
        var path = name.EndsWith("/", StringComparison.Ordinal) ? name[..^1] : name;
        var segments = path.Split('/');
        if (string.IsNullOrWhiteSpace(path) || name.Length > MaxArchivePathLength
            || name.Contains('\\') || Path.IsPathRooted(name)
            || segments.Any(segment => segment.Length == 0 || segment.Length > 255
                || segment is "." or ".." || segment.Contains(':')))
            throw new RepositoryRequestException(StatusCodes.Status400BadRequest, $"Package archive contains an unsafe path '{name}'.");
        if (!names.Add(path))
            throw new RepositoryRequestException(StatusCodes.Status400BadRequest, $"Package archive contains duplicate path '{name}'.");
        if (entry.Length > MaxUncompressedEntrySize)
            throw new RepositoryRequestException(StatusCodes.Status400BadRequest,
                $"Package archive entry '{name}' exceeds the 512 MiB size limit.");
        if (name.EndsWith("/", StringComparison.Ordinal) && entry.Length != 0)
            throw new RepositoryRequestException(StatusCodes.Status400BadRequest, $"Package directory entry '{name}' contains data.");
        if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            throw new RepositoryRequestException(StatusCodes.Status400BadRequest, $"Package archive contains a symbolic link '{name}'.");
    }

    private static string ReadManifest(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var result = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0)
                return result.ToString();
            if (result.Length + read > 1024 * 1024)
                throw new RepositoryRequestException(StatusCodes.Status400BadRequest,
                    "Package manifest exceeds the 1 MiB limit.");
            result.Append(buffer, 0, read);
        }
    }

}

public sealed record RepositoryPackageSummary(string Id, IReadOnlyList<string> Versions);
public sealed record RepositoryPackageDetails(string Id, string LatestVersion, IReadOnlyList<string> Versions,
    string? Description, string? Author, string? Licence, string? Website, IReadOnlyList<string> Dependencies);
public sealed record StoredPackageIndex(byte[] Json, EntityTagHeaderValue ETag, IReadOnlyList<string>? Versions);
public sealed record PackageVersionIndex([property: JsonPropertyName("versions")] List<string>? Versions);
public sealed class RepositoryRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
