using MutualGPU.Application;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;

namespace MutualGPU.Application.Tests;

public sealed class ProviderProgressTests
{
    [Fact]
    public void Progress_requires_owned_accepted_attempt_and_is_bounded_by_sequence_and_time()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var unit = new ExecutionUnit(ExecutionUnitId.New(), new EnrollmentDefinition(new MachineProfile(ResourceTier.Medium, ResourceTier.Medium), [capability]));
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceProfile.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        var attempt = task.Assign(AttemptId.New(), unit.Id, "opaque", DateTimeOffset.UtcNow);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UtcNow);
        var registry = new ProviderConnectionRegistry();
        registry.Connect(unit);
        registry.Track(unit.Id, task, attempt);
        var first = new TaskProgress(1, DateTimeOffset.UtcNow, "render", 10, "warming up");

        Assert.True(registry.TryReport(unit.Id, task.Id, attempt.Id, attempt.Handle, first));
        Assert.False(registry.TryReport(unit.Id, task.Id, attempt.Id, attempt.Handle, first with { SequenceNumber = 2, ObservedAt = first.ObservedAt.AddMilliseconds(500) }));
        Assert.False(registry.TryReport(unit.Id, task.Id, attempt.Id, attempt.Handle, first with { SequenceNumber = 1, ObservedAt = first.ObservedAt.AddSeconds(2) }));
        Assert.True(registry.TryReport(unit.Id, task.Id, attempt.Id, attempt.Handle, first with { SequenceNumber = 2, ObservedAt = first.ObservedAt.AddSeconds(1) }));
        Assert.Equal((ulong)2, registry.Get(task.Id)!.SequenceNumber);
    }
}
