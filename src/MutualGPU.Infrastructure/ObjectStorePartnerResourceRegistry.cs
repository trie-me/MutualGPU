using System.Collections.Concurrent;
using System.Text.Json;
using MutualGPU.Application;

namespace MutualGPU.Infrastructure;

/// <summary>
/// Stores one JSON document for each partner-origin request. Keeping the request
/// as an individual create-only file makes the audit trail durable without a
/// mutable index, while the approved-origin cache supplies the synchronous CORS check.
/// </summary>
public sealed class ObjectStorePartnerResourceRegistry(
    IObjectStore store,
    MutualGpuObjectKeys keys,
    TimeProvider timeProvider) : IPartnerResourceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, byte> approvedOrigins = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim reviewGate = new(1, 1);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await foreach (var entry in store.ListAsync(keys.PartnerResourceRequests(), cancellationToken).ConfigureAwait(false))
        {
            var request = await ReadAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            if (request is { ProcessedAt: not null, RevokedAt: null })
            {
                approvedOrigins.TryAdd(request.Origin, 0);
            }
        }
    }

    public async Task<PartnerResourceRequest> SubmitAsync(PartnerResourceSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var request = new PartnerResourceRequest(
            Guid.CreateVersion7(),
            submission.PartnerName,
            submission.ContactEmail,
            submission.Origin,
            timeProvider.GetUtcNow());
        await WriteAsync(keys.PartnerResourceRequest(request.Id), request, ObjectWriteConditions.IfNotExists, cancellationToken).ConfigureAwait(false);
        return request;
    }

    public async Task<IReadOnlyList<PartnerResourceRequest>> ListPendingAsync(CancellationToken cancellationToken)
    {
        var pending = new List<PartnerResourceRequest>();
        await foreach (var entry in store.ListAsync(keys.PartnerResourceRequests(), cancellationToken).ConfigureAwait(false))
        {
            var request = await ReadAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            if (request is { ProcessedAt: null })
            {
                pending.Add(request);
            }
        }

        return pending.OrderBy(static request => request.SubmittedAt).ToArray();
    }

    public async Task<IReadOnlyList<PartnerResourceRequest>> ListApprovedAsync(CancellationToken cancellationToken)
    {
        var approved = new List<PartnerResourceRequest>();
        await foreach (var entry in store.ListAsync(keys.PartnerResourceRequests(), cancellationToken).ConfigureAwait(false))
        {
            var request = await ReadAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            if (request is { ProcessedAt: not null, RevokedAt: null })
            {
                approved.Add(request);
            }
        }

        return approved.OrderByDescending(static request => request.ProcessedAt).ToArray();
    }

    public async Task<PartnerResourceRequest?> ApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) return null;
        await reviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (request, eTag) = await ReadForUpdateAsync(id, cancellationToken).ConfigureAwait(false);
            if (request is null) return null;
            if (request.RevokedAt is not null) return request;
            if (request.ProcessedAt is null)
            {
                request = request with { ProcessedAt = timeProvider.GetUtcNow() };
                await WriteAsync(keys.PartnerResourceRequest(id), request, new ObjectWriteConditions(ExpectedETag: eTag), cancellationToken).ConfigureAwait(false);
            }

            approvedOrigins.TryAdd(request.Origin, 0);
            return request;
        }
        finally
        {
            reviewGate.Release();
        }
    }

    public async Task<PartnerResourceRequest?> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) return null;
        await reviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (request, eTag) = await ReadForUpdateAsync(id, cancellationToken).ConfigureAwait(false);
            if (request is not { ProcessedAt: not null, RevokedAt: null }) return null;

            request = request with { RevokedAt = timeProvider.GetUtcNow() };
            await WriteAsync(keys.PartnerResourceRequest(id), request, new ObjectWriteConditions(ExpectedETag: eTag), cancellationToken).ConfigureAwait(false);
            approvedOrigins.TryRemove(request.Origin, out _);
            if ((await ListApprovedAsync(cancellationToken).ConfigureAwait(false)).Any(active =>
                StringComparer.Ordinal.Equals(active.Origin, request.Origin)))
            {
                approvedOrigins.TryAdd(request.Origin, 0);
            }
            return request;
        }
        finally
        {
            reviewGate.Release();
        }
    }

    public bool IsApprovedOrigin(string? origin) =>
        !String.IsNullOrWhiteSpace(origin) && approvedOrigins.ContainsKey(origin);

    private async Task<PartnerResourceRequest?> ReadAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        await using var read = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        return read is null
            ? null
            : await JsonSerializer.DeserializeAsync<PartnerResourceRequest>(read.Content, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(PartnerResourceRequest? Request, string? ETag)> ReadForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var read = await store.GetAsync(keys.PartnerResourceRequest(id), cancellationToken).ConfigureAwait(false);
        if (read is null) return (null, null);
        var request = await JsonSerializer.DeserializeAsync<PartnerResourceRequest>(read.Content, JsonOptions, cancellationToken).ConfigureAwait(false);
        return (request, read.ETag);
    }

    private async Task WriteAsync(ObjectKey key, PartnerResourceRequest request, ObjectWriteConditions conditions, CancellationToken cancellationToken)
    {
        await using var content = new MemoryStream();
        await JsonSerializer.SerializeAsync(content, request, JsonOptions, cancellationToken).ConfigureAwait(false);
        content.Position = 0;
        await store.PutAsync(key, content, conditions, cancellationToken).ConfigureAwait(false);
    }
}
