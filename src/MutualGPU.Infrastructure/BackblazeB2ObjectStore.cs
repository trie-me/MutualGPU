using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using MutualGPU.Application;

namespace MutualGPU.Infrastructure;

/// <summary>S3-compatible Backblaze B2 target. It is constructed only for an explicitly configured B2 target.</summary>
public sealed class BackblazeB2ObjectStoreOptions
{
    public BackblazeB2ObjectStoreOptions(
        string bucketName,
        string endpoint,
        string region,
        string applicationKeyId,
        string applicationKey,
        bool ForcePathStyle = true)
    {
        BucketName = bucketName;
        Endpoint = endpoint;
        Region = region;
        ApplicationKeyId = applicationKeyId;
        ApplicationKey = applicationKey;
        this.ForcePathStyle = ForcePathStyle;
    }

    public string BucketName { get; }

    public string Endpoint { get; }

    public string Region { get; }

    public string ApplicationKeyId { get; }

    public string ApplicationKey { get; }

    public bool ForcePathStyle { get; }

    public override string ToString() =>
        $"BackblazeB2ObjectStoreOptions {{ BucketName = {BucketName}, Endpoint = {Endpoint}, Region = {Region}, ApplicationKeyId = <redacted>, ApplicationKey = <redacted>, ForcePathStyle = {ForcePathStyle} }}";

    public void Validate()
    {
        if (String.IsNullOrWhiteSpace(BucketName) || String.IsNullOrWhiteSpace(Region) ||
            String.IsNullOrWhiteSpace(ApplicationKeyId) || String.IsNullOrWhiteSpace(ApplicationKey) ||
            !Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            !StringComparer.OrdinalIgnoreCase.Equals(endpoint.Scheme, Uri.UriSchemeHttps) ||
            !endpoint.IsDefaultPort ||
            !String.IsNullOrEmpty(endpoint.Query) || !String.IsNullOrEmpty(endpoint.Fragment) ||
            !String.IsNullOrEmpty(endpoint.UserInfo) || endpoint.AbsolutePath is not "/" ||
            !StringComparer.OrdinalIgnoreCase.Equals(endpoint.Host, $"s3.{Region}.backblazeb2.com"))
        {
            throw new InvalidOperationException("Backblaze B2 requires bucket, region, application-key credentials, and an absolute HTTPS S3 endpoint.");
        }
    }
}

public sealed class BackblazeB2ObjectStore : IObjectStore, IObjectStoreHealth, IObjectStoreWriteReceipts, IDisposable
{
    private readonly IAmazonS3 client;
    private readonly BackblazeB2ObjectStoreOptions options;

    public BackblazeB2ObjectStore(BackblazeB2ObjectStoreOptions options)
        : this(CreateClient(options), options) { }

    internal BackblazeB2ObjectStore(IAmazonS3 client, BackblazeB2ObjectStoreOptions options)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.options.Validate();
    }

    public async Task<ObjectRead?> GetAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = options.BucketName, Key = key.Value }, cancellationToken).ConfigureAwait(false);
            return new ObjectRead(response.ResponseStream, response.Headers.ContentLength, response.Headers.ContentType, response.ETag, new ResponseOwner(response));
        }
        catch (AmazonS3Exception error) when (error.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task PutAsync(ObjectKey key, Stream content, ObjectWriteConditions conditions, CancellationToken cancellationToken) =>
        _ = await PutWithReceiptAsync(key, content, conditions, cancellationToken).ConfigureAwait(false);

    public async Task<ObjectWriteReceipt> PutWithReceiptAsync(ObjectKey key, Stream content, ObjectWriteConditions conditions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions != ObjectWriteConditions.None)
        {
            throw new NotSupportedException(
                "The Backblaze B2 object-store adapter does not support conditional writes until Backblaze documents an atomic S3-compatible primitive.");
        }
        var request = new PutObjectRequest { BucketName = options.BucketName, Key = key.Value, InputStream = content, AutoCloseStream = false };
        var response = await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
        return new ObjectWriteReceipt(response.ETag);
    }

    public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = options.BucketName, Key = key.Value }, cancellationToken);

    public async IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? token = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.BucketName, Prefix = prefix.Value, ContinuationToken = token }, cancellationToken).ConfigureAwait(false);
            foreach (var entry in response.S3Objects ?? []) yield return new ObjectEntry(new ObjectKey(entry.Key), entry.Size.GetValueOrDefault(), entry.LastModified ?? DateTime.UnixEpoch, entry.ETag);
            token = response.IsTruncated is true ? response.NextContinuationToken : null;
        }
        while (token is not null);
    }

    public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        return Task.FromResult(new Uri(client.GetPreSignedURL(new GetPreSignedUrlRequest { BucketName = options.BucketName, Key = key.Value, Expires = DateTime.UtcNow.Add(lifetime), Verb = HttpVerb.GET }), UriKind.Absolute));
    }

    public async Task CheckHealthAsync(CancellationToken cancellationToken) =>
        _ = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = options.BucketName,
            MaxKeys = 1,
            // Prefix-restricted B2 app keys reject unrestricted ListObjects calls.
            Prefix = MutualGpuObjectKeys.RootPrefix,
        }, cancellationToken).ConfigureAwait(false);

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

    private sealed class ResponseOwner(GetObjectResponse response) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { response.Dispose(); return ValueTask.CompletedTask; }
    }
}
