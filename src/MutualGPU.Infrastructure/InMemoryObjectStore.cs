using System.Collections.Concurrent;
using MutualGPU.Application;

namespace MutualGPU.Infrastructure;

/// <summary>
/// Development/test implementation of the object-store contract. Production registration
/// always uses the Backblaze adapter; this exists so the demo host and in-process tests
/// remain runnable without credentials.
/// </summary>
public sealed class InMemoryObjectStore : IObjectStore, IObjectStoreHealth
{
    private readonly ConcurrentDictionary<string, StoredObject> objects = new(StringComparer.Ordinal);

    public Task<ObjectRead?> GetAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!objects.TryGetValue(key.Value, out var stored)) return Task.FromResult<ObjectRead?>(null);
        var content = new MemoryStream(stored.Content, writable: false);
        return Task.FromResult<ObjectRead?>(new ObjectRead(content, stored.Content.LongLength, stored.ContentType, stored.ETag, content));
    }

    public async Task PutAsync(ObjectKey key, Stream content, ObjectWriteConditions conditions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(conditions);
        cancellationToken.ThrowIfCancellationRequested();
        if (conditions.MustNotExist && objects.ContainsKey(key.Value))
            throw new InvalidOperationException($"Object '{key.Value}' already exists.");
        if (conditions.ExpectedETag is { Length: > 0 } expected &&
            (!objects.TryGetValue(key.Value, out var existing) || !StringComparer.Ordinal.Equals(expected, existing.ETag)))
            throw new InvalidOperationException($"Object '{key.Value}' does not have the expected ETag.");
        await using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        var bytes = copy.ToArray();
        objects[key.Value] = new StoredObject(bytes, Guid.CreateVersion7().ToString("N"), null, DateTimeOffset.UtcNow);
    }

    public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        objects.TryRemove(key.Value, out _);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var pair in objects.Where(pair => pair.Key.StartsWith(prefix.Value, StringComparison.Ordinal)).OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ObjectEntry(new ObjectKey(pair.Key), pair.Value.Content.LongLength, pair.Value.LastModified, pair.Value.ETag);
            await Task.Yield();
        }
    }

    public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!objects.ContainsKey(key.Value)) throw new FileNotFoundException("The requested object does not exist.", key.Value);
        return Task.FromResult(new Uri($"https://example.invalid/mutualgpu/download/{Uri.EscapeDataString(key.Value)}?expires={DateTimeOffset.UtcNow.Add(lifetime):O}"));
    }

    public Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private sealed record StoredObject(byte[] Content, string ETag, string? ContentType, DateTimeOffset LastModified);
}
