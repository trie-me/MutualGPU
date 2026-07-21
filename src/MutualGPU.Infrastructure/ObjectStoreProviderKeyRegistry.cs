using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MutualGPU.Application;
using MutualGPU.Domain;

namespace MutualGPU.Infrastructure;

/// <summary>
/// Durable provider-key registry. A key's HMAC-addressed record is authoritative:
/// no raw key reaches Backblaze, process configuration, or object names.
/// </summary>
public sealed class ObjectStoreProviderKeyRegistry(IObjectStore store, MutualGpuObjectKeys keys) : IProvisionedExecutionUnitRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ExecutionUnitId?> AuthenticateAsync(string? presharedKey, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(presharedKey)) return null;
        var record = await ReadAsync(keys.ProviderKey(presharedKey), cancellationToken).ConfigureAwait(false);
        return record is { Active: true } ? record.ExecutionUnitId : null;
    }

    public async IAsyncEnumerable<ExecutionUnitId> ListExecutionUnitIdsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var ids = new HashSet<ExecutionUnitId>();
        await foreach (var entry in store.ListAsync(keys.ProviderKeys(), cancellationToken).ConfigureAwait(false))
        {
            if (!entry.Key.Value.EndsWith(".json", StringComparison.Ordinal)) continue;
            var record = await ReadAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            if (record is { Active: true } && ids.Add(record.ExecutionUnitId)) yield return record.ExecutionUnitId;
        }
    }

    /// <summary>Creates an active provider key exactly once. The caller receives the raw key only at issuance time.</summary>
    public async Task ProvisionAsync(ExecutionUnitId executionUnitId, string presharedKey, CancellationToken cancellationToken) =>
        await WriteAsync(new ProviderKeyRecord(executionUnitId, true, DateTimeOffset.UtcNow), presharedKey, cancellationToken).ConfigureAwait(false);

    private async Task WriteAsync(ProviderKeyRecord record, string presharedKey, CancellationToken cancellationToken) =>
        await WriteAsync(keys.ProviderKey(presharedKey), record, ObjectWriteConditions.IfNotExists, cancellationToken).ConfigureAwait(false);

    private async Task<ProviderKeyRecord?> ReadAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        await using var read = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        return read is null ? null : await JsonSerializer.DeserializeAsync<ProviderKeyRecord>(read.Content, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(ObjectKey key, ProviderKeyRecord record, ObjectWriteConditions conditions, CancellationToken cancellationToken)
    {
        await using var content = new MemoryStream();
        await JsonSerializer.SerializeAsync(content, record, JsonOptions, cancellationToken).ConfigureAwait(false);
        content.Position = 0;
        await store.PutAsync(key, content, conditions, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record ProviderKeyRecord(ExecutionUnitId ExecutionUnitId, bool Active, DateTimeOffset IssuedAt);

public sealed class ProviderKeyIssuer(ObjectStoreProviderKeyRegistry registry)
{
    public async Task<IReadOnlyList<IssuedProviderKey>> IssueAsync(int count, CancellationToken cancellationToken)
    {
        var issued = new List<IssuedProviderKey>(count);
        await IssueAsync(count, (key, _) => { issued.Add(key); return Task.CompletedTask; }, cancellationToken).ConfigureAwait(false);
        return issued;
    }

    /// <summary>Invokes the callback only after each generated key has a durable record.</summary>
    public async Task IssueAsync(int count, Func<IssuedProviderKey, CancellationToken, Task> onIssued, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onIssued);
        if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count));
        for (var index = 0; index < count; index++)
        {
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var executionUnitId = ExecutionUnitId.New();
            await registry.ProvisionAsync(executionUnitId, key, cancellationToken).ConfigureAwait(false);
            await onIssued(new IssuedProviderKey(executionUnitId, key), cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed record IssuedProviderKey(ExecutionUnitId ExecutionUnitId, string PresharedKey);
