using MutualGPU.Domain;
using NetCats.Core;

namespace MutualGPU.Application;

/// <summary>Shared provider-message state transitions used by every transport adapter.</summary>
public sealed class ProviderSessionApplication(
    ITaskRepository tasks,
    IProviderAssignments assignments,
    IStagedResults stagedResults,
    IProviderProgress progress,
    IApplicationEventSink events,
    TimeProvider timeProvider,
    IOperationUnitOfWork? operations = null)
{
    private static readonly HashSet<string> TerminalFailureSteps = new(StringComparer.OrdinalIgnoreCase) { "content_safety" };

    public Latent<bool> Accept(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, DateTimeOffset acceptedAt) =>
        Transition(unitId, taskId, attemptId, handle, task => task.Accept(attemptId, handle, acceptedAt), remove: false);

    public Latent<bool> Reject(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string? reason) =>
        Transition(unitId, taskId, attemptId, handle, task => task.Requeue(attemptId, handle, AttemptState.Rejected, "provider_rejected", reason), remove: true);

    public Latent<bool> Fail(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string? step, string? reason) =>
        Transition(unitId, taskId, attemptId, handle, task =>
        {
            if (step is not null && TerminalFailureSteps.Contains(step)) task.Fail(attemptId, handle, step, reason);
            else task.Requeue(attemptId, handle, AttemptState.Failed, step, reason);
        }, remove: true);

    public Latent<bool> Complete(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string receipt) => Latent<bool>.DelayAsync(async cancellationToken =>
    {
        if (operations is not null)
        {
            var durableStaged = await stagedResults
                .GetAsync(unitId, taskId, attemptId, handle, receipt, cancellationToken)
                .ConfigureAwait(false);
            if (durableStaged is null)
            {
                return await stagedResults
                    .IsCompletedAsync(unitId, taskId, attemptId, handle, receipt, cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                var completed = await operations.ExecuteAsync(
                    async (context, token) =>
                    {
                        var upload = await context.ResultUploads
                            .GetByReceiptAsync(taskId, attemptId, receipt, token)
                            .ConfigureAwait(false);
                        if (upload is null || upload.ExecutionUnitId != unitId) return false;
                        if (upload.State is ResultUploadState.Completed) return true;
                        if (upload.State is not ResultUploadState.Uploaded) return false;
                        var task = await context.Tasks.GetAsync(taskId, token).ConfigureAwait(false);
                        if (task is null) return false;
                        task.Complete(attemptId, handle, durableStaged.Result);
                        context.Tasks.Update(task);
                        context.ResultUploads.Update(upload with
                        {
                            State = ResultUploadState.Completed,
                            CompletedAt = timeProvider.GetUtcNow(),
                            Version = upload.Version + 1,
                        });
                        var artifacts = await context.Artifacts.GetForTaskAsync(taskId, token).ConfigureAwait(false);
                        foreach (var artifact in artifacts.Where(artifact =>
                            artifact.ResultUploadOperationId == upload.Id &&
                            artifact.State is ArtifactState.Staged))
                        {
                            context.Artifacts.Update(artifact with { State = ArtifactState.Available });
                        }
                        context.Publish(OperationEvent.TaskChanged(task.RequestorId, timeProvider.GetUtcNow()));
                        context.Publish(OperationEvent.Scheduler(timeProvider.GetUtcNow()));
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!completed) return false;
                assignments.Remove(unitId, taskId, attemptId);
                progress.Remove(taskId, attemptId);
                return true;
            }
            catch (DomainRuleViolation)
            {
                return false;
            }
            catch (OptimisticConcurrencyException)
            {
                return await stagedResults
                    .IsCompletedAsync(unitId, taskId, attemptId, handle, receipt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (!assignments.TryGet(unitId, taskId, attemptId, handle, out var task))
            return stagedResults.IsCompleted(unitId, taskId, attemptId, handle, receipt);
        if (!stagedResults.TryTake(unitId, taskId, attemptId, handle, receipt, out var staged)) return false;
        try
        {
            task.Complete(attemptId, handle, staged.Result);
            await tasks.SaveAsync(task, cancellationToken).ConfigureAwait(false);
            assignments.Remove(unitId, taskId, attemptId);
            progress.Remove(taskId, attemptId);
            stagedResults.MarkCompleted(unitId, taskId, attemptId, handle, receipt);
            events.TaskChanged(task.RequestorId);
            events.TriggerScheduler();
            return true;
        }
        catch (DomainRuleViolation) { return false; }
    });

    public ProviderProgressDisposition ReportProgress(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, TaskProgress update)
    {
        var disposition = progress.Report(unitId, taskId, attemptId, handle, update);
        if (disposition is ProviderProgressDisposition.Accepted && assignments.TryGet(unitId, taskId, attemptId, handle, out var task))
        {
            events.TaskChanged(task.RequestorId);
        }
        return disposition;
    }

    public Latent<int> Disconnect(ExecutionUnitId unitId) => Latent<int>.DelayAsync(async cancellationToken =>
    {
        var changed = 0;
        foreach (var active in assignments.GetForExecutionUnit(unitId))
        {
            if (operations is not null)
            {
                try
                {
                    var disconnected = await operations.ExecuteAsync(
                        async (context, token) =>
                        {
                            var current = await context.Tasks.GetAsync(active.Task.Id, token).ConfigureAwait(false);
                            var attempt = current?.Attempts.SingleOrDefault(item => item.Id == active.Attempt.Id);
                            if (current is null ||
                                attempt?.ExecutionUnitId != unitId ||
                                attempt.State is not AttemptState.Accepted)
                            {
                                return (Task: (TaskRequest?)null, Attempt: (TaskAttempt?)null);
                            }
                            current.Disconnect(attempt.Id, attempt.Handle, timeProvider.GetUtcNow());
                            context.Tasks.Update(current);
                            context.Publish(OperationEvent.TaskChanged(current.RequestorId, timeProvider.GetUtcNow()));
                            return (
                                Task: (TaskRequest?)current,
                                Attempt: current.Attempts.Single(item => item.Id == attempt.Id));
                        },
                        cancellationToken).ConfigureAwait(false);
                    if (disconnected.Task is not null && disconnected.Attempt is not null)
                    {
                        assignments.Track(unitId, disconnected.Task, disconnected.Attempt);
                        changed++;
                    }
                }
                catch (OptimisticConcurrencyException)
                {
                }
                catch (DomainRuleViolation)
                {
                }
                continue;
            }

            try
            {
                if (active.Task.Attempts.SingleOrDefault(attempt => attempt.Id == active.Attempt.Id)?.State is not AttemptState.Accepted) continue;
                active.Task.Disconnect(active.Attempt.Id, active.Attempt.Handle, timeProvider.GetUtcNow());
                changed++;
            }
            catch (DomainRuleViolation) { }
        }
        return changed;
    });

    public Latent<bool> Rebind(ExecutionUnitId unitId, string handle) => Latent<bool>.DelayAsync(async cancellationToken =>
    {
        assignments.TryGetByHandle(unitId, handle, out var active);
        if (operations is not null)
        {
            var candidate = active?.Task ?? await tasks
                .GetActiveByHandleAsync(unitId, handle, cancellationToken)
                .ConfigureAwait(false);
            if (candidate is null) return false;
            try
            {
                var rebound = await operations.ExecuteAsync(
                    async (context, token) =>
                    {
                        var current = await context.Tasks.GetAsync(candidate.Id, token).ConfigureAwait(false);
                        var attempt = current?.Attempts.SingleOrDefault(item =>
                            item.ExecutionUnitId == unitId &&
                            StringComparer.Ordinal.Equals(item.Handle, handle) &&
                            item.State is AttemptState.Accepted or AttemptState.Disconnected);
                        if (current is null || attempt is null)
                            return (Task: (TaskRequest?)null, Attempt: (TaskAttempt?)null);
                        if (attempt.State is AttemptState.Disconnected)
                        {
                            current.Rebind(attempt.Id, handle);
                            context.Tasks.Update(current);
                            context.Publish(OperationEvent.TaskChanged(current.RequestorId, timeProvider.GetUtcNow()));
                        }
                        return (
                            Task: (TaskRequest?)current,
                            Attempt: current.Attempts.Single(item => item.Id == attempt.Id));
                    },
                    cancellationToken).ConfigureAwait(false);
                if (rebound.Task is null || rebound.Attempt is null) return false;
                assignments.Track(unitId, rebound.Task, rebound.Attempt);
                return true;
            }
            catch (OptimisticConcurrencyException)
            {
                return false;
            }
            catch (DomainRuleViolation)
            {
                return false;
            }
        }

        if (active is null) return false;
        try
        {
            active.Task.Rebind(active.Attempt.Id, handle);
            return true;
        }
        catch (DomainRuleViolation) { return false; }
    });

    public Latent<int> RevokeDisconnected(DateTimeOffset deadline) => Latent<int>.DelayAsync(async cancellationToken =>
    {
        var changed = 0;
        var candidates = assignments.GetDisconnectedBefore(deadline).ToList();
        if (operations is not null)
        {
            var durable = await tasks
                .GetByAttemptStateBeforeAsync(AttemptState.Disconnected, deadline, cancellationToken)
                .ConfigureAwait(false);
            candidates.AddRange(durable.SelectMany(task => task.Attempts
                .Where(attempt =>
                    attempt.State is AttemptState.Disconnected &&
                    attempt.DisconnectedAt <= deadline)
                .Select(attempt => new ActiveProviderAssignment(
                    attempt.ExecutionUnitId,
                    task,
                    attempt))));
        }
        foreach (var active in candidates
            .DistinctBy(static item => (item.Task.Id, item.Attempt.Id)))
        {
            if (operations is not null)
            {
                if (await RequeuePersistentAsync(
                    active,
                    "disconnect_recovery_expired",
                    "The provider disconnected and did not reconnect before the recovery window expired.",
                    cancellationToken).ConfigureAwait(false))
                {
                    assignments.Remove(active.ExecutionUnitId, active.Task.Id, active.Attempt.Id);
                    progress.Remove(active.Task.Id, active.Attempt.Id);
                    changed++;
                }
                continue;
            }

            try
            {
                active.Task.Requeue(active.Attempt.Id, active.Attempt.Handle, AttemptState.Revoked, "disconnect_recovery_expired", "The provider disconnected and did not reconnect before the recovery window expired.");
                await tasks.SaveAsync(active.Task, cancellationToken).ConfigureAwait(false);
                assignments.Remove(active.ExecutionUnitId, active.Task.Id, active.Attempt.Id);
                progress.Remove(active.Task.Id, active.Attempt.Id);
                events.TaskChanged(active.Task.RequestorId);
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
            if (operations is not null)
            {
                if (active.Task.Attempts.SingleOrDefault(attempt => attempt.Id == active.Attempt.Id)?.State is not AttemptState.Disconnected) continue;
                if (await RequeuePersistentAsync(
                    active,
                    "disconnect_recovery_expired",
                    "The provider disconnected and did not reconnect before the recovery window expired.",
                    cancellationToken).ConfigureAwait(false))
                {
                    assignments.Remove(unitId, active.Task.Id, active.Attempt.Id);
                    progress.Remove(active.Task.Id, active.Attempt.Id);
                    changed++;
                }
                continue;
            }

            try
            {
                if (active.Task.Attempts.SingleOrDefault(attempt => attempt.Id == active.Attempt.Id)?.State is not AttemptState.Disconnected) continue;
                active.Task.Requeue(active.Attempt.Id, active.Attempt.Handle, AttemptState.Revoked, "disconnect_recovery_expired", "The provider disconnected and did not reconnect before the recovery window expired.");
                await tasks.SaveAsync(active.Task, cancellationToken).ConfigureAwait(false);
                assignments.Remove(unitId, active.Task.Id, active.Attempt.Id);
                progress.Remove(active.Task.Id, active.Attempt.Id);
                events.TaskChanged(active.Task.RequestorId);
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
        var candidates = assignments.GetUnacceptedBefore(deadline).ToList();
        if (operations is not null)
        {
            var durable = await tasks
                .GetByAttemptStateBeforeAsync(AttemptState.Assigned, deadline, cancellationToken)
                .ConfigureAwait(false);
            candidates.AddRange(durable.SelectMany(task => task.Attempts
                .Where(attempt =>
                    attempt.State is AttemptState.Assigned &&
                    attempt.AssignedAt <= deadline)
                .Select(attempt => new ActiveProviderAssignment(
                    attempt.ExecutionUnitId,
                    task,
                    attempt))));
        }
        foreach (var active in candidates
            .DistinctBy(static item => (item.Task.Id, item.Attempt.Id)))
        {
            if (operations is not null)
            {
                if (await RequeuePersistentAsync(
                    active,
                    "acknowledgement_timeout",
                    "The provider did not accept the task before the acknowledgement deadline.",
                    cancellationToken).ConfigureAwait(false))
                {
                    assignments.Remove(active.ExecutionUnitId, active.Task.Id, active.Attempt.Id);
                    progress.Remove(active.Task.Id, active.Attempt.Id);
                    changed = true;
                }
                continue;
            }

            try
            {
                active.Task.Requeue(active.Attempt.Id, active.Attempt.Handle, AttemptState.Revoked, "acknowledgement_timeout", "The provider did not accept the task before the acknowledgement deadline.");
                await tasks.SaveAsync(active.Task, cancellationToken).ConfigureAwait(false);
                assignments.Remove(active.ExecutionUnitId, active.Task.Id, active.Attempt.Id);
                progress.Remove(active.Task.Id, active.Attempt.Id);
                events.TaskChanged(active.Task.RequestorId);
                changed = true;
            }
            catch (DomainRuleViolation)
            {
                assignments.Remove(active.ExecutionUnitId, active.Task.Id, active.Attempt.Id);
                progress.Remove(active.Task.Id, active.Attempt.Id);
            }
        }
        if (changed) events.TriggerScheduler();
        return changed;
    });

    private async Task<bool> RequeuePersistentAsync(
        ActiveProviderAssignment active,
        string failureStep,
        string failureReason,
        CancellationToken cancellationToken)
    {
        if (operations is null) return false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await operations.ExecuteAsync(
                    async (context, token) =>
                    {
                        var current = await context.Tasks.GetAsync(active.Task.Id, token).ConfigureAwait(false);
                        var owned = current?.Attempts.SingleOrDefault(item => item.Id == active.Attempt.Id);
                        if (current is null || owned?.ExecutionUnitId != active.ExecutionUnitId) return false;
                        current.Requeue(
                            owned.Id,
                            owned.Handle,
                            AttemptState.Revoked,
                            failureStep,
                            failureReason);
                        context.Tasks.Update(current);
                        context.Publish(OperationEvent.TaskChanged(current.RequestorId, timeProvider.GetUtcNow()));
                        context.Publish(OperationEvent.Scheduler(timeProvider.GetUtcNow()));
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OptimisticConcurrencyException) when (attempt == 0)
            {
            }
            catch (DomainRuleViolation)
            {
                return false;
            }
        }
        return false;
    }

    private Latent<bool> Transition(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, Action<TaskRequest> transition, bool remove) =>
        Latent<bool>.DelayAsync(async cancellationToken =>
        {
            if (operations is not null)
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var outcome = await operations.ExecuteAsync(
                            async (context, token) =>
                            {
                                var current = await context.Tasks.GetAsync(taskId, token).ConfigureAwait(false);
                                var owned = current?.Attempts.SingleOrDefault(item => item.Id == attemptId);
                                if (current is null || owned?.ExecutionUnitId != unitId)
                                {
                                    return (Changed: false, Task: (TaskRequest?)null, Attempt: (TaskAttempt?)null);
                                }
                                transition(current);
                                context.Tasks.Update(current);
                                context.Publish(OperationEvent.TaskChanged(current.RequestorId, timeProvider.GetUtcNow()));
                                context.Publish(OperationEvent.Scheduler(timeProvider.GetUtcNow()));
                                return (
                                    Changed: true,
                                    Task: (TaskRequest?)current,
                                    Attempt: (TaskAttempt?)current.Attempts.Single(item => item.Id == attemptId));
                            },
                            cancellationToken).ConfigureAwait(false);
                        if (!outcome.Changed) return false;
                        if (remove)
                        {
                            assignments.Remove(unitId, taskId, attemptId);
                            progress.Remove(taskId, attemptId);
                        }
                        else if (outcome.Task is not null && outcome.Attempt is not null)
                        {
                            // The connection registry is an advisory local projection, but
                            // it must immediately reflect the committed durable transition.
                            // Otherwise a provider that sends progress directly after its
                            // accepted frame is incorrectly rejected as still assigned.
                            assignments.Track(unitId, outcome.Task, outcome.Attempt);
                        }
                        return true;
                    }
                    catch (OptimisticConcurrencyException) when (attempt == 0)
                    {
                    }
                    catch (DomainRuleViolation)
                    {
                        return false;
                    }
                }
                return false;
            }

            if (!assignments.TryGet(unitId, taskId, attemptId, handle, out var task)) return false;
            try
            {
                transition(task);
                await tasks.SaveAsync(task, cancellationToken).ConfigureAwait(false);
                if (remove)
                {
                    assignments.Remove(unitId, taskId, attemptId);
                    progress.Remove(taskId, attemptId);
                }
                events.TaskChanged(task.RequestorId);
                events.TriggerScheduler();
                return true;
            }
            catch (DomainRuleViolation)
            {
                return false;
            }
        });
}
