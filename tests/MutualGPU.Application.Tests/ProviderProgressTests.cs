using MutualGPU.Application;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;

namespace MutualGPU.Application.Tests;

public sealed class ProviderProgressTests
{
    [Fact]
    public void Progress_requires_an_owned_accepted_attempt_and_drops_only_stale_sequences()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var unit = new ExecutionUnit(ExecutionUnitId.New(), new EnrollmentDefinition(new MachineProfile(ResourceTier.Medium, new MachineSpecifications(ResourceTier.Medium, 16)), [capability]));
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        var attempt = task.Assign(AttemptId.New(), unit.Id, "opaque", DateTimeOffset.UtcNow);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UtcNow);
        var registry = new ProviderConnectionRegistry();
        registry.Connect(unit);
        registry.Track(unit.Id, task, attempt);
        var first = new TaskProgress(1, DateTimeOffset.UtcNow, "render", 10, "warming up");

        Assert.Equal(ProviderProgressDisposition.Accepted, registry.Report(unit.Id, task.Id, attempt.Id, attempt.Handle, first));
        Assert.Equal(ProviderProgressDisposition.Accepted, registry.Report(unit.Id, task.Id, attempt.Id, attempt.Handle, first with { SequenceNumber = 2, ObservedAt = first.ObservedAt.AddMilliseconds(1) }));
        Assert.Equal(ProviderProgressDisposition.DroppedStaleSequence, registry.Report(unit.Id, task.Id, attempt.Id, attempt.Handle, first with { SequenceNumber = 1, ObservedAt = first.ObservedAt.AddSeconds(2) }));
        Assert.Equal(ProviderProgressDisposition.Accepted, registry.Report(unit.Id, task.Id, attempt.Id, attempt.Handle, first with { SequenceNumber = 3, ObservedAt = first.ObservedAt.AddSeconds(1) }));
        Assert.Equal((ulong)3, registry.Get(task.Id)!.SequenceNumber);
    }

    [Fact]
    public void Progress_is_scoped_to_the_attempt_and_a_replacement_starts_at_sequence_one()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var unit = new ExecutionUnit(ExecutionUnitId.New(), new EnrollmentDefinition(new MachineProfile(ResourceTier.Medium, new MachineSpecifications(ResourceTier.Medium, 16)), [capability]));
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        var first = task.Assign(AttemptId.New(), unit.Id, "first", DateTimeOffset.UtcNow);
        task.Accept(first.Id, first.Handle, DateTimeOffset.UtcNow);
        var registry = new ProviderConnectionRegistry();
        registry.Connect(unit);
        registry.Track(unit.Id, task, first);
        Assert.Equal(ProviderProgressDisposition.Accepted, registry.Report(unit.Id, task.Id, first.Id, first.Handle, new TaskProgress(999, DateTimeOffset.UtcNow)));

        task.Requeue(first.Id, first.Handle, AttemptState.Failed, "execution", "retry");
        registry.Remove(unit.Id, task.Id, first.Id);
        registry.Remove(task.Id, first.Id);
        var second = task.Assign(AttemptId.New(), unit.Id, "second", DateTimeOffset.UtcNow);
        task.Accept(second.Id, second.Handle, DateTimeOffset.UtcNow);
        registry.Track(unit.Id, task, second);

        Assert.Equal(ProviderProgressDisposition.Accepted, registry.Report(unit.Id, task.Id, second.Id, second.Handle, new TaskProgress(1, DateTimeOffset.UtcNow)));
        Assert.Equal((ulong)1, registry.Get(task.Id)!.SequenceNumber);
    }

    [Fact]
    public void Progress_classifies_every_invalid_handle_family_without_mutating_progress()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var unit = new ExecutionUnit(ExecutionUnitId.New(), new EnrollmentDefinition(new MachineProfile(ResourceTier.Medium, new MachineSpecifications(ResourceTier.Medium, 16)), [capability]));
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        var attempt = task.Assign(AttemptId.New(), unit.Id, "opaque", DateTimeOffset.UtcNow);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UtcNow);
        var pendingTask = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        var pendingAttempt = pendingTask.Assign(AttemptId.New(), unit.Id, "pending", DateTimeOffset.UtcNow);
        var otherUnit = new ExecutionUnit(ExecutionUnitId.New(), new EnrollmentDefinition(new MachineProfile(ResourceTier.Medium, new MachineSpecifications(ResourceTier.Medium, 16)), [capability]));
        var registry = new ProviderConnectionRegistry();
        registry.Track(unit.Id, task, attempt);
        registry.Track(unit.Id, pendingTask, pendingAttempt);

        Assert.Equal(ProviderProgressDisposition.RejectedUnknownAssignment, registry.Report(unit.Id, TaskId.New(), AttemptId.New(), "unknown", new TaskProgress(1, DateTimeOffset.UtcNow)));
        Assert.Equal(ProviderProgressDisposition.RejectedWrongExecutionUnit, registry.Report(otherUnit.Id, task.Id, attempt.Id, attempt.Handle, new TaskProgress(1, DateTimeOffset.UtcNow)));
        Assert.Equal(ProviderProgressDisposition.RejectedWrongHandle, registry.Report(unit.Id, task.Id, attempt.Id, "not-the-handle", new TaskProgress(1, DateTimeOffset.UtcNow)));
        Assert.Equal(ProviderProgressDisposition.RejectedAttemptState, registry.Report(unit.Id, pendingTask.Id, pendingAttempt.Id, pendingAttempt.Handle, new TaskProgress(1, DateTimeOffset.UtcNow)));
        Assert.Equal(ProviderProgressDisposition.RejectedMalformed, registry.Report(unit.Id, task.Id, attempt.Id, attempt.Handle, new TaskProgress(1, DateTimeOffset.UtcNow, Percent: Double.NaN)));
        Assert.Null(registry.Get(task.Id));
    }
}
