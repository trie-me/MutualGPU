using System.Text.Json;
using MutualGPU.Application;
using MutualGPU.Domain;

namespace MutualGPU.Infrastructure;

public sealed class ObjectStoreExecutionUnitRepository(
    IObjectStore store,
    MutualGpuObjectKeys keys,
    IExecutionUnitKeyResolver keyRegistry,
    RepositoryLockRegistry locks) : IExecutionUnitRepository, ICapabilityReader, IEnrollmentStartupRecovery
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ExecutionUnit?> GetAsync(ExecutionUnitId id, CancellationToken cancellationToken)
    {
        var providerDigest = await keyRegistry.GetProviderKeyDigestAsync(id, cancellationToken).ConfigureAwait(false);
        if (providerDigest is null) return null;
        var snapshot = await ReadAsync<ExecutionUnitSnapshot>(keys.NodeIdentityForProviderDigest(providerDigest), cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : ExecutionUnit.Hydrate(snapshot);
    }

    public async Task<IReadOnlyList<CapabilityDefinition>> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var definitions = new List<CapabilityDefinition>();
        await foreach (var entry in store.ListAsync(keys.Capabilities(), cancellationToken))
        {
            if (!entry.Key.Value.EndsWith("/definition.json", StringComparison.Ordinal)) continue;
            var definition = await ReadAsync<CapabilityDefinition>(entry.Key, cancellationToken).ConfigureAwait(false);
            if (definition is not null) definitions.Add(definition);
        }
        return definitions;
    }

    public async Task<CapabilityDefinition?> GetAsync(CapabilityId capabilityId, CancellationToken cancellationToken) =>
        await ReadAsync<CapabilityDefinition>(keys.CapabilityDefinition(capabilityId), cancellationToken).ConfigureAwait(false);

    public async Task SaveAsync(ExecutionUnit executionUnit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionUnit);
        var providerDigest = await keyRegistry.GetProviderKeyDigestAsync(executionUnit.Id, cancellationToken).ConfigureAwait(false);
        if (providerDigest is null)
            throw new InvalidOperationException("The execution unit is not provisioned.");
        await using var held = await locks.AcquireAsync(executionUnit.Id.Value, cancellationToken);
        foreach (var capability in executionUnit.CurrentEnrollment.Capabilities)
        {
            var current = await ReadAsync<CapabilityDefinition>(keys.CapabilityDefinition(capability.Id), cancellationToken);
            if (current is null) await WriteAsync(keys.CapabilityDefinition(capability.Id), capability, ObjectWriteConditions.IfNotExists, cancellationToken);
        }

        // An enrollment replacement is a business fact. Persist it immutably before
        // advancing the compact identity projection, so a successful response never
        // exists without a durable event from which it can be explained or rebuilt.
        var enrollmentEvent = new EnrollmentEvent(EnrollmentEventId.New(), DateTimeOffset.UtcNow, executionUnit.ToSnapshot());
        await WriteAsync(
            keys.EnrollmentForProviderDigest(providerDigest, executionUnit.Version, enrollmentEvent.Id),
            enrollmentEvent,
            ObjectWriteConditions.IfNotExists,
            cancellationToken);
        await WriteAsync(keys.NodeIdentityForProviderDigest(providerDigest), executionUnit.ToSnapshot(), ObjectWriteConditions.None, cancellationToken);
    }

    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        var recovered = 0;
        await foreach (var executionUnitId in keyRegistry.ListExecutionUnitIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            var providerDigest = await keyRegistry.GetProviderKeyDigestAsync(executionUnitId, cancellationToken).ConfigureAwait(false);
            if (providerDigest is null) continue;
            EnrollmentEvent? latest = null;
            ObjectKey? latestKey = null;
            await foreach (var entry in store.ListAsync(keys.EnrollmentsForProviderDigest(providerDigest), cancellationToken).ConfigureAwait(false))
            {
                if (!entry.Key.Value.EndsWith(".json", StringComparison.Ordinal)) continue;
                var candidate = await ReadAsync<EnrollmentEvent>(entry.Key, cancellationToken).ConfigureAwait(false);
                if (candidate is null || candidate.Snapshot.Id != executionUnitId) continue;
                if (latest is null || candidate.Snapshot.Version.Value > latest.Snapshot.Version.Value ||
                    candidate.Snapshot.Version == latest.Snapshot.Version && StringComparer.Ordinal.Compare(entry.Key.Value, latestKey!.Value.Value) > 0)
                {
                    latest = candidate;
                    latestKey = entry.Key;
                }
            }

            if (latest is null) continue;
            var identity = await ReadAsync<ExecutionUnitSnapshot>(keys.NodeIdentityForProviderDigest(providerDigest), cancellationToken).ConfigureAwait(false);
            if (identity is not null && identity.Version.Value >= latest.Snapshot.Version.Value) continue;
            await WriteAsync(keys.NodeIdentityForProviderDigest(providerDigest), latest.Snapshot, ObjectWriteConditions.None, cancellationToken).ConfigureAwait(false);
            recovered++;
        }
        return recovered;
    }

    private async Task<T?> ReadAsync<T>(ObjectKey key, CancellationToken cancellationToken)
    {
        await using var read = await store.GetAsync(key, cancellationToken);
        return read is null ? default : await JsonSerializer.DeserializeAsync<T>(read.Content, JsonOptions, cancellationToken);
    }

    private async Task WriteAsync<T>(ObjectKey key, T value, ObjectWriteConditions conditions, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
        stream.Position = 0;
        await store.PutAsync(key, stream, conditions, cancellationToken);
    }

    private sealed record EnrollmentEvent(EnrollmentEventId Id, DateTimeOffset OccurredAt, ExecutionUnitSnapshot Snapshot);
}
