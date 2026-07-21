using MutualGPU.Domain;
using NetCats.Core;

namespace MutualGPU.Application;

/// <summary>Shared provider-message state transitions used by every transport adapter.</summary>
public sealed class ProviderSessionApplication(ITaskRepository tasks, IProviderAssignments assignments, IStagedResults stagedResults, IProviderProgress progress, IApplicationEventSink events, TimeProvider timeProvider)
{
    public Latent<bool> Accept(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, DateTimeOffset acceptedAt) =>
        Transition(unitId, taskId, attemptId, handle, task => task.Accept(attemptId, handle, acceptedAt), remove: false);

    public Latent<bool> Reject(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string? reason) =>
        Transition(unitId, taskId, attemptId, handle, task => task.Requeue(attemptId, handle, AttemptState.Rejected, reason), remove: true);

    public Latent<bool> Fail(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string? step) =>
        Transition(unitId, taskId, attemptId, handle, task => task.Requeue(attemptId, handle, AttemptState.Failed, step), remove: true);

    public Latent<bool> Complete(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string receipt) => Latent<bool>.DelayAsync(async cancellationToken =>
    {
        if (!assignments.TryGet(unitId, taskId, attemptId, handle, out var task) || !stagedResults.TryTake(unitId, taskId, attemptId, handle, receipt, out var staged)) return false;
        try
        {
            task.Complete(attemptId, handle, staged.Result);
            await tasks.SaveAsync(task, cancellationToken).ConfigureAwait(false);
            assignments.Remove(unitId, taskId, attemptId);
            progress.Remove(taskId);
            events.TriggerScheduler();
            return true;
        }
        catch (DomainRuleViolation) { return false; }
    });

    public bool ReportProgress(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, TaskProgress update) => progress.TryReport(unitId, taskId, attemptId, handle, update);

    public Latent<int> Disconnect(ExecutionUnitId unitId) => Latent<int>.DelayAsync(async cancellationToken =>
    {
        var changed = 0;
        foreach (var active in assignments.GetForExecutionUnit(unitId))
        {
            try
            {
                if (active.Task.Attempts.SingleOrDefault(attempt => attempt.Id == active.Attempt.Id)?.State is not AttemptState.Accepted) continue;
                active.Task.Disconnect(active.Attempt.Id, active.Attempt.Handle, timeProvider.GetUtcNow());
                await tasks.SaveAsync(active.Task, cancellationToken).ConfigureAwait(false);
                changed++;
            }
            catch (DomainRuleViolation) { }
        }
        return changed;
    });

    public Latent<bool> Rebind(ExecutionUnitId unitId, string handle) => Latent<bool>.DelayAsync(async cancellationToken =>
    {
        if (!assignments.TryGetByHandle(unitId, handle, out var active)) return false;
        try
        {
            active.Task.Rebind(active.Attempt.Id, handle);
            await tasks.SaveAsync(active.Task, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DomainRuleViolation) { return false; }
    });

    public Latent<int> RevokeDisconnected(DateTimeOffset deadline) => Latent<int>.DelayAsync(async cancellationToken =>
    {
        var changed = 0;
        foreach (var active in assignments.GetDisconnectedBefore(deadline))
        {
            try
            {
                active.Task.Requeue(active.Attempt.Id, active.Attempt.Handle, AttemptState.Revoked, "disconnect_recovery_expired");
                await tasks.SaveAsync(active.Task, cancellationToken).ConfigureAwait(false);
                assignments.Remove(active.ExecutionUnitId, active.Task.Id, active.Attempt.Id);
                changed++;
            }
            catch (DomainRuleViolation) { }
        }
        if (changed > 0) events.TriggerScheduler();
        return changed;
    });

    public Latent<int> RevokeDisconnected(ExecutionUnitId unitId) => Latent<int>.DelayAsync(async cancellationToken =>
    {
        var changed = 0;
        foreach (var active in assignments.GetForExecutionUnit(unitId))
        {
            try
            {
                if (active.Task.Attempts.SingleOrDefault(attempt => attempt.Id == active.Attempt.Id)?.State is not AttemptState.Disconnected) continue;
                active.Task.Requeue(active.Attempt.Id, active.Attempt.Handle, AttemptState.Revoked, "disconnect_recovery_expired");
                await tasks.SaveAsync(active.Task, cancellationToken).ConfigureAwait(false);
                assignments.Remove(unitId, active.Task.Id, active.Attempt.Id);
                changed++;
            }
            catch (DomainRuleViolation) { }
        }
        if (changed > 0) events.TriggerScheduler();
        return changed;
    });

    public Latent<bool> RevokeExpired(DateTimeOffset deadline) => Latent<bool>.DelayAsync(async cancellationToken =>
    {
        var changed = false;
        foreach (var active in assignments.GetUnacceptedBefore(deadline))
        {
            try
            {
                active.Task.Requeue(active.Attempt.Id, active.Attempt.Handle, AttemptState.Revoked, "acknowledgement_timeout");
                await tasks.SaveAsync(active.Task, cancellationToken).ConfigureAwait(false);
                assignments.Remove(active.ExecutionUnitId, active.Task.Id, active.Attempt.Id);
                changed = true;
            }
            catch (DomainRuleViolation)
            {
                assignments.Remove(active.ExecutionUnitId, active.Task.Id, active.Attempt.Id);
            }
        }
        if (changed) events.TriggerScheduler();
        return changed;
    });

    private Latent<bool> Transition(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, Action<TaskRequest> transition, bool remove) =>
        Latent<bool>.DelayAsync(async cancellationToken =>
        {
            if (!assignments.TryGet(unitId, taskId, attemptId, handle, out var task)) return false;
            try
            {
                transition(task);
                await tasks.SaveAsync(task, cancellationToken).ConfigureAwait(false);
                if (remove) assignments.Remove(unitId, taskId, attemptId);
                events.TriggerScheduler();
                return true;
            }
            catch (DomainRuleViolation)
            {
                return false;
            }
        });
}
