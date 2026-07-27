using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using MutualGPU.Application;

namespace MutualGPU.Infrastructure;

/// <summary>Writes the authoritative all-origin CORS policy for direct browser
/// reads of presigned objects in the application-data bucket. Provider-key
/// storage never receives a browser CORS policy.</summary>
public sealed class AwsS3BrowserObjectCorsPolicy : IBrowserObjectCorsPolicy
{
    private readonly IAmazonS3 client;
    private readonly string bucketName;

    public AwsS3BrowserObjectCorsPolicy(AwsS3ObjectStoreOptions options)
        : this(CreateClient(options), BucketName(options)) { }

    internal AwsS3BrowserObjectCorsPolicy(IAmazonS3 client, string bucketName)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.bucketName = String.IsNullOrWhiteSpace(bucketName)
            ? throw new ArgumentException("An application-data bucket is required.", nameof(bucketName))
            : bucketName;
    }

    public async Task SynchronizeAsync(IReadOnlyCollection<string> allowedOrigins, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);

        await client.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucketName,
            Configuration = new CORSConfiguration
            {
                Rules =
                [
                    new CORSRule
                    {
                        AllowedOrigins = ["*"],
                        AllowedMethods = ["GET", "HEAD"],
                        AllowedHeaders = ["*"],
                        ExposeHeaders = ["ETag"],
                        MaxAgeSeconds = 3600,
                    }
                ]
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static IAmazonS3 CreateClient(AwsS3ObjectStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new AmazonS3Client(new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region) });
    }

    private static string BucketName(AwsS3ObjectStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options.BucketName;
    }
}

/// <summary>Local and non-AWS hosts do not expose presigned AWS object URLs.</summary>
public sealed class NoOpBrowserObjectCorsPolicy : IBrowserObjectCorsPolicy
{
    public Task SynchronizeAsync(IReadOnlyCollection<string> allowedOrigins, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
