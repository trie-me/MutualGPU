using System.Reflection;
using Amazon.S3;
using Amazon.S3.Model;
using MutualGPU.Infrastructure;

namespace MutualGPU.Api.Tests;

public sealed class AwsS3ObjectStoreTests
{
    [Fact]
    public async Task Readiness_check_lists_only_the_static_least_privilege_prefix()
    {
        var proxy = DispatchProxy.Create<IAmazonS3, RecordingS3Proxy>();
        var recorder = Assert.IsAssignableFrom<RecordingS3Proxy>(proxy);
        var store = new AwsS3ObjectStore(proxy, new AwsS3ObjectStoreOptions("target-artifacts", "us-east-1"));

        await store.CheckHealthAsync(CancellationToken.None);

        var request = Assert.IsType<ListObjectsV2Request>(recorder.Request);
        Assert.Equal("target-artifacts", request.BucketName);
        Assert.Equal("mutualgpu/v3/requestors/_health/tasks/_health/inputs/", request.Prefix);
        Assert.Equal(1, request.MaxKeys);
    }

    private class RecordingS3Proxy : DispatchProxy
    {
        public object? Request { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod ?? throw new InvalidOperationException("The invoked S3 method was unavailable.");
            if (method.Name == nameof(IAmazonS3.ListObjectsV2Async))
            {
                Request = args![0];
                return Task.FromResult(new ListObjectsV2Response());
            }
            if (method.ReturnType == typeof(void)) return null;
            throw new NotSupportedException($"Unexpected S3 method: {method.Name}.");
        }
    }
}
