using System.Security.Cryptography;
using MutualGPU.Domain;
using NetCats.Core;

namespace MutualGPU.Application;

public sealed class SchedulerApplication(
    IQueuedTaskReader queue,
    IProviderPresence presence,
    ITaskRepository tasks,
    IProviderAssignments assignments)
{
    public Latent<int> Evaluate(DateTimeOffset now) => Latent<int>.DelayAsync(async cancellationToken =>
    {
        var assigned = 0;
        foreach (var task in Scheduling.Order(await queue.GetQueuedAsync(cancellationToken).ConfigureAwait(false)))
        {
            var candidate = Scheduling.SelectCandidate(task, presence.GetConnectedCandidates(task.Capability.Id));
            if (candidate is null) continue;
            var attempt = task.Assign(AttemptId.New(), candidate.ExecutionUnitId, NewHandle(), now);
            await tasks.SaveAsync(task, cancellationToken).ConfigureAwait(false);
            assignments.Track(candidate.ExecutionUnitId, task, attempt);
            var input = task.Parameters.Image is { } image
                ? new ProviderInputAssignment(
                    task.RequestorId,
                    image,
                    task.Parameters.ImageExtension ?? "bin",
                    task.Parameters.ImageContentType ?? "application/octet-stream",
                    task.Parameters.ImageLength ?? 0,
                    task.Parameters.ImageSha256 ?? String.Empty)
                : null;
            if (!assignments.TryDeliver(candidate.ExecutionUnitId, new ProviderAssignment(task.Id, attempt.Id, attempt.Handle, task.Parameters.Scalars, input)))
            {
                task.Requeue(attempt.Id, attempt.Handle, AttemptState.Revoked, "delivery_failed");
                await tasks.SaveAsync(task, cancellationToken).ConfigureAwait(false);
                assignments.Remove(candidate.ExecutionUnitId, task.Id, attempt.Id);
                continue;
            }
            assigned++;
        }
        return assigned;
    });

    private static string NewHandle() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
