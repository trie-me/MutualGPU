using System.Reflection;
using Amazon.S3;
using Amazon.S3.Model;
using MutualGPU.Infrastructure;

namespace MutualGPU.Application.Tests;

public sealed class BackblazeB2ObjectStoreTests
{
    [Fact]
    public async Task Presigned_get_uses_the_explicit_s3_compatible_endpoint_without_a_network_request()
    {
        using var store = new BackblazeB2ObjectStore(Options());

        var url = await store.CreateDownloadUrlAsync(
            new ObjectKey("compatibility/test/result.zip"),
            TimeSpan.FromMinutes(15),
            CancellationToken.None);

        Assert.Equal(Uri.UriSchemeHttps, url.Scheme);
        Assert.Equal("s3.us-east-005.backblazeb2.com", url.Host);
        Assert.Equal("/test-artifacts/compatibility/test/result.zip", url.AbsolutePath);
        Assert.Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256", url.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_probe_is_read_only_and_limited_to_the_application_root()
    {
        var proxy = DispatchProxy.Create<IAmazonS3, RecordingS3Proxy>();
        var recorder = Assert.IsAssignableFrom<RecordingS3Proxy>(proxy);
        using var store = new BackblazeB2ObjectStore(proxy, Options());

        await store.CheckHealthAsync(CancellationToken.None);

        var request = Assert.IsType<ListObjectsV2Request>(recorder.ListRequest);
        Assert.Equal("test-artifacts", request.BucketName);
        Assert.Equal(MutualGpuObjectKeys.RootPrefix, request.Prefix);
        Assert.Equal(1, request.MaxKeys);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "prior-opaque-etag")]
    public async Task Conditional_writes_are_rejected_before_the_sdk_is_called(bool mustNotExist, string? expectedETag)
    {
        var proxy = DispatchProxy.Create<IAmazonS3, RecordingS3Proxy>();
        var recorder = Assert.IsAssignableFrom<RecordingS3Proxy>(proxy);
        using var store = new BackblazeB2ObjectStore(proxy, Options());
        await using var content = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<NotSupportedException>(() => store.PutWithReceiptAsync(
            new ObjectKey("compatibility/test/result.zip"),
            content,
            new ObjectWriteConditions(expectedETag, mustNotExist),
            CancellationToken.None));

        Assert.Null(recorder.PutRequest);
    }

    [Fact]
    public async Task Successful_write_returns_the_provider_etag_as_opaque_metadata()
    {
        var proxy = DispatchProxy.Create<IAmazonS3, RecordingS3Proxy>();
        var recorder = Assert.IsAssignableFrom<RecordingS3Proxy>(proxy);
        using var store = new BackblazeB2ObjectStore(proxy, Options());
        await using var content = new MemoryStream([1, 2, 3]);

        var receipt = await store.PutWithReceiptAsync(
            new ObjectKey("compatibility/test/result.zip"),
            content,
            ObjectWriteConditions.None,
            CancellationToken.None);

        Assert.NotNull(recorder.PutRequest);
        Assert.Equal("provider-issued-opaque-etag", receipt.ETag);
    }

    [Fact]
    public void Options_do_not_render_application_key_material()
    {
        const string applicationKey = "test-application-key-sentinel";
        var options = new BackblazeB2ObjectStoreOptions(
            "test-artifacts",
            "https://s3.us-east-005.backblazeb2.com",
            "us-east-005",
            "test-application-key-id",
            applicationKey);

        Assert.DoesNotContain(applicationKey, options.ToString(), StringComparison.Ordinal);
        Assert.Contains("<redacted>", options.ToString(), StringComparison.Ordinal);
    }

    private static BackblazeB2ObjectStoreOptions Options() => new(
        "test-artifacts",
        "https://s3.us-east-005.backblazeb2.com",
        "us-east-005",
        "test-application-key-id",
        "test-application-key");

    private class RecordingS3Proxy : DispatchProxy
    {
        public ListObjectsV2Request? ListRequest { get; private set; }

        public PutObjectRequest? PutRequest { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IAmazonS3.ListObjectsV2Async):
                    ListRequest = Assert.IsType<ListObjectsV2Request>(args?[0]);
                    return Task.FromResult(new ListObjectsV2Response());
                case nameof(IAmazonS3.PutObjectAsync):
                    PutRequest = Assert.IsType<PutObjectRequest>(args?[0]);
                    return Task.FromResult(new PutObjectResponse { ETag = "provider-issued-opaque-etag" });
                case nameof(IDisposable.Dispose):
                    return null;
                default:
                    throw new NotSupportedException($"Unexpected S3 call: {targetMethod?.Name}.");
            }
        }
    }
}
