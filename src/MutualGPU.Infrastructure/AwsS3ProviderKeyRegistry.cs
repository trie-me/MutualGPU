using MutualGPU.Application;

namespace MutualGPU.Infrastructure;

/// <summary>
/// Configuration for the dedicated AWS S3 bucket that holds provider-key bindings.
/// The SDK credential chain supplies AWS credentials; no key material is application configuration.
/// </summary>
public sealed record AwsS3ProviderKeyRegistryOptions(string BucketName, string Region)
{
    public void Validate()
    {
        if (String.IsNullOrWhiteSpace(BucketName))
        {
            throw new InvalidOperationException("MutualGPU provider-key storage requires an S3 bucket name.");
        }

        if (String.IsNullOrWhiteSpace(Region))
        {
            throw new InvalidOperationException("MutualGPU provider-key storage requires an AWS region.");
        }
    }
}

/// <summary>
/// Provider-key registry backed by its own AWS S3 bucket. Task and artifact storage
/// remain independent of this authentication store.
/// </summary>
public sealed class AwsS3ProviderKeyRegistry : IExecutionUnitKeyRegistry
{
    private readonly ObjectStoreProviderKeyRegistry inner;

    public AwsS3ProviderKeyRegistry(AwsS3ProviderKeyRegistryOptions options, MutualGpuObjectKeys keys)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keys);
        options.Validate();

        var store = new AwsS3ObjectStore(new AwsS3ObjectStoreOptions(options.BucketName, options.Region));
        inner = new ObjectStoreProviderKeyRegistry(store, keys);
    }

    public Task ProvisionAsync(MutualGPU.Domain.ExecutionUnitId executionUnitId, string presharedKey, CancellationToken cancellationToken) =>
        inner.ProvisionAsync(executionUnitId, presharedKey, cancellationToken);

    public Task<MutualGPU.Domain.ExecutionUnitId?> AuthenticateAsync(string? presharedKey, CancellationToken cancellationToken) =>
        inner.AuthenticateAsync(presharedKey, cancellationToken);

    public Task<string?> GetProviderKeyDigestAsync(MutualGPU.Domain.ExecutionUnitId executionUnitId, CancellationToken cancellationToken) =>
        inner.GetProviderKeyDigestAsync(executionUnitId, cancellationToken);

    public IAsyncEnumerable<MutualGPU.Domain.ExecutionUnitId> ListExecutionUnitIdsAsync(CancellationToken cancellationToken) =>
        inner.ListExecutionUnitIdsAsync(cancellationToken);
}
