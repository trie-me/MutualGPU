using MutualGPU.Domain;
using NetCats.Core;

namespace MutualGPU.Application;

public sealed record CapabilityAvailability(
    CapabilityDefinition Capability,
    IReadOnlyList<MachineAvailability> Machines);

public sealed record MachineAvailability(MachineSpecifications Specifications, int ConnectedCount, int IdleCount);

public sealed class CapabilityCatalogueApplication(IExecutionUnitRepository units, IProviderPresence presence)
{
    public Latent<IReadOnlyList<CapabilityAvailability>> List() => Latent<IReadOnlyList<CapabilityAvailability>>.DelayAsync(async cancellationToken =>
    {
        var capabilities = await units.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        return capabilities.Select(capability =>
        {
            var candidates = presence.GetConnectedCandidates(capability.Id);
            var machines = candidates.GroupBy(static candidate => candidate.Specifications)
                .Select(group => new MachineAvailability(group.Key, group.Count(), group.Count(static candidate => candidate.IsIdle)))
                .OrderByDescending(static availability => availability.Specifications.ComputeTier)
                .ThenBy(static availability => availability.Specifications.MemoryGiB)
                .ToArray();
            return new CapabilityAvailability(capability, machines);
        }).Where(static item => item.Machines.Count > 0).OrderBy(static item => item.Capability.Name, StringComparer.Ordinal).ToArray();
    });
}
