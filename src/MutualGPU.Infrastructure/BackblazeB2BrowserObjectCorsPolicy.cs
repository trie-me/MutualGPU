using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using MutualGPU.Application;

namespace MutualGPU.Infrastructure;

/// <summary>Applies browser read CORS only. Object-store uploads remain API-proxied.</summary>
public sealed class BackblazeB2BrowserObjectCorsPolicy : IBrowserObjectCorsPolicy, IDisposable
{
    private readonly IAmazonS3 client;
    private readonly string bucketName;

    public BackblazeB2BrowserObjectCorsPolicy(BackblazeB2ObjectStoreOptions options)
        : this(CreateClient(options), options?.BucketName ?? throw new ArgumentNullException(nameof(options))) { }

    internal BackblazeB2BrowserObjectCorsPolicy(IAmazonS3 client, string bucketName)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.bucketName = String.IsNullOrWhiteSpace(bucketName) ? throw new ArgumentException("A bucket name is required.", nameof(bucketName)) : bucketName;
    }

    public async Task SynchronizeAsync(IReadOnlyCollection<string> allowedOrigins, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);
        var origins = allowedOrigins.Where(static origin => !String.IsNullOrWhiteSpace(origin)).Distinct(StringComparer.Ordinal).ToArray();
        if (origins.Length == 0)
        {
            await client.DeleteCORSConfigurationAsync(new DeleteCORSConfigurationRequest { BucketName = bucketName }, cancellationToken).ConfigureAwait(false);
            return;
        }
        await client.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucketName,
            Configuration = new CORSConfiguration
            {
                Rules =
                [new CORSRule
                {
                    AllowedOrigins = origins.ToList(),
                    AllowedMethods = ["GET", "HEAD"],
                    AllowedHeaders = ["*"],
                    ExposeHeaders = ["ETag"],
                    MaxAgeSeconds = 3600,
                }],
            },
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => client.Dispose();

    private static IAmazonS3 CreateClient(BackblazeB2ObjectStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new AmazonS3Client(new BasicAWSCredentials(options.ApplicationKeyId, options.ApplicationKey), new AmazonS3Config
        {
            ServiceURL = options.Endpoint,
            AuthenticationRegion = options.Region,
            ForcePathStyle = options.ForcePathStyle,
        });
    }
}
