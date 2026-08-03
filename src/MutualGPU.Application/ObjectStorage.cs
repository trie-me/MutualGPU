using MutualGPU.Domain;

namespace MutualGPU.Application;

/// <summary>A bucket-independent key. Validation prevents path traversal and accidental bucket-wide operations.</summary>
public readonly record struct ObjectKey
{
    public ObjectKey(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || value.StartsWith("/", StringComparison.Ordinal) || value.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("An object key must be a relative, traversal-free key.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ObjectPrefix
{
    public ObjectPrefix(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || value.StartsWith("/", StringComparison.Ordinal) || value.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("An object prefix must be relative and traversal-free.", nameof(value));
        }

        Value = value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record ObjectWriteConditions(string? ExpectedETag = null, bool MustNotExist = false)
{
    public static ObjectWriteConditions None { get; } = new();

    public static ObjectWriteConditions IfNotExists { get; } = new(MustNotExist: true);
}

public sealed record ObjectEntry(ObjectKey Key, long Length, DateTimeOffset LastModified, string? ETag);

public sealed class ObjectRead : IAsyncDisposable
{
    private readonly IAsyncDisposable? owner;

    public ObjectRead(Stream content, long length, string? contentType, string? eTag, IAsyncDisposable? owner = null)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Length = length;
        ContentType = contentType;
        ETag = eTag;
        this.owner = owner;
    }

    public Stream Content { get; }

    public long Length { get; }

    public string? ContentType { get; }

    public string? ETag { get; }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        if (owner is not null)
        {
            await owner.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public interface IObjectStore
{
    Task<ObjectRead?> GetAsync(ObjectKey key, CancellationToken cancellationToken);

    Task PutAsync(ObjectKey key, Stream content, ObjectWriteConditions conditions, CancellationToken cancellationToken);

    Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken);

    IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, CancellationToken cancellationToken);

    Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken);
}

/// <summary>Readiness probe for the configured object store; it must not create mutable data.</summary>
public interface IObjectStoreHealth
{
    Task CheckHealthAsync(CancellationToken cancellationToken);
}

/// <summary>Optional native write result. ETags remain provider-issued opaque values.</summary>
public sealed record ObjectWriteReceipt(string? ETag);

public interface IObjectStoreWriteReceipts
{
    Task<ObjectWriteReceipt> PutWithReceiptAsync(
        ObjectKey key,
        Stream content,
        ObjectWriteConditions conditions,
        CancellationToken cancellationToken);
}

public sealed record ObjectStoreTarget(
    string Id,
    IObjectStore Store,
    IObjectStoreHealth Health,
    IBrowserObjectCorsPolicy BrowserCors);

/// <summary>
/// Resolves only explicitly configured storage targets. It never substitutes a
/// different provider when a persisted target is unavailable.
/// </summary>
public interface IObjectStoreRegistry
{
    string WriteTargetId { get; }

    ObjectStoreTarget GetRequired(string storageTargetId);

    /// <summary>Checks configured target identity without creating a provider client.</summary>
    bool IsConfigured(string storageTargetId);
}

/// <summary>Resolves an authorized logical artifact to its stored location.</summary>
public interface IArtifactDownloadUrlResolver
{
    Task<Uri> CreateDownloadUrlAsync(
        TaskId taskId,
        ArtifactId artifactId,
        ObjectKey legacyKey,
        TimeSpan lifetime,
        CancellationToken cancellationToken);
}
