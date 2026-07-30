using Amazon;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;
using MutualGPU.Application;

namespace MutualGPU.Infrastructure;

/// <summary>AWS S3 configuration for MutualGPU's durable task and artifact store.</summary>
public sealed record AwsS3ObjectStoreOptions(string BucketName, string Region, string? Profile = null)
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
public sealed class AwsS3ObjectStore : IObjectStore, IObjectStoreHealth, IDisposable
{
    private readonly IAmazonS3 client;
    private readonly AwsS3ObjectStoreOptions options;

    public AwsS3ObjectStore(AwsS3ObjectStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        var configuration = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region),
        };
        if (String.IsNullOrWhiteSpace(options.Profile))
        {
            client = new AmazonS3Client(configuration);
        }
        else
        {
            var profiles = new CredentialProfileStoreChain();
            if (!profiles.TryGetAWSCredentials(options.Profile, out var credentials))
            {
                throw new InvalidOperationException($"AWS profile '{options.Profile}' is not configured.");
            }
            client = new AmazonS3Client(credentials, configuration);
        }
    }

    public async Task<ObjectRead?> GetAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = options.BucketName,
                Key = key.Value,
            }, cancellationToken).ConfigureAwait(false);
            return new ObjectRead(
                response.ResponseStream,
                response.Headers.ContentLength,
                response.Headers.ContentType,
                response.ETag,
                new ResponseOwner(response));
        }
        catch (AmazonS3Exception error) when (error.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task PutAsync(ObjectKey key, Stream content, ObjectWriteConditions conditions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(conditions);
        var request = new PutObjectRequest
        {
            BucketName = options.BucketName,
            Key = key.Value,
            InputStream = content,
            AutoCloseStream = false,
        };
        if (conditions.MustNotExist) request.Headers["If-None-Match"] = "*";
        if (!String.IsNullOrWhiteSpace(conditions.ExpectedETag)) request.Headers["If-Match"] = conditions.ExpectedETag;
        await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
        client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = options.BucketName,
            Key = key.Value,
        }, cancellationToken);

    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? continuationToken = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = options.BucketName,
                Prefix = prefix.Value,
                ContinuationToken = continuationToken,
            }, cancellationToken).ConfigureAwait(false);
            foreach (var entry in response.S3Objects ?? [])
            {
                yield return new ObjectEntry(
                    new ObjectKey(entry.Key),
                    entry.Size.GetValueOrDefault(),
                    entry.LastModified ?? DateTime.UnixEpoch,
                    entry.ETag);
            }
            continuationToken = response.IsTruncated is true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);
    }

    public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.BucketName,
            Key = key.Value,
            Expires = DateTime.UtcNow.Add(lifetime),
            Verb = HttpVerb.GET,
        });
        return Task.FromResult(new Uri(url, UriKind.Absolute));
    }

    public async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = options.BucketName,
            MaxKeys = 1,
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => client.Dispose();

    private sealed class ResponseOwner(GetObjectResponse response) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            response.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
