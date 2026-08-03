using MutualGPU.Infrastructure;

namespace MutualGPU.Application.Tests;

public sealed class ObjectStorageTargetsTests
{
    [Fact]
    public void New_write_target_selection_is_explicit_but_not_provider_specific()
    {
        var selection = new ArtifactStorageTargetSelection("backblaze-primary");

        Assert.Equal("backblaze-primary", selection.WriteStorageTargetId);
        Assert.Throws<ArgumentException>(() => new ArtifactStorageTargetSelection(" "));
    }

    [Fact]
    public void A_mixed_provider_registry_is_lazy_and_does_not_construct_backblaze_for_an_aws_read()
    {
        var awsCreations = 0;
        var backblazeCreations = 0;
        var aws = new RecordingStore();
        var backblaze = new RecordingStore();
        using var registry = ObjectStoreRegistry.CreateLazy(
            ArtifactStorageTargetIds.AwsPrimary,
            [
                new KeyValuePair<string, Func<ObjectStoreTarget>>(
                    ArtifactStorageTargetIds.AwsPrimary,
                    () =>
                    {
                        awsCreations++;
                        return Target(ArtifactStorageTargetIds.AwsPrimary, aws);
                    }),
                new KeyValuePair<string, Func<ObjectStoreTarget>>(
                    "backblaze-archive",
                    () =>
                    {
                        backblazeCreations++;
                        return Target("backblaze-archive", backblaze);
                    }),
            ]);

        Assert.True(registry.IsConfigured(ArtifactStorageTargetIds.AwsPrimary));
        Assert.True(registry.IsConfigured("backblaze-archive"));
        Assert.Equal(0, awsCreations);
        Assert.Equal(0, backblazeCreations);

        Assert.Same(aws, registry.GetRequired(ArtifactStorageTargetIds.AwsPrimary).Store);
        Assert.Equal(1, awsCreations);
        Assert.Equal(0, backblazeCreations);
    }

    [Fact]
    public void Backblaze_configuration_requires_canonical_endpoint_and_an_explicit_cors_authority()
    {
        var valid = Options("https://s3.us-east-005.backblazeb2.com", BackblazeB2BrowserCorsMode.ExternallyManaged);
        valid.Validate();

        Assert.Throws<InvalidOperationException>(() =>
            Options("https://s3.us-west-004.backblazeb2.com", BackblazeB2BrowserCorsMode.ExternallyManaged).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            Options("https://s3.us-east-005.backblazeb2.com", null).Validate());
    }

    private static ObjectStorageOptions Options(string endpoint, BackblazeB2BrowserCorsMode? corsMode) => new()
    {
        WriteTarget = ArtifactStorageTargetIds.AwsPrimary,
        Targets = new Dictionary<string, ObjectStorageTargetOptions>(StringComparer.Ordinal)
        {
            [ArtifactStorageTargetIds.AwsPrimary] = new()
            {
                Provider = ObjectStorageProvider.AwsS3,
                BucketName = "test-aws-artifacts",
                Region = "us-east-1",
            },
            ["backblaze-archive"] = new()
            {
                Provider = ObjectStorageProvider.BackblazeB2,
                BucketName = "test-b2-artifacts",
                Region = "us-east-005",
                Endpoint = endpoint,
                AccessKeyId = "test-application-key-id",
                SecretAccessKey = "test-application-key",
                BrowserCorsMode = corsMode,
            },
        },
    };

    private static ObjectStoreTarget Target(string id, RecordingStore store) =>
        new(id, store, store, new NoOpBrowserObjectCorsPolicy());

    private sealed class RecordingStore : IObjectStore, IObjectStoreHealth
    {
        public Task<ObjectRead?> GetAsync(ObjectKey key, CancellationToken cancellationToken) => Task.FromResult<ObjectRead?>(null);
        public Task PutAsync(ObjectKey key, Stream content, ObjectWriteConditions conditions, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => Task.CompletedTask;
        public async IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri("https://example.test/"));
        public Task CheckHealthAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
