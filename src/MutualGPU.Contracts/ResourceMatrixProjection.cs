using MutualGPU.Domain;

namespace MutualGPU.Contracts;

public static class ResourceMatrixProjection
{
    public static ResourceMatrixDto Create(IReadOnlyCollection<ResourceAvailabilityDto> availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        var memory = availability
            .Where(static item => item.ConnectedCount > 0)
            .Select(static item => item.Memory)
            .Distinct()
            .OrderBy(static tier => tier)
            .ToArray();
        var compute = availability
            .Where(static item => item.ConnectedCount > 0)
            .Select(static item => item.Compute)
            .Distinct()
            .OrderByDescending(static tier => tier)
            .ToArray();
        var lookup = availability.ToDictionary(static item => (item.Compute, item.Memory));
        var rows = compute.Select(tier => new ResourceMatrixRowDto(
            tier,
            memory.Select(memoryTier => lookup.TryGetValue((tier, memoryTier), out var item)
                ? new ResourceMatrixCellDto(memoryTier, item.ConnectedCount, item.IdleCount, true)
                : new ResourceMatrixCellDto(memoryTier, 0, 0, false)).ToArray())).ToArray();
        return new ResourceMatrixDto(memory, rows);
    }
}
