using MutualGPU.Domain;

namespace MutualGPU.Domain.Tests;

public sealed class ResourceModelTests
{
    [Fact]
    public void Enrollment_requires_a_concrete_scheduling_tier_and_valid_machine_specifications()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "render", [], new OutputDefinition(), "hash");

        var automatic = new EnrollmentDefinition(
            new MachineProfile(ResourceTier.Automatic, new MachineSpecifications(ResourceTier.Large, 32)),
            [capability]);
        var invalidMemory = new EnrollmentDefinition(
            new MachineProfile(ResourceTier.Large, new MachineSpecifications(ResourceTier.Large, 0)),
            [capability]);

        Assert.Equal("machine_tier_invalid", Assert.Throws<DomainRuleViolation>(automatic.Validate).Code);
        Assert.Equal("machine_specifications_invalid", Assert.Throws<DomainRuleViolation>(invalidMemory.Validate).Code);
    }

    [Fact]
    public void Task_requests_require_a_concrete_cpu_gpu_tier_and_positive_memory()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "render", [], new OutputDefinition(), "hash");

        _ = new TaskRequest(
            TaskId.New(), RequestorId.New(), capability, new MachineSpecifications(ResourceTier.Large, 32),
            new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UnixEpoch);

        var violation = Assert.Throws<DomainRuleViolation>(() => new TaskRequest(
            TaskId.New(), RequestorId.New(), capability, new MachineSpecifications(ResourceTier.Unspecified, 0),
            new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UnixEpoch));
        Assert.Equal("task_resources_invalid", violation.Code);
    }
}
