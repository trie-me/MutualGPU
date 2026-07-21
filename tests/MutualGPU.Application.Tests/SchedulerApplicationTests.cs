using MutualGPU.Application;
using MutualGPU.Domain;

namespace MutualGPU.Application.Tests;

public sealed class SchedulerApplicationTests
{
    [Fact]
    public async Task Scheduler_assigns_the_best_idle_connected_candidate_and_marks_it_busy()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Medium, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        var unit = ExecutionUnitId.New();
        var presence = new FakePresence(Candidate(unit, capability.Id, ResourceTier.Medium));
        var assignments = new FakeAssignments(presence);
        var scheduler = new SchedulerApplication(new FakeQueue(task), presence, new FakeTasks(), assignments);

        var count = await scheduler.Evaluate(DateTimeOffset.UtcNow).RunAsync(CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Assigned, task.Status);
        Assert.Single(assignments.Delivered);
        Assert.Equal(unit, assignments.Delivered[0].UnitId);
    }

    [Fact]
    public async Task Scheduler_carries_an_immutable_image_descriptor_to_the_transport_boundary()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [new InputDefinition("image", CapabilityInputType.Image, true, "Image")], new OutputDefinition(), "hash");
        var requestor = RequestorId.New();
        var image = ArtifactId.New();
        var task = new TaskRequest(
            TaskId.New(),
            requestor,
            capability,
            ResourceTier.Automatic,
            new TaskParameters(new Dictionary<string, string>(), image, ImageContentType: "image/png", ImageExtension: "png", ImageLength: 42, ImageSha256: "digest"),
            DateTimeOffset.UtcNow);
        var unit = ExecutionUnitId.New();
        var presence = new FakePresence(Candidate(unit, capability.Id, ResourceTier.Small));
        var assignments = new FakeAssignments(presence);
        var scheduler = new SchedulerApplication(
            new FakeQueue(task),
            presence,
            new FakeTasks(),
            assignments);

        await scheduler.Evaluate(DateTimeOffset.UtcNow).RunAsync(CancellationToken.None);

        var input = Assert.Single(assignments.Delivered).Assignment.Input;
        Assert.NotNull(input);
        Assert.Equal(requestor, input.RequestorId);
        Assert.Equal(image, input.ArtifactId);
        Assert.Equal("image/png", input.ContentType);
        Assert.Equal("png", input.Extension);
        Assert.Equal(42, input.Length);
        Assert.Equal("digest", input.Sha256);
    }

    [Fact]
    public async Task Older_matching_work_is_assigned_first_and_preserves_specialist_capacity()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var now = DateTimeOffset.UtcNow;
        var lower = NewTask(capability, ResourceTier.Small, now.AddMinutes(-5));
        var higher = NewTask(capability, ResourceTier.Large, now);
        var smallUnit = ExecutionUnitId.New();
        var largeUnit = ExecutionUnitId.New();
        var presence = new FakePresence(
            Candidate(smallUnit, capability.Id, ResourceTier.Small),
            Candidate(largeUnit, capability.Id, ResourceTier.Large));
        var assignments = new FakeAssignments(presence);
        var scheduler = new SchedulerApplication(new FakeQueue(lower, higher), presence, new FakeTasks(), assignments);

        var count = await scheduler.Evaluate(now).RunAsync(CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Collection(
            assignments.Delivered,
            item => { Assert.Equal(lower.Id, item.Assignment.TaskId); Assert.Equal(smallUnit, item.UnitId); },
            item => { Assert.Equal(higher.Id, item.Assignment.TaskId); Assert.Equal(largeUnit, item.UnitId); });
    }

    [Fact]
    public async Task Exact_tier_match_is_preferred_over_a_higher_tier_node()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var task = NewTask(capability, ResourceTier.Medium, DateTimeOffset.UtcNow);
        var exact = ExecutionUnitId.New();
        var better = ExecutionUnitId.New();
        var presence = new FakePresence(
            Candidate(better, capability.Id, ResourceTier.Large),
            Candidate(exact, capability.Id, ResourceTier.Medium));
        var assignments = new FakeAssignments(presence);
        var scheduler = new SchedulerApplication(new FakeQueue(task), presence, new FakeTasks(), assignments);

        await scheduler.Evaluate(DateTimeOffset.UtcNow).RunAsync(CancellationToken.None);

        Assert.Equal(exact, Assert.Single(assignments.Delivered).UnitId);
    }

    [Fact]
    public async Task Machine_specifications_are_task_matching_constraints()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var task = NewTask(capability, ResourceTier.Large, DateTimeOffset.UtcNow);
        var unit = ExecutionUnitId.New();
        var presence = new FakePresence(new ProviderCandidate(
            unit,
            capability.Id,
            ResourceTier.Large,
            new MachineSpecifications(ResourceTier.Small, 4),
            true));
        var assignments = new FakeAssignments(presence);
        var scheduler = new SchedulerApplication(new FakeQueue(task), presence, new FakeTasks(), assignments);

        await scheduler.Evaluate(DateTimeOffset.UtcNow).RunAsync(CancellationToken.None);

        Assert.Empty(assignments.Delivered);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Queued, task.Status);
    }

    [Fact]
    public async Task One_pass_assigns_until_all_candidates_are_exhausted_and_leaves_unmatched_work_queued()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var now = DateTimeOffset.UtcNow;
        var first = NewTask(capability, ResourceTier.Automatic, now);
        var second = NewTask(capability, ResourceTier.Automatic, now.AddSeconds(1));
        var third = NewTask(capability, ResourceTier.Automatic, now.AddSeconds(2));
        var presence = new FakePresence(
            Candidate(ExecutionUnitId.New(), capability.Id, ResourceTier.Small),
            Candidate(ExecutionUnitId.New(), capability.Id, ResourceTier.Medium));
        var assignments = new FakeAssignments(presence);
        var scheduler = new SchedulerApplication(new FakeQueue(first, second, third), presence, new FakeTasks(), assignments);

        var count = await scheduler.Evaluate(now).RunAsync(CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Assigned, first.Status);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Assigned, second.Status);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Queued, third.Status);
    }

    [Fact]
    public async Task Failed_transport_delivery_requeues_the_assignment_without_consuming_a_provider_slot()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var task = NewTask(capability, ResourceTier.Automatic, DateTimeOffset.UtcNow);
        var presence = new FakePresence(Candidate(ExecutionUnitId.New(), capability.Id, ResourceTier.Small));
        var assignments = new FakeAssignments(presence, shouldDeliver: false);
        var scheduler = new SchedulerApplication(new FakeQueue(task), presence, new FakeTasks(), assignments);

        var count = await scheduler.Evaluate(DateTimeOffset.UtcNow).RunAsync(CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Queued, task.Status);
        Assert.Equal(AttemptState.Revoked, Assert.Single(task.Attempts).State);
        Assert.Single(assignments.Removed);
    }

    private static TaskRequest NewTask(CapabilityDefinition capability, ResourceTier tier, DateTimeOffset createdAt) =>
        new(TaskId.New(), RequestorId.New(), capability, tier, new TaskParameters(new Dictionary<string, string>(), null), createdAt);

    private static ProviderCandidate Candidate(ExecutionUnitId unit, CapabilityId capability, ResourceTier tier) =>
        new(unit, capability, tier, new MachineSpecifications(tier is ResourceTier.Automatic ? ResourceTier.Small : tier, 16), true);

    private sealed class FakeQueue(params TaskRequest[] tasks) : IQueuedTaskReader
    {
        public Task<IReadOnlyList<TaskRequest>> GetQueuedAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskRequest>>(tasks);
    }

    private sealed class FakePresence(params ProviderCandidate[] candidates) : IProviderPresence
    {
        private ProviderCandidate[] candidates = candidates;

        public IReadOnlyList<ProviderCandidate> GetConnectedCandidates(CapabilityId capabilityId) => candidates.Where(candidate => candidate.CapabilityId == capabilityId).ToArray();

        public void MarkBusy(ExecutionUnitId executionUnitId) => candidates = candidates.Select(candidate => candidate.ExecutionUnitId == executionUnitId ? candidate with { IsIdle = false } : candidate).ToArray();
    }

    private sealed class FakeTasks : ITaskRepository
    {
        public Task<TaskRequest?> GetAsync(RequestorId requestorId, TaskId id, CancellationToken cancellationToken) => Task.FromResult<TaskRequest?>(null);
        public Task<IReadOnlyList<TaskRequest>> GetByRequestorAsync(RequestorId requestorId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskRequest>>([]);
        public Task SaveAsync(TaskRequest task, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeAssignments(FakePresence presence, bool shouldDeliver = true) : IProviderAssignments
    {
        public List<(ExecutionUnitId UnitId, ProviderAssignment Assignment)> Delivered { get; } = [];
        public List<(ExecutionUnitId UnitId, TaskId TaskId, AttemptId AttemptId)> Removed { get; } = [];
        public bool TryDeliver(ExecutionUnitId executionUnitId, ProviderAssignment assignment)
        {
            if (!shouldDeliver) return false;
            Delivered.Add((executionUnitId, assignment));
            presence.MarkBusy(executionUnitId);
            return true;
        }
        public void Track(ExecutionUnitId executionUnitId, TaskRequest task, TaskAttempt attempt) { }
        public bool TryGet(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId, string handle, out TaskRequest task) { task = null!; return false; }
        public void Remove(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId) => Removed.Add((executionUnitId, taskId, attemptId));
        public IReadOnlyList<ActiveProviderAssignment> GetUnacceptedBefore(DateTimeOffset deadline) => [];
        public IReadOnlyList<ActiveProviderAssignment> GetForExecutionUnit(ExecutionUnitId executionUnitId) => [];
        public IReadOnlyList<ActiveProviderAssignment> GetDisconnectedBefore(DateTimeOffset deadline) => [];
        public bool TryGetByHandle(ExecutionUnitId executionUnitId, string handle, out ActiveProviderAssignment assignment) { assignment = null!; return false; }
    }
}
