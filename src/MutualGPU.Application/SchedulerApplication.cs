using System.Security.Cryptography;
using MutualGPU.Domain;
using NetCats.Core;

namespace MutualGPU.Application;

public sealed class SchedulerApplication(
    IQueuedTaskReader queue,
    IProviderPresence presence,
    ITaskRepository tasks,
    IProviderAssignments assignments,
    IApplicationEventSink? events = null,
    IOperationUnitOfWork? operations = null)
{
    public Latent<int> Evaluate(DateTimeOffset now) => Latent<int>.DelayAsync(async cancellationToken =>
    {
        var assigned = 0;
        foreach (var task in Scheduling.Order(await queue.GetQueuedAsync(cancellationToken).ConfigureAwait(false)))
        {
            var candidate = Scheduling.SelectCandidate(task, presence.GetConnectedCandidates(task.Capability.Id));
            if (candidate is null) continue;
            if (operations is not null)
            {
                (TaskRequest Task, TaskAttempt Attempt)? committed;
                try
                {
                    committed = await operations.ExecuteAsync<(TaskRequest, TaskAttempt)?>(
                        async (context, token) =>
                        {
                            var current = await context.Tasks.GetAsync(task.Id, token).ConfigureAwait(false);
                            if (current?.Status is not MutualGPU.Domain.TaskStatus.Queued) return null;
                            var attempt = current.Assign(
                                AttemptId.New(),
                                candidate.ExecutionUnitId,
                                NewHandle(),
                                now,
                                candidate.SessionId,
                                candidate.IpHash,
                                candidate.IpClassAB,
                                candidate.ProviderName,
                                candidate.Transport);
                            context.Tasks.Update(current);
                            context.Publish(OperationEvent.TaskChanged(current.RequestorId, now));
                            context.Publish(OperationEvent.AssignmentReady(current.Id, attempt.Id, candidate.ExecutionUnitId, now));
                            return (current, attempt);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OptimisticConcurrencyException)
                {
                    continue;
                }

                if (committed is null) continue;
                var (committedTask, committedAttempt) = committed.Value;
                assignments.Track(candidate.ExecutionUnitId, committedTask, committedAttempt);
                var committedInput = Input(committedTask);
                if (!assignments.TryDeliver(
                    candidate.ExecutionUnitId,
                    new ProviderAssignment(
                        committedTask.Id,
                        committedAttempt.Id,
                        committedAttempt.Handle,
                        committedTask.Parameters.Scalars,
                        committedInput)))
                {
                    try
                    {
                        await operations.ExecuteAsync(
                            async (context, token) =>
                            {
                                var current = await context.Tasks.GetAsync(committedTask.Id, token).ConfigureAwait(false);
                                var active = current?.Attempts.SingleOrDefault(item => item.Id == committedAttempt.Id);
                                if (current is null || active is null) return false;
                                current.Requeue(
                                    active.Id,
                                    active.Handle,
                                    AttemptState.Revoked,
                                    "delivery_failed",
                                    "The provider connection closed before the task could be delivered.");
                                context.Tasks.Update(current);
                                context.Publish(OperationEvent.TaskChanged(current.RequestorId, DateTimeOffset.UtcNow));
                                context.Publish(OperationEvent.Scheduler(DateTimeOffset.UtcNow));
                                return true;
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OptimisticConcurrencyException)
                    {
                    }
                    assignments.Remove(candidate.ExecutionUnitId, committedTask.Id, committedAttempt.Id);
                    continue;
                }
                assigned++;
                continue;
            }

            var attempt = task.Assign(
                AttemptId.New(),
                candidate.ExecutionUnitId,
                NewHandle(),
                now,
                candidate.SessionId,
                candidate.IpHash,
                candidate.IpClassAB,
                candidate.ProviderName,
                candidate.Transport);
            await tasks.SaveAsync(task, cancellationToken).ConfigureAwait(false);
            events?.TaskChanged(task.RequestorId);
            assignments.Track(candidate.ExecutionUnitId, task, attempt);
            var input = Input(task);
            if (!assignments.TryDeliver(candidate.ExecutionUnitId, new ProviderAssignment(task.Id, attempt.Id, attempt.Handle, task.Parameters.Scalars, input)))
            {
                task.Requeue(attempt.Id, attempt.Handle, AttemptState.Revoked, "delivery_failed", "The provider connection closed before the task could be delivered.");
                await tasks.SaveAsync(task, cancellationToken).ConfigureAwait(false);
                events?.TaskChanged(task.RequestorId);
                assignments.Remove(candidate.ExecutionUnitId, task.Id, attempt.Id);
                continue;
            }
            assigned++;
        }
        return assigned;
    });

    private static ProviderInputAssignment? Input(TaskRequest task) =>
        task.Parameters.Image is { } image
            ? new ProviderInputAssignment(
                task.RequestorId,
                image,
                task.Parameters.ImageExtension ?? "bin",
                task.Parameters.ImageContentType ?? "application/octet-stream",
                task.Parameters.ImageLength ?? 0,
                task.Parameters.ImageSha256 ?? String.Empty)
            : null;

    private static string NewHandle() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
