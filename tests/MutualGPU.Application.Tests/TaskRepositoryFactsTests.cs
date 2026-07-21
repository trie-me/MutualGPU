using MutualGPU.Domain;
using MutualGPU.Infrastructure;

namespace MutualGPU.Application.Tests;

public sealed class TaskRepositoryFactsTests
{
    [Fact]
    public async Task Saves_append_immutable_task_facts_and_hydration_reads_the_newest_fact()
    {
        var store = new InMemoryObjectStore();
        var keys = new MutualGpuObjectKeys([1, 2, 3]);
        var repository = new ObjectStoreTaskRepository(store, keys, new RepositoryLockRegistry());
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);

        await repository.SaveAsync(task, CancellationToken.None);
        task.Assign(AttemptId.New(), ExecutionUnitId.New(), "opaque-handle", DateTimeOffset.UtcNow);
        await repository.SaveAsync(task, CancellationToken.None);
        var hydrated = await repository.GetAsync(task.RequestorId, task.Id, CancellationToken.None);

        Assert.NotNull(hydrated);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Assigned, hydrated.Status);
        Assert.Single(hydrated.Attempts);
        var facts = new List<string>();
        await foreach (var entry in store.ListAsync(keys.TaskFacts(task.RequestorId, task.Id), CancellationToken.None)) facts.Add(entry.Key.Value);
        Assert.Equal(2, facts.Count);
        var attemptEvents = new List<string>();
        await foreach (var entry in store.ListAsync(keys.AttemptEvents(task.RequestorId, task.Id, task.Attempts.Single().Id), CancellationToken.None)) attemptEvents.Add(entry.Key.Value);
        Assert.Single(attemptEvents);
        Assert.EndsWith("0001-assigned.json", attemptEvents[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attempt_state_changes_append_ordered_immutable_events()
    {
        var store = new InMemoryObjectStore();
        var keys = new MutualGpuObjectKeys([1, 2, 3]);
        var repository = new ObjectStoreTaskRepository(store, keys, new RepositoryLockRegistry());
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        await repository.SaveAsync(task, CancellationToken.None);
        var attempt = task.Assign(AttemptId.New(), ExecutionUnitId.New(), "opaque-handle", DateTimeOffset.UtcNow);
        await repository.SaveAsync(task, CancellationToken.None);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UtcNow);
        await repository.SaveAsync(task, CancellationToken.None);
        task.Disconnect(attempt.Id, attempt.Handle, DateTimeOffset.UtcNow);
        await repository.SaveAsync(task, CancellationToken.None);
        task.Rebind(attempt.Id, attempt.Handle);
        await repository.SaveAsync(task, CancellationToken.None);

        var names = new List<string>();
        await foreach (var entry in store.ListAsync(keys.AttemptEvents(task.RequestorId, task.Id, attempt.Id), CancellationToken.None)) names.Add(Path.GetFileName(entry.Key.Value));

        Assert.Equal(["0001-assigned.json", "0002-accepted.json", "0003-disconnected.json", "0004-accepted.json"], names);
    }

    [Fact]
    public async Task Startup_recovery_revokes_inflight_attempt_and_requeues_task()
    {
        var store = new InMemoryObjectStore();
        var keys = new MutualGpuObjectKeys([1, 2, 3]);
        var repository = new ObjectStoreTaskRepository(store, keys, new RepositoryLockRegistry());
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        await repository.SaveAsync(task, CancellationToken.None);
        var attempt = task.Assign(AttemptId.New(), ExecutionUnitId.New(), "opaque-handle", DateTimeOffset.UtcNow);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UtcNow);
        await repository.SaveAsync(task, CancellationToken.None);

        var recovered = await repository.RecoverAsync(CancellationToken.None);
        var hydrated = await repository.GetAsync(task.RequestorId, task.Id, CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Queued, hydrated!.Status);
        Assert.Equal(AttemptState.Revoked, hydrated.Attempts.Single().State);
        Assert.Equal("restart_recovery", hydrated.Attempts.Single().FailureStep);
    }

    [Fact]
    public async Task Startup_recovery_rebuilds_missing_queue_and_task_summary_projections()
    {
        var store = new InMemoryObjectStore();
        var keys = new MutualGpuObjectKeys([1, 2, 3]);
        var repository = new ObjectStoreTaskRepository(store, keys, new RepositoryLockRegistry());
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        var marker = keys.QueueMarker(task.Capability.Id, task.Tier, task.CreatedAt, task.Id);

        await repository.SaveAsync(task, CancellationToken.None);
        await store.DeleteAsync(marker, CancellationToken.None);
        await store.DeleteAsync(keys.TaskSummaryProjection(task.RequestorId), CancellationToken.None);

        var recovered = await repository.RecoverAsync(CancellationToken.None);
        var queued = await repository.GetQueuedAsync(CancellationToken.None);
        var summaries = await repository.ListSummariesAsync(task.RequestorId, CancellationToken.None);

        Assert.Equal(0, recovered);
        Assert.Contains(queued, candidate => candidate.Id == task.Id);
        Assert.Equal(task.Id, Assert.Single(summaries).TaskId);
    }

    [Fact]
    public async Task Startup_recovery_removes_stale_queue_markers_for_terminal_tasks()
    {
        var store = new InMemoryObjectStore();
        var keys = new MutualGpuObjectKeys([1, 2, 3]);
        var repository = new ObjectStoreTaskRepository(store, keys, new RepositoryLockRegistry());
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        var marker = keys.QueueMarker(task.Capability.Id, task.Tier, task.CreatedAt, task.Id);

        await repository.SaveAsync(task, CancellationToken.None);
        byte[] stale;
        await using (var read = await store.GetAsync(marker, CancellationToken.None))
        {
            Assert.NotNull(read);
            using var content = new MemoryStream();
            await read!.Content.CopyToAsync(content);
            stale = content.ToArray();
        }
        var attempt = task.Assign(AttemptId.New(), ExecutionUnitId.New(), "handle", DateTimeOffset.UtcNow);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UtcNow);
        task.Complete(attempt.Id, attempt.Handle, new TaskResult(new ResultArtifact(ArtifactId.New(), "application/zip", 4, "digest")));
        await repository.SaveAsync(task, CancellationToken.None);
        await using (var content = new MemoryStream(stale, writable: false))
        {
            await store.PutAsync(marker, content, ObjectWriteConditions.IfNotExists, CancellationToken.None);
        }

        await repository.RecoverAsync(CancellationToken.None);
        await using var remaining = await store.GetAsync(marker, CancellationToken.None);

        Assert.Null(remaining);
    }
}
