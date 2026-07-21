using Amazon;
using Amazon.S3;
using MutualGPU.Application;

namespace MutualGPU.Infrastructure;

/// <summary>AWS S3 configuration for MutualGPU's durable task, artifact, and provider-key store.</summary>
public sealed record AwsS3ObjectStoreOptions(string BucketName, string Region)
{
    public void Validate()
    {
        if (String.IsNullOrWhiteSpace(BucketName))
        {
            throw new InvalidOperationException("MutualGPU S3 storage requires a bucket name.");
        }

        if (String.IsNullOrWhiteSpace(Region))
        {
            throw new InvalidOperationException("MutualGPU S3 storage requires an AWS region.");
        }
    }
}

/// <summary>
/// AWS S3 object store using the standard SDK credential chain, including the ECS task role.
/// </summary>
public sealed class AwsS3ObjectStore : IObjectStore, IObjectStoreHealth
{
    private readonly BackblazeObjectStore inner;

    public AwsS3ObjectStore(AwsS3ObjectStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var client = new AmazonS3Client(new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region),
        });
        inner = new BackblazeObjectStore(client, new BackblazeObjectStoreOptions(options.BucketName));
    }

    public Task<ObjectRead?> GetAsync(ObjectKey key, CancellationToken cancellationToken) => inner.GetAsync(key, cancellationToken);

    public Task PutAsync(ObjectKey key, Stream content, ObjectWriteConditions conditions, CancellationToken cancellationToken) =>
        inner.PutAsync(key, content, conditions, cancellationToken);

    public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);

    public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, cancellationToken);

    public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken) =>
        inner.CreateDownloadUrlAsync(key, lifetime, cancellationToken);

    public Task CheckHealthAsync(CancellationToken cancellationToken) => inner.CheckHealthAsync(cancellationToken);
}
