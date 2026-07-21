using MutualGPU.Domain;
using NetCats.Core;

namespace MutualGPU.Application;

public sealed record CapabilityAvailability(
    CapabilityDefinition Capability,
    IReadOnlyList<ResourceAvailability> Resources);

public sealed record ResourceAvailability(ResourceProfile Resources, int ConnectedCount, int IdleCount);

public sealed class CapabilityCatalogueApplication(IExecutionUnitRepository units, IProviderPresence presence)
{
    public Latent<IReadOnlyList<CapabilityAvailability>> List() => Latent<IReadOnlyList<CapabilityAvailability>>.DelayAsync(async cancellationToken =>
    {
        var capabilities = await units.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        return capabilities.Select(capability =>
        {
            var candidates = presence.GetConnectedCandidates(capability.Id);
            var resources = candidates.GroupBy(static candidate => candidate.Resources)
                .Select(group => new ResourceAvailability(group.Key, group.Count(), group.Count(static candidate => candidate.IsIdle)))
                .OrderBy(static profile => profile.Resources.Compute)
                .ThenBy(static profile => profile.Resources.Memory)
                .ToArray();
            return new CapabilityAvailability(capability, resources);
        }).Where(static item => item.Resources.Count > 0).OrderBy(static item => item.Capability.Name, StringComparer.Ordinal).ToArray();
    });
}
