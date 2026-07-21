using System.Collections.Concurrent;
using System.Text.Json;
using MutualGPU.Application;
using MutualGPU.Domain;

namespace MutualGPU.Infrastructure;

/// <summary>
/// A process-local lock registry. It protects a single host's stage/commit window; it is not distributed leadership.
/// </summary>
public sealed class RepositoryLockRegistry : IEnrollmentGate
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();

    public async ValueTask<IAsyncDisposable> AcquireAsync(Guid aggregateId, CancellationToken cancellationToken)
    {
        var semaphore = locks.GetOrAdd(aggregateId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) =>
        AcquireAsync(Guid.Empty, cancellationToken);

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Persists task mementos and queue markers through the sole durable storage port. Projection updates occur only after
/// the commit marker has been stored.
/// </summary>
public sealed class ObjectStoreTaskRepository(
    IObjectStore store,
    MutualGpuObjectKeys keys,
    RepositoryLockRegistry locks,
    TaskRepositoryOptions? options = null) : ITaskRepository, ITaskSummaryReader, IQueuedTaskReader, IStartupRecovery
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TaskRepositoryOptions options = options ?? TaskRepositoryOptions.Default;

    public async Task<TaskRequest?> GetAsync(RequestorId requestorId, TaskId id, CancellationToken cancellationToken)
    {
        var snapshot = await ReadLatestFactAsync(requestorId, id, cancellationToken).ConfigureAwait(false)
            ?? await ReadAsync<TaskRequestSnapshot>(keys.TaskManifest(requestorId, id), cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : TaskRequest.Hydrate(snapshot);
    }

    public async Task<IReadOnlyList<TaskRequest>> GetByRequestorAsync(RequestorId requestorId, CancellationToken cancellationToken)
    {
        var taskIds = new HashSet<TaskId>();
        await foreach (var entry in store.ListAsync(keys.RequestorTasks(requestorId), cancellationToken).ConfigureAwait(false))
        {
            var segments = entry.Key.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var taskIndex = Array.FindIndex(segments, static segment => StringComparer.Ordinal.Equals(segment, "tasks"));
            if (taskIndex >= 0 && taskIndex + 1 < segments.Length && Guid.TryParseExact(segments[taskIndex + 1], "N", out var id))
            {
                taskIds.Add(new TaskId(id));
            }
        }

        var snapshots = new ConcurrentBag<TaskRequestSnapshot>();
        await Parallel.ForEachAsync(
            taskIds,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = options.MaximumHydrationConcurrency,
            },
            async (taskId, token) =>
            {
                var snapshot = await ReadLatestFactAsync(requestorId, taskId, token).ConfigureAwait(false)
                    ?? await ReadAsync<TaskRequestSnapshot>(keys.TaskManifest(requestorId, taskId), token).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            }).ConfigureAwait(false);

        return snapshots.Select(TaskRequest.Hydrate).OrderByDescending(static task => task.CreatedAt).ToArray();
    }

    public async Task<IReadOnlyList<TaskSummary>> ListSummariesAsync(RequestorId requestorId, CancellationToken cancellationToken)
    {
        var projection = await ReadAsync<TaskSummaryProjection>(keys.TaskSummaryProjection(requestorId), cancellationToken).ConfigureAwait(false);
        return projection?.Tasks ?? [];
    }

    public async Task<IReadOnlyList<TaskRequest>> GetQueuedAsync(CancellationToken cancellationToken)
    {
        var queued = new List<TaskRequest>();
        await foreach (var entry in store.ListAsync(new ObjectPrefix("mutualgpu/v3/queue"), cancellationToken).ConfigureAwait(false))
        {
            var marker = await ReadAsync<QueueMarker>(entry.Key, cancellationToken).ConfigureAwait(false);
            if (marker is null) continue;
            var task = await GetAsync(marker.RequestorId, marker.TaskId, cancellationToken).ConfigureAwait(false);
            if (task?.Status is MutualGPU.Domain.TaskStatus.Queued) queued.Add(task);
        }
        return queued;
    }

    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        var ids = new HashSet<(RequestorId RequestorId, TaskId TaskId)>();
        await foreach (var entry in store.ListAsync(new ObjectPrefix("mutualgpu/v3/requestors"), cancellationToken).ConfigureAwait(false))
        {
            var segments = entry.Key.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var requestorIndex = Array.FindIndex(segments, static segment => StringComparer.Ordinal.Equals(segment, "requestors"));
            var taskIndex = Array.FindIndex(segments, static segment => StringComparer.Ordinal.Equals(segment, "tasks"));
            if (requestorIndex >= 0 && requestorIndex + 1 < segments.Length && taskIndex >= 0 && taskIndex + 1 < segments.Length &&
                Guid.TryParseExact(segments[requestorIndex + 1], "N", out var requestor) && Guid.TryParseExact(segments[taskIndex + 1], "N", out var task))
                ids.Add((new RequestorId(requestor), new TaskId(task)));
        }

        var recoveredTasks = new Dictionary<(RequestorId RequestorId, TaskId TaskId), TaskRequest>();
        var recovered = 0;
        foreach (var (requestorId, taskId) in ids)
        {
            var task = await GetAsync(requestorId, taskId, cancellationToken).ConfigureAwait(false);
            var active = task?.Attempts.LastOrDefault(attempt => attempt.State is AttemptState.Assigned or AttemptState.Accepted or AttemptState.Disconnected);
            if (task is null) continue;
            if (active is not null)
            {
                task.Requeue(active.Id, active.Handle, AttemptState.Revoked, "restart_recovery");
                await SaveAsync(task, cancellationToken).ConfigureAwait(false);
                recovered++;
            }
            else
            {
                await ReconcileQueueMarkerAsync(task, cancellationToken).ConfigureAwait(false);
            }
            recoveredTasks[(requestorId, taskId)] = task;
        }

        await ReconcileStaleQueueMarkersAsync(recoveredTasks, cancellationToken).ConfigureAwait(false);
        foreach (var requestor in recoveredTasks.Values.GroupBy(static task => task.RequestorId))
        {
            var summaries = requestor
                .OrderByDescending(static task => task.CreatedAt)
                .Select(ToSummary)
                .ToArray();
            await WriteAsync(
                keys.TaskSummaryProjection(requestor.Key),
                new TaskSummaryProjection(Guid.CreateVersion7(), summaries),
                cancellationToken).ConfigureAwait(false);
        }
        return recovered;
    }

    private async Task ReconcileQueueMarkerAsync(TaskRequest task, CancellationToken cancellationToken)
    {
        var marker = keys.QueueMarker(task.Capability.Id, task.Resources, task.CreatedAt, task.Id);
        if (task.Status is not MutualGPU.Domain.TaskStatus.Queued)
        {
            await store.DeleteAsync(marker, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var existing = await store.GetAsync(marker, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return;
        await WriteAsync(
            marker,
            new QueueMarker(task.Id, task.RequestorId, task.Capability.Id, task.Resources, task.CreatedAt),
            ObjectWriteConditions.IfNotExists,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileStaleQueueMarkersAsync(
        IReadOnlyDictionary<(RequestorId RequestorId, TaskId TaskId), TaskRequest> tasks,
        CancellationToken cancellationToken)
    {
        await foreach (var entry in store.ListAsync(new ObjectPrefix("mutualgpu/v3/queue"), cancellationToken).ConfigureAwait(false))
        {
            var marker = await ReadAsync<QueueMarker>(entry.Key, cancellationToken).ConfigureAwait(false);
            if (marker is null ||
                !tasks.TryGetValue((marker.RequestorId, marker.TaskId), out var task) ||
                task.Status is not MutualGPU.Domain.TaskStatus.Queued ||
                entry.Key != keys.QueueMarker(task.Capability.Id, task.Resources, task.CreatedAt, task.Id))
            {
                await store.DeleteAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task SaveAsync(TaskRequest task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        await using var held = await locks.AcquireAsync(task.Id.Value, cancellationToken).ConfigureAwait(false);
        await using var requestorHeld = await locks.AcquireAsync(task.RequestorId.Value, cancellationToken).ConfigureAwait(false);
        var manifest = keys.TaskManifest(task.RequestorId, task.Id);
        var queueMarker = keys.QueueMarker(task.Capability.Id, task.Resources, task.CreatedAt, task.Id);
        var operationId = Guid.CreateVersion7();
        var committedAt = DateTimeOffset.UtcNow;
        var fact = keys.TaskFact(task.RequestorId, task.Id, committedAt, operationId);
        var commit = keys.Commit(operationId);
        var previous = await ReadLatestFactAsync(task.RequestorId, task.Id, cancellationToken).ConfigureAwait(false);

        // The manifest records immutable submission data once. Every state transition is
        // an append-only fact; the compact summary below is deliberately only a projection.
        if (await store.GetAsync(manifest, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            await existing.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await WriteAsync(manifest, task.ToSnapshot(), ObjectWriteConditions.IfNotExists, cancellationToken).ConfigureAwait(false);
        }
        await WriteAsync(fact, task.ToSnapshot(), ObjectWriteConditions.IfNotExists, cancellationToken).ConfigureAwait(false);
        var committedKeys = new List<string> { fact.Value };
        foreach (var changedAttempt in ChangedAttempts(previous, task))
        {
            var sequence = await NextAttemptEventSequenceAsync(task.RequestorId, task.Id, changedAttempt.Id, cancellationToken).ConfigureAwait(false);
            var attemptEvent = keys.AttemptEvent(task.RequestorId, task.Id, changedAttempt.Id, sequence, changedAttempt.State.ToString().ToLowerInvariant());
            await WriteAsync(attemptEvent, new AttemptStateEvent(changedAttempt.Id, changedAttempt.State, committedAt, changedAttempt.FailureStep), ObjectWriteConditions.IfNotExists, cancellationToken).ConfigureAwait(false);
            committedKeys.Add(attemptEvent.Value);
        }
        if (task.Status is MutualGPU.Domain.TaskStatus.Queued)
        {
            await WriteAsync(queueMarker, new QueueMarker(task.Id, task.RequestorId, task.Capability.Id, task.Resources, task.CreatedAt), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await store.DeleteAsync(queueMarker, cancellationToken).ConfigureAwait(false);
        }
        committedKeys.Add(queueMarker.Value);

        var summaryKey = keys.TaskSummaryProjection(task.RequestorId);
        var previousProjection = await ReadAsync<TaskSummaryProjection>(summaryKey, cancellationToken).ConfigureAwait(false);
        var summaries = (previousProjection?.Tasks ?? [])
            .Where(summary => summary.TaskId != task.Id)
            .Append(ToSummary(task))
            .OrderByDescending(static summary => summary.CreatedAt)
            .ToArray();
        await WriteAsync(summaryKey, new TaskSummaryProjection(operationId, summaries), cancellationToken).ConfigureAwait(false);
        committedKeys.Add(summaryKey.Value);

        await WriteAsync(commit, new CommitMarker(operationId, committedAt, committedKeys), cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> ReadAsync<T>(ObjectKey key, CancellationToken cancellationToken)
    {
        await using var read = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (read is null)
        {
            return default;
        }

        return await JsonSerializer.DeserializeAsync<T>(read.Content, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TaskRequestSnapshot?> ReadLatestFactAsync(RequestorId requestorId, TaskId taskId, CancellationToken cancellationToken)
    {
        ObjectKey? latest = null;
        await foreach (var entry in store.ListAsync(keys.TaskFacts(requestorId, taskId), cancellationToken).ConfigureAwait(false))
        {
            if (entry.Key.Value.EndsWith(".json", StringComparison.Ordinal) &&
                (latest is null || StringComparer.Ordinal.Compare(entry.Key.Value, latest.Value.Value) > 0))
            {
                latest = entry.Key;
            }
        }
        return latest is null ? null : await ReadAsync<TaskRequestSnapshot>(latest.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> NextAttemptEventSequenceAsync(RequestorId requestorId, TaskId taskId, AttemptId attemptId, CancellationToken cancellationToken)
    {
        var count = 0;
        await foreach (var _ in store.ListAsync(keys.AttemptEvents(requestorId, taskId, attemptId), cancellationToken).ConfigureAwait(false)) count++;
        return count + 1;
    }

    private static IReadOnlyList<TaskAttempt> ChangedAttempts(TaskRequestSnapshot? previous, TaskRequest task)
    {
        var existing = previous?.Attempts.ToDictionary(static attempt => attempt.Id) ?? [];
        return task.Attempts
            .Where(attempt => !existing.TryGetValue(attempt.Id, out var prior) || prior.State != attempt.State)
            .ToArray();
    }

    private Task WriteAsync<T>(ObjectKey key, T value, CancellationToken cancellationToken) => WriteAsync(key, value, ObjectWriteConditions.None, cancellationToken);

    private async Task WriteAsync<T>(ObjectKey key, T value, ObjectWriteConditions conditions, CancellationToken cancellationToken)
    {
        await using var content = new MemoryStream();
        await JsonSerializer.SerializeAsync(content, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        content.Position = 0;
        await store.PutAsync(key, content, conditions, cancellationToken).ConfigureAwait(false);
    }

    private sealed record QueueMarker(TaskId TaskId, RequestorId RequestorId, CapabilityId CapabilityId, MachineSpecifications Resources, DateTimeOffset CreatedAt);

    private sealed record CommitMarker(Guid OperationId, DateTimeOffset CommittedAt, IReadOnlyList<string> Keys);

    private sealed record AttemptStateEvent(AttemptId AttemptId, AttemptState State, DateTimeOffset OccurredAt, string? FailureStep);

    private sealed record TaskSummaryProjection(Guid OperationId, IReadOnlyList<TaskSummary> Tasks);

    private static TaskSummary ToSummary(TaskRequest task) => new(
        task.Id,
        task.Capability.Name,
        task.CreatedAt,
        task.Resources,
        task.Status,
        task.AssignmentCount,
        task.Attempts.LastOrDefault(static attempt => attempt.State is AttemptState.Failed or AttemptState.Rejected or AttemptState.Revoked)?.FailureStep);
}

public sealed record TaskRepositoryOptions
{
    public static TaskRepositoryOptions Default { get; } = new();

    public int MaximumHydrationConcurrency
    {
        get;
        init => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    } = 16;
}
