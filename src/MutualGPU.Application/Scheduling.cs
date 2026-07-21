using MutualGPU.Domain;

namespace MutualGPU.Application;

public static class Scheduling
{
    public static IOrderedEnumerable<TaskRequest> Order(IReadOnlyCollection<TaskRequest> tasks) => tasks
        .Where(static task => task.Status is MutualGPU.Domain.TaskStatus.Queued)
        .OrderByDescending(static task => AllocationTier(task.Resources))
        .ThenByDescending(static task => (int)task.Resources.Compute + (int)task.Resources.Memory)
        .ThenBy(static task => task.CreatedAt);

    public static ProviderCandidate? SelectCandidate(TaskRequest task, IReadOnlyList<ProviderCandidate> candidates) => candidates
        .Where(candidate => candidate.IsIdle && candidate.CapabilityId == task.Capability.Id && candidate.Resources.Satisfies(task.Resources))
        .OrderBy(candidate => Distance(candidate.Resources, task.Resources))
        .ThenBy(static candidate => candidate.ExecutionUnitId.Value)
        .FirstOrDefault();

    private static int AllocationTier(ResourceProfile profile) => Math.Max((int)profile.Compute, (int)profile.Memory);

    private static int Distance(ResourceProfile candidate, ResourceProfile requested) =>
        Math.Max(0, (int)candidate.Compute - (int)requested.Compute) + Math.Max(0, (int)candidate.Memory - (int)requested.Memory);
}
