using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net.Http.Headers;

namespace Cxpm.Web;

/// <summary>
/// Single-instance filesystem adapter for local development and simple hosting.
/// Cloud adapters should implement the same conditional object operations using
/// their storage service's native preconditions.
/// </summary>
public sealed class FileSystemPackageObjectStorage : IPackageObjectStorage
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;

    public FileSystemPackageObjectStorage(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configuredPath = configuration["RepositoryStorage:Path"];
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "packages")
            : configuredPath, environment.ContentRootPath);
        Directory.CreateDirectory(_root);
    }

    public Task<IReadOnlyList<string>> ListPackageIdsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> packageIds = Directory.EnumerateDirectories(_root)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult(packageIds);
    }

    public async Task<StoredObject?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = GetPath(key);
        if (!File.Exists(path))
            return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new StoredObject(bytes, MakeETag(bytes));
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(key);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(GetPath(key)));
    }

    public async Task<bool> CreateIfAbsentAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        var destination = GetPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await content.CopyToAsync(output, cancellationToken);

            try
            {
                File.Move(temporary, destination, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(destination))
            {
                return false;
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public async Task<ConditionalObjectWriteResult> WriteConditionallyAsync(string key, byte[] content,
        IReadOnlyCollection<string> expectedETags, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        var path = GetPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var gate = WriteLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var exists = File.Exists(path);
            if (ifNoneMatch && exists)
                return new ConditionalObjectWriteResult(false, null);
            if (!ifNoneMatch)
            {
                if (!exists || expectedETags.Count == 0)
                    return new ConditionalObjectWriteResult(false, null);
                var currentBytes = await File.ReadAllBytesAsync(path, cancellationToken);
                var currentETag = MakeETag(currentBytes);
                if (!expectedETags.Any(candidate => candidate == currentETag.ToString()))
                    return new ConditionalObjectWriteResult(false, currentETag);
            }

            await WriteAtomicallyAsync(path, content, cancellationToken);
            return new ConditionalObjectWriteResult(true, MakeETag(content));
        }
        finally
        {
            gate.Release();
        }
    }

    private string GetPath(string key)
    {
        var segments = key.Split('/');
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\\') || Path.IsPathRooted(key)
            || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.Contains(':')))
            throw new ArgumentException("Storage keys must be normalized relative paths.", nameof(key));

        var path = Path.GetFullPath(Path.Combine(_root,
            string.Join(Path.DirectorySeparatorChar, segments)));
        var rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(rootPrefix, comparison))
            throw new ArgumentException("Storage key escapes the configured storage root.", nameof(key));
        return path;
    }

    private static EntityTagHeaderValue MakeETag(byte[] bytes) =>
        new($"\"{Convert.ToBase64String(SHA256.HashData(bytes))}\"");

    private static async Task WriteAtomicallyAsync(string destination, byte[] contents, CancellationToken cancellationToken)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, contents, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
