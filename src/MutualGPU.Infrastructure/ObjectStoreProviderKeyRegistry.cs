using System.Runtime.CompilerServices;
using System.Text.Json;
using MutualGPU.Application;
using MutualGPU.Domain;

namespace MutualGPU.Infrastructure;

/// <summary>
/// S3-backed provider-key registry. A provider key's SHA-256 digest selects its create-only
/// record, whose body contains only the execution-unit ID.
/// </summary>
public sealed class ObjectStoreProviderKeyRegistry(IObjectStore store, MutualGpuObjectKeys keys) : IExecutionUnitKeyRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ProvisionAsync(ExecutionUnitId executionUnitId, string presharedKey, CancellationToken cancellationToken)
    {
        if (executionUnitId.Value == Guid.Empty)
        {
            throw new ArgumentException("An execution-unit ID is required.", nameof(executionUnitId));
        }

        if (String.IsNullOrWhiteSpace(presharedKey))
        {
            throw new ArgumentException("A preshared key is required.", nameof(presharedKey));
        }

        await using var content = new MemoryStream();
        await JsonSerializer.SerializeAsync(content, new ProviderKeyBinding(executionUnitId), JsonOptions, cancellationToken).ConfigureAwait(false);
        content.Position = 0;
        await store.PutAsync(keys.ProviderKey(presharedKey), content, ObjectWriteConditions.IfNotExists, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionUnitId?> AuthenticateAsync(string? presharedKey, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(presharedKey))
        {
            return null;
        }

        var binding = await ReadAsync(keys.ProviderKey(presharedKey), cancellationToken).ConfigureAwait(false);
        return binding is null || binding.ExecutionUnitId.Value == Guid.Empty
            ? null
            : binding.ExecutionUnitId;
    }

    public async Task<string?> GetProviderKeyDigestAsync(ExecutionUnitId executionUnitId, CancellationToken cancellationToken)
    {
        await foreach (var entry in store.ListAsync(keys.ProviderKeys(), cancellationToken).ConfigureAwait(false))
        {
            if (!entry.Key.Value.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            var binding = await ReadAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            if (binding?.ExecutionUnitId != executionUnitId)
            {
                continue;
            }

            return Path.GetFileNameWithoutExtension(entry.Key.Value);
        }

        return null;
    }

    public async IAsyncEnumerable<ExecutionUnitId> ListExecutionUnitIdsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<ExecutionUnitId>();
        await foreach (var entry in store.ListAsync(keys.ProviderKeys(), cancellationToken).ConfigureAwait(false))
        {
            if (!entry.Key.Value.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            var binding = await ReadAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            if (binding is null ||
                binding.ExecutionUnitId.Value == Guid.Empty ||
                !seen.Add(binding.ExecutionUnitId))
            {
                continue;
            }

            yield return binding.ExecutionUnitId;
        }
    }

    private async Task<ProviderKeyBinding?> ReadAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        await using var content = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        return content is null
            ? null
            : await JsonSerializer.DeserializeAsync<ProviderKeyBinding>(content.Content, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private sealed record ProviderKeyBinding(ExecutionUnitId ExecutionUnitId);
}
