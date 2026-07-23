using MutualGPU.Domain;
using MutualGPU.Infrastructure;

namespace MutualGPU.Application.Tests;

public sealed class CapabilityCatalogueApplicationTests
{
    [Fact]
    public async Task Catalogue_is_the_distinct_union_of_current_provider_capabilities()
    {
        var registry = new ProviderConnectionRegistry();
        var shared = new CapabilityDefinition(CapabilityId.New(), "shared", [], new OutputDefinition(), "shared-contract");
        var second = new CapabilityDefinition(CapabilityId.New(), "second", [], new OutputDefinition(), "second-contract");
        var durableOnly = new CapabilityDefinition(CapabilityId.New(), "durable-only", [], new OutputDefinition(), "durable-contract");
        var firstUnit = Unit(Machine(ResourceTier.Medium, 16), shared);
        var secondUnit = Unit(Machine(ResourceTier.Large, 32), shared, second);
        var firstLease = registry.Connect(firstUnit);
        registry.Connect(secondUnit, isIdle: false);
        var application = new CapabilityCatalogueApplication(registry);

        var available = await application.List().RunAsync();

        Assert.Equal(["second", "shared"], available.Select(static item => item.Capability.Name));
        Assert.DoesNotContain(available, item => item.Capability.Id == durableOnly.Id);
        var sharedAvailability = Assert.Single(available, item => item.Capability.Id == shared.Id);
        Assert.Equal(2, sharedAvailability.Machines.Sum(static machine => machine.ConnectedCount));
        Assert.Equal(1, sharedAvailability.Machines.Sum(static machine => machine.IdleCount));

        Assert.True(registry.Disconnect(firstLease));
        available = await application.List().RunAsync();

        sharedAvailability = Assert.Single(available, item => item.Capability.Id == shared.Id);
        Assert.Equal(1, sharedAvailability.Machines.Sum(static machine => machine.ConnectedCount));
        Assert.Equal(0, sharedAvailability.Machines.Sum(static machine => machine.IdleCount));
    }

    private static ExecutionUnit Unit(MachineProfile machine, params CapabilityDefinition[] capabilities) =>
        new(ExecutionUnitId.New(), new EnrollmentDefinition(machine, capabilities));

    private static MachineProfile Machine(ResourceTier tier, int memoryGiB) =>
        new(tier, new MachineSpecifications(tier, memoryGiB));
}
