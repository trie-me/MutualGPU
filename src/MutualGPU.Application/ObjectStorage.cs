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
