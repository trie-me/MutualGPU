using MutualGPU.Domain;
using MutualGPU.Infrastructure;

namespace MutualGPU.Application.Tests;

public sealed class ArtifactDownloadUrlResolverTests
{
    [Fact]
    public async Task Coexisting_aws_and_backblaze_locations_resolve_only_to_their_recorded_target()
    {
        var taskId = TaskId.New();
        var awsArtifact = Artifact(taskId, ArtifactId.New(), "compatibility/aws/result.zip") with
        {
            Locations =
            [new ArtifactLocation(
                ArtifactStorageTargetIds.AwsPrimary,
                "compatibility/aws/result.zip",
                "aws-opaque-etag",
                ArtifactLocationState.Available,
                DateTimeOffset.UtcNow)],
        };
        var backblazeArtifact = Artifact(taskId, ArtifactId.New(), "compatibility/b2/result.zip") with
        {
            Locations =
            [new ArtifactLocation(
                "backblaze-archive",
                "compatibility/b2/result.zip",
                "b2-opaque-etag",
                ArtifactLocationState.Available,
                DateTimeOffset.UtcNow)],
        };
        var aws = new RecordingStore("https://aws.example/");
        var backblaze = new RecordingStore("https://b2.example/");
        var fallback = new RecordingStore("https://fallback.example/");
        var resolver = new ArtifactDownloadUrlResolver(
            new RecordingOperations([awsArtifact, backblazeArtifact]),
            new ObjectStoreRegistry(
                ArtifactStorageTargetIds.AwsPrimary,
                [
                    Target(ArtifactStorageTargetIds.AwsPrimary, aws),
                    Target("backblaze-archive", backblaze),
                ],
                ownsTargets: false),
            fallback);

        var awsUrl = await resolver.CreateDownloadUrlAsync(
            taskId,
            awsArtifact.Id,
            new ObjectKey("legacy/aws/result.zip"),
            TimeSpan.FromMinutes(15),
            CancellationToken.None);
        var backblazeUrl = await resolver.CreateDownloadUrlAsync(
            taskId,
            backblazeArtifact.Id,
            new ObjectKey("legacy/b2/result.zip"),
            TimeSpan.FromMinutes(15),
            CancellationToken.None);

        Assert.Equal("aws.example", awsUrl.Host);
        Assert.Equal("compatibility/aws/result.zip", aws.LastDownloadKey?.Value);
        Assert.Equal("b2.example", backblazeUrl.Host);
        Assert.Equal("compatibility/b2/result.zip", backblaze.LastDownloadKey?.Value);
        Assert.Null(fallback.LastDownloadKey);
    }

    [Fact]
    public async Task Unknown_or_ambiguous_persisted_location_fails_without_a_provider_fallback()
    {
        var taskId = TaskId.New();
        var artifact = Artifact(taskId, ArtifactId.New(), "compatibility/unknown/result.zip") with
        {
            Locations =
            [new ArtifactLocation(
                "removed-target",
                "compatibility/unknown/result.zip",
                null,
                ArtifactLocationState.Available,
                DateTimeOffset.UtcNow)],
        };
        var aws = new RecordingStore("https://aws.example/");
        var fallback = new RecordingStore("https://fallback.example/");
        var resolver = new ArtifactDownloadUrlResolver(
            new RecordingOperations([artifact]),
            new ObjectStoreRegistry(
                ArtifactStorageTargetIds.AwsPrimary,
                [Target(ArtifactStorageTargetIds.AwsPrimary, aws)],
                ownsTargets: false),
            fallback);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.CreateDownloadUrlAsync(
            taskId,
            artifact.Id,
            new ObjectKey("legacy/unknown/result.zip"),
            TimeSpan.FromMinutes(15),
            CancellationToken.None));

        Assert.Contains("not configured", error.Message, StringComparison.Ordinal);
        Assert.Null(aws.LastDownloadKey);
        Assert.Null(fallback.LastDownloadKey);
    }

    private static ArtifactDescriptor Artifact(TaskId taskId, ArtifactId artifactId, string key) => new(
        artifactId,
        taskId,
        null,
        ArtifactDirection.Output,
        "result",
        key,
        "application/zip",
        42,
        new string('a', 64),
        ArtifactState.Available,
        DateTimeOffset.UtcNow);

    private static ObjectStoreTarget Target(string id, RecordingStore store) =>
        new(id, store, store, new NoOpBrowserObjectCorsPolicy());

    private sealed class RecordingOperations(IReadOnlyList<ArtifactDescriptor> artifacts) : IOperationUnitOfWork
    {
        private readonly IOperationContext context = new RecordingContext(artifacts);

        public Task<T> ExecuteAsync<T>(Func<IOperationContext, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
            operation(context, cancellationToken);
    }

    private sealed class RecordingContext(IReadOnlyList<ArtifactDescriptor> artifacts) : IOperationContext
    {
        public ITaskRepository Tasks => throw new NotSupportedException();
        public IExecutionUnitRepository ExecutionUnits => throw new NotSupportedException();
        public ICapabilityRepository Capabilities => throw new NotSupportedException();
        public IPartnerResourceRepository PartnerResources => throw new NotSupportedException();
        public IResultUploadRepository ResultUploads => throw new NotSupportedException();
        public IArtifactRepository Artifacts { get; } = new RecordingArtifacts(artifacts);
        public void Publish(OperationEvent message) => throw new NotSupportedException();
    }

    private sealed class RecordingArtifacts(IReadOnlyList<ArtifactDescriptor> artifacts) : IArtifactRepository
    {
        public Task<IReadOnlyList<ArtifactDescriptor>> GetForTaskAsync(TaskId taskId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ArtifactDescriptor>>(
                artifacts.Where(artifact => artifact.TaskId == taskId).ToArray());

        public void Add(ArtifactDescriptor artifact) => throw new NotSupportedException();

        public void Update(ArtifactDescriptor artifact) => throw new NotSupportedException();
    }

    private sealed class RecordingStore(Uri downloadBase) : IObjectStore, IObjectStoreHealth
    {
        public RecordingStore(string downloadBase) : this(new Uri(downloadBase, UriKind.Absolute)) { }

        public ObjectKey? LastDownloadKey { get; private set; }

        public Task<ObjectRead?> GetAsync(ObjectKey key, CancellationToken cancellationToken) =>
            Task.FromResult<ObjectRead?>(null);

        public Task PutAsync(ObjectKey key, Stream content, ObjectWriteConditions conditions, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<ObjectEntry> ListAsync(
            ObjectPrefix prefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            LastDownloadKey = key;
            return Task.FromResult(new Uri(downloadBase, Uri.EscapeDataString(key.Value)));
        }

        public Task CheckHealthAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
