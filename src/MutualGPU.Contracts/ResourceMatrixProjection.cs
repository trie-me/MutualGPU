using MutualGPU.Domain;

namespace MutualGPU.Contracts;

public static class ResourceMatrixProjection
{
    public static ResourceMatrixDto Create(IReadOnlyCollection<MachineAvailabilityDto> availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        var memory = availability
            .Where(static item => item.ConnectedCount > 0)
            .Select(static item => item.MemoryGiB)
            .Distinct()
            .OrderBy(static memoryGiB => memoryGiB)
            .ToArray();
        var compute = availability
            .Where(static item => item.ConnectedCount > 0)
            .Select(static item => item.ComputeTier)
            .Distinct()
            .OrderByDescending(static tier => tier)
            .ToArray();
        var lookup = availability.ToDictionary(static item => (item.ComputeTier, item.MemoryGiB));
        var rows = compute.Select(tier => new ResourceMatrixRowDto(
            tier,
            memory.Select(memoryGiB => lookup.TryGetValue((tier, memoryGiB), out var item)
                ? new ResourceMatrixCellDto(memoryGiB, item.ConnectedCount, item.IdleCount, true)
                : new ResourceMatrixCellDto(memoryGiB, 0, 0, false)).ToArray())).ToArray();
        return new ResourceMatrixDto(memory, rows);
    }
}
