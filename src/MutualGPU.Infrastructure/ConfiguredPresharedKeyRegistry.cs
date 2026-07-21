using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using MutualGPU.Application;
using MutualGPU.Domain;

namespace MutualGPU.Infrastructure;

/// <summary>Static MVP registry. Keys are configuration values and are compared in fixed time.</summary>
public sealed class ConfiguredPresharedKeyRegistry : IExecutionUnitAuthenticator, IExecutionUnitKeyResolver
{
    private readonly IReadOnlyDictionary<ExecutionUnitId, byte[]> keys;
    private readonly IReadOnlyDictionary<ExecutionUnitId, string> configuredKeys;
    private readonly MutualGpuObjectKeys objectKeys;

    public ConfiguredPresharedKeyRegistry(
        IReadOnlyDictionary<ExecutionUnitId, string> configuredKeys,
        MutualGpuObjectKeys objectKeys)
    {
        ArgumentNullException.ThrowIfNull(configuredKeys);
        ArgumentNullException.ThrowIfNull(objectKeys);
        this.configuredKeys = configuredKeys;
        this.objectKeys = objectKeys;
        keys = configuredKeys.ToDictionary(
            static pair => pair.Key,
            static pair => Encoding.UTF8.GetBytes(pair.Value),
            EqualityComparer<ExecutionUnitId>.Default);
    }

    public Task<ExecutionUnitId?> AuthenticateAsync(string? presharedKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryAuthenticate(presharedKey, out var executionUnitId)
            ? (ExecutionUnitId?)executionUnitId
            : null);
    }

    public Task<string?> GetProviderKeyDigestAsync(ExecutionUnitId executionUnitId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(configuredKeys.TryGetValue(executionUnitId, out var presharedKey)
            ? objectKeys.ProviderDigest(presharedKey)
            : null);
    }

    public async IAsyncEnumerable<ExecutionUnitId> ListExecutionUnitIdsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var executionUnitId in configuredKeys.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return executionUnitId;
        }
    }

    public bool TryGetPresharedKey(ExecutionUnitId executionUnitId, out string presharedKey) =>
        configuredKeys.TryGetValue(executionUnitId, out presharedKey!);

    public IReadOnlyCollection<ExecutionUnitId> ExecutionUnitIds => configuredKeys.Keys.ToArray();

    public bool TryAuthenticate(string? presharedKey, out ExecutionUnitId executionUnitId)
    {
        if (String.IsNullOrWhiteSpace(presharedKey))
        {
            executionUnitId = default;
            return false;
        }

        var candidate = Encoding.UTF8.GetBytes(presharedKey);
        foreach (var (id, configured) in keys)
        {
            if (CryptographicOperations.FixedTimeEquals(candidate, configured))
            {
                executionUnitId = id;
                return true;
            }
        }

        executionUnitId = default;
        return false;
    }
}
