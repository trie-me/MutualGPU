using System.Collections.Concurrent;
using MutualGPU.Domain;
using NetCats.Core;
using NetCats.Runtime;

namespace MutualGPU.Api;

/// <summary>
/// Projects the operational lifetime of an assigned task into one long-lived
/// fiber. Transport messages become short child fibers, so diagnostics describe
/// work progressing rather than merely the adapter connection that carries it.
/// </summary>
public sealed class TaskAttemptFiberTracker(MutualGpuFiberOwner fibers)
{
    private readonly ConcurrentDictionary<AttemptKey, TrackedAttempt> attempts = [];

    public void Start(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId)
    {
        var key = new AttemptKey(executionUnitId, taskId, attemptId);
        if (attempts.ContainsKey(key)) return;

        var scope = fibers.ProviderSessions.CreateChild(new FiberScopeOptions("task-attempt"));
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fiber = scope.Start(
            Latent<int>.DelayAsync(token => completion.Task.WaitAsync(token)),
            new FiberDescriptor($"task-{taskId.Value.ToString("N")[..8]}"));
        var tracked = new TrackedAttempt(scope, fiber, completion);
        if (!attempts.TryAdd(key, tracked))
        {
            completion.TrySetCanceled();
            _ = scope.CloseAsync();
        }
    }

    public async Task OperationAsync(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId, string scopeName, string operation, CancellationToken cancellationToken)
    {
        if (!attempts.TryGetValue(new AttemptKey(executionUnitId, taskId, attemptId), out var tracked)) return;
        var label = String.IsNullOrWhiteSpace(operation) ? "operation" : operation.Trim();
        try
        {
            await fibers.RunObservedAsync(
                tracked.Scope,
                scopeName,
                label,
                static _ => Task.FromResult(0),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Completion won the race with a late provider progress update.
        }
    }

    public async Task CompleteAsync(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId)
    {
        if (!attempts.TryRemove(new AttemptKey(executionUnitId, taskId, attemptId), out var tracked)) return;
        tracked.Completion.TrySetResult(0);
        await tracked.Fiber.JoinAsync().ConfigureAwait(false);
        await tracked.Scope.CloseAsync().ConfigureAwait(false);
    }

    public async Task CancelExecutionUnitAsync(ExecutionUnitId executionUnitId)
    {
        var active = attempts.Where(pair => pair.Key.ExecutionUnitId == executionUnitId).ToArray();
        foreach (var (key, tracked) in active)
        {
            if (!attempts.TryRemove(key, out _)) continue;
            tracked.Fiber.RequestCancellation();
            await tracked.Scope.CloseAsync().ConfigureAwait(false);
        }
    }

    private readonly record struct AttemptKey(ExecutionUnitId ExecutionUnitId, TaskId TaskId, AttemptId AttemptId);

    private sealed record TrackedAttempt(FiberScope Scope, Fiber<int> Fiber, TaskCompletionSource<int> Completion);
}
