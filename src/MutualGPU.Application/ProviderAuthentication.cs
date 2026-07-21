using System.Security.Cryptography;
using MutualGPU.Domain;

namespace MutualGPU.Application;

public interface IExecutionUnitAuthenticator
{
    Task<ExecutionUnitId?> AuthenticateAsync(string? presharedKey, CancellationToken cancellationToken);
}

/// <summary>Resolves execution units to opaque provider digests used in object keys.</summary>
public interface IExecutionUnitKeyResolver
{
    Task<string?> GetProviderKeyDigestAsync(ExecutionUnitId executionUnitId, CancellationToken cancellationToken);

    IAsyncEnumerable<ExecutionUnitId> ListExecutionUnitIdsAsync(CancellationToken cancellationToken);
}

/// <summary>Durable provider-key bindings. Raw provider keys are never retained by this contract.</summary>
public interface IExecutionUnitKeyRegistry : IExecutionUnitAuthenticator, IExecutionUnitKeyResolver
{
    Task ProvisionAsync(ExecutionUnitId executionUnitId, string presharedKey, CancellationToken cancellationToken);
}

public sealed record IssuedProviderKey(ExecutionUnitId ExecutionUnitId, string PresharedKey);

/// <summary>Issues provider keys only after their durable registry bindings have been created.</summary>
public sealed class ProviderKeyIssuer(IExecutionUnitKeyRegistry registry)
{
    public async Task<IReadOnlyList<IssuedProviderKey>> IssueAsync(int count, CancellationToken cancellationToken)
    {
        var issued = new List<IssuedProviderKey>(count);
        await IssueAsync(count, (key, _) =>
        {
            issued.Add(key);
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return issued;
    }

    public async Task IssueAsync(int count, Func<IssuedProviderKey, CancellationToken, Task> onIssued, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "At least one provider key must be issued.");
        }

        ArgumentNullException.ThrowIfNull(onIssued);
        for (var index = 0; index < count; index++)
        {
            var issued = new IssuedProviderKey(ExecutionUnitId.New(), CreatePresharedKey());
            await registry.ProvisionAsync(issued.ExecutionUnitId, issued.PresharedKey, cancellationToken).ConfigureAwait(false);
            await onIssued(issued, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string CreatePresharedKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
