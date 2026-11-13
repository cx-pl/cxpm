using System.Net.Http.Headers;

namespace Cxpm.Web;

/// <summary>
/// Object-level persistence required by the package feed. Implementations must
/// provide atomic create-if-absent and ETag-conditional replacement semantics.
/// </summary>
public interface IPackageObjectStorage
{
    Task<IReadOnlyList<string>> ListPackageIdsAsync(CancellationToken cancellationToken);
    Task<StoredObject?> ReadAsync(string key, CancellationToken cancellationToken);
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);
    Task<bool> CreateIfAbsentAsync(string key, Stream content, CancellationToken cancellationToken);
    Task<ConditionalObjectWriteResult> WriteConditionallyAsync(string key, byte[] content,
        IReadOnlyCollection<string> expectedETags, bool ifNoneMatch, CancellationToken cancellationToken);
}

public sealed record StoredObject(byte[] Content, EntityTagHeaderValue ETag);
public sealed record ConditionalObjectWriteResult(bool Written, EntityTagHeaderValue? ETag);
