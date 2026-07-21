using MutualGPU.Domain;

namespace MutualGPU.Application;

public static class Scheduling
{
    public static IOrderedEnumerable<TaskRequest> Order(IReadOnlyCollection<TaskRequest> tasks) => tasks
        .Where(static task => task.Status is MutualGPU.Domain.TaskStatus.Queued)
        .OrderBy(static task => task.CreatedAt);

    public static ProviderCandidate? SelectCandidate(TaskRequest task, IReadOnlyList<ProviderCandidate> candidates) => candidates
        .Where(candidate => candidate.IsIdle && candidate.CapabilityId == task.Capability.Id && MachineSpecificationsPolicy.Satisfies(candidate.Specifications, task.Resources))
        .OrderBy(candidate => candidate.Specifications.ComputeTier - task.Resources.ComputeTier)
        .ThenBy(candidate => candidate.Specifications.MemoryGiB - task.Resources.MemoryGiB)
        .ThenBy(static candidate => candidate.ExecutionUnitId.Value)
        .FirstOrDefault();
}
