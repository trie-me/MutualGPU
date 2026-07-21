using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using MutualGPU.Application;

namespace MutualGPU.Infrastructure;

public sealed record BackblazeObjectStoreOptions(string BucketName)
{
    public void Validate()
    {
        if (String.IsNullOrWhiteSpace(BucketName))
        {
            throw new InvalidOperationException("MutualGPU storage requires a Backblaze bucket name.");
        }
    }
}

public sealed record BackblazeS3Options(string Endpoint, string KeyId, string ApplicationKey, string BucketName)
{
    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("MutualGPU storage requires an absolute Backblaze S3 endpoint.");
        }

        if (String.IsNullOrWhiteSpace(KeyId) || String.IsNullOrWhiteSpace(ApplicationKey))
        {
            throw new InvalidOperationException("MutualGPU storage requires Backblaze application credentials.");
        }

        new BackblazeObjectStoreOptions(BucketName).Validate();
    }
}

/// <summary>Backblaze B2's S3-compatible adapter. Bucket and credentials remain infrastructure configuration.</summary>
public sealed class BackblazeObjectStore(IAmazonS3 client, BackblazeObjectStoreOptions options) : IObjectStore, IObjectStoreHealth
{
    public async Task<ObjectRead?> GetAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = Bucket(),
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
            BucketName = Bucket(),
            Key = key.Value,
            InputStream = content,
            AutoCloseStream = false,
        };
        if (conditions.MustNotExist)
        {
            request.Headers["If-None-Match"] = "*";
        }

        if (!String.IsNullOrWhiteSpace(conditions.ExpectedETag))
        {
            request.Headers["If-Match"] = conditions.ExpectedETag;
        }

        await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => client.DeleteObjectAsync(new DeleteObjectRequest
    {
        BucketName = Bucket(),
        Key = key.Value,
    }, cancellationToken);

    public async IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? continuationToken = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = Bucket(),
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
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = Bucket(),
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
            BucketName = Bucket(),
            MaxKeys = 1,
        }, cancellationToken).ConfigureAwait(false);
    }

    private string Bucket()
    {
        options.Validate();
        return options.BucketName;
    }

    private sealed class ResponseOwner(GetObjectResponse response) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            response.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public static class BackblazeObjectStoreServiceCollectionExtensions
{
    public static IServiceCollection AddMutualGpuBackblazeObjectStore(this IServiceCollection services, BackblazeS3Options options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton(new BackblazeObjectStoreOptions(options.BucketName));
        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
            new BasicAWSCredentials(options.KeyId, options.ApplicationKey),
            new AmazonS3Config
            {
                ServiceURL = options.Endpoint,
                ForcePathStyle = true,
            }));
        services.AddSingleton<BackblazeObjectStore>();
        services.AddSingleton<IObjectStore>(static services => services.GetRequiredService<BackblazeObjectStore>());
        services.AddSingleton<IObjectStoreHealth>(static services => services.GetRequiredService<BackblazeObjectStore>());
        return services;
    }
}
