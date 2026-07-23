using MutualGPU.Domain;
using NetCats.Core;

namespace MutualGPU.Application;

public sealed record CapabilityAvailability(
    CapabilityDefinition Capability,
    IReadOnlyList<MachineAvailability> Machines);

public sealed record MachineAvailability(MachineSpecifications Specifications, int ConnectedCount, int IdleCount);

public sealed class CapabilityCatalogueApplication(IProviderPresence presence)
{
    public Latent<IReadOnlyList<CapabilityAvailability>> List() => Latent<IReadOnlyList<CapabilityAvailability>>.DelayAsync(_ =>
    {
        var available = presence.GetConnectedCapabilities()
            .GroupBy(static item => item.Capability.Id)
            .Select(group =>
            {
                var capability = group.First().Capability;
                var machines = group.GroupBy(static item => item.Specifications)
                    .Select(machine => new MachineAvailability(machine.Key, machine.Count(), machine.Count(static item => item.IsIdle)))
                    .OrderByDescending(static item => item.Specifications.ComputeTier)
                    .ThenBy(static item => item.Specifications.MemoryGiB)
                    .ToArray();
                return new CapabilityAvailability(capability, machines);
            })
            .OrderBy(static item => item.Capability.Name, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<CapabilityAvailability>>(available);
    });
}
