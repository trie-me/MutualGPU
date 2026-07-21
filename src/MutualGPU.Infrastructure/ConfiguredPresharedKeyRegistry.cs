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

    public ConfiguredPresharedKeyRegistry(IReadOnlyDictionary<ExecutionUnitId, string> configuredKeys)
    {
        ArgumentNullException.ThrowIfNull(configuredKeys);
        this.configuredKeys = configuredKeys;
        keys = configuredKeys.ToDictionary(
            static pair => pair.Key,
            static pair => Encoding.UTF8.GetBytes(pair.Value),
            EqualityComparer<ExecutionUnitId>.Default);
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
