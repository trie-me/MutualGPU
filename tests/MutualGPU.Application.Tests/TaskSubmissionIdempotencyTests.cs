using MutualGPU.Application;
using MutualGPU.Domain;

namespace MutualGPU.Application.Tests;

public sealed class TaskSubmissionIdempotencyTests
{
    [Fact]
    public async Task Same_key_and_payload_returns_original_task_but_changed_payload_conflicts()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [new InputDefinition("seed", CapabilityInputType.Integer, true, "Seed")], new OutputDefinition(), "hash");
        var repository = new Tasks();
        var app = new TaskSubmissionApplication(new Capabilities(capability), new Presence(capability.Id), repository, new Events());
        var command = new SubmitTaskCommand(RequestorId.New(), capability.Id, "hash", new Dictionary<string, string> { ["seed"] = "42" }, null, ResourceTier.Automatic, DateTimeOffset.UtcNow, "same-key");

        var first = Assert.IsType<SubmitTaskResult.Created>(await app.Submit(command).RunAsync());
        var duplicate = Assert.IsType<SubmitTaskResult.Created>(await app.Submit(command).RunAsync());
        var changed = Assert.IsType<SubmitTaskResult.Conflict>(await app.Submit(command with { Scalars = new Dictionary<string, string> { ["seed"] = "43" } }).RunAsync());

        Assert.Equal(first.Task.Id, duplicate.Task.Id);
        Assert.Equal("idempotency_key_reused", changed.Code);
    }

    [Fact]
    public async Task Same_key_and_same_image_content_returns_original_task_even_when_the_staged_artifact_id_changes()
    {
        var capability = new CapabilityDefinition(
            CapabilityId.New(),
            "splats",
            [new InputDefinition("image", CapabilityInputType.Image, true, "Image", ContentTypes: ["image/png"])],
            new OutputDefinition(),
            "hash");
        var repository = new Tasks();
        var app = new TaskSubmissionApplication(new Capabilities(capability), new Presence(capability.Id), repository, new Events());
        var command = new SubmitTaskCommand(
            RequestorId.New(),
            capability.Id,
            "hash",
            new Dictionary<string, string>(),
            ArtifactId.New(),
            ResourceTier.Automatic,
            DateTimeOffset.UtcNow,
            "same-image-key",
            TaskId.New(),
            "image/png",
            "png",
            123,
            "aabbcc");

        var first = Assert.IsType<SubmitTaskResult.Created>(await app.Submit(command).RunAsync());
        var replay = Assert.IsType<SubmitTaskResult.Created>(await app.Submit(command with { Image = ArtifactId.New(), TaskId = TaskId.New() }).RunAsync());
        var changed = Assert.IsType<SubmitTaskResult.Conflict>(await app.Submit(command with { Image = ArtifactId.New(), TaskId = TaskId.New(), ImageSha256 = "different" }).RunAsync());

        Assert.True(first.CreatedNow);
        Assert.False(replay.CreatedNow);
        Assert.Equal(first.Task.Id, replay.Task.Id);
        Assert.Equal("idempotency_key_reused", changed.Code);
    }

    [Fact]
    public async Task Submission_queues_a_profile_that_has_no_current_matching_machine()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var app = new TaskSubmissionApplication(new Capabilities(capability), new Presence(capability.Id), new Tasks(), new Events());
        var command = new SubmitTaskCommand(
            RequestorId.New(), capability.Id, "hash", new Dictionary<string, string>(), null,
            ResourceTier.ExtraLarge, DateTimeOffset.UtcNow);

        var result = await app.Submit(command).RunAsync();

        var created = Assert.IsType<SubmitTaskResult.Created>(result);
        Assert.Equal(new MachineSpecifications(ResourceTier.ExtraLarge, MachineSpecificationsPolicy.MinimumMemoryGiB), created.Task.Resources);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Queued, created.Task.Status);
    }

    private sealed class Capabilities(CapabilityDefinition capability) : ICapabilityReader
    {
        public Task<CapabilityDefinition?> GetAsync(CapabilityId capabilityId, CancellationToken cancellationToken) => Task.FromResult<CapabilityDefinition?>(capability);
    }

    private sealed class Presence(CapabilityId capabilityId) : IProviderPresence
    {
        public IReadOnlyList<ProviderCandidate> GetConnectedCandidates(CapabilityId requested) =>
            [new(ExecutionUnitId.New(), capabilityId, ResourceTier.Small, new MachineSpecifications(ResourceTier.Small, 8), true)];
    }

    private sealed class Tasks : ITaskRepository
    {
        private readonly List<TaskRequest> tasks = [];
        public Task<TaskRequest?> GetAsync(RequestorId requestorId, TaskId id, CancellationToken cancellationToken) => Task.FromResult(tasks.SingleOrDefault(task => task.RequestorId == requestorId && task.Id == id));
        public Task<IReadOnlyList<TaskRequest>> GetByRequestorAsync(RequestorId requestorId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskRequest>>(tasks.Where(task => task.RequestorId == requestorId).ToArray());
        public Task SaveAsync(TaskRequest task, CancellationToken cancellationToken) { if (!tasks.Contains(task)) tasks.Add(task); return Task.CompletedTask; }
    }

    private sealed class Events : IApplicationEventSink { public void TriggerScheduler() { } }
}
