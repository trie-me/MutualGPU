using MutualGPU.Domain;

namespace MutualGPU.Domain.Tests;

public sealed class TaskAttemptStateTests
{
    [Fact]
    public void Disconnected_attempt_can_only_be_rebound_by_its_current_handle()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceProfile.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UnixEpoch);
        var attempt = task.Assign(AttemptId.New(), ExecutionUnitId.New(), "valid-handle", DateTimeOffset.UnixEpoch);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UnixEpoch.AddSeconds(1));
        task.Disconnect(attempt.Id, attempt.Handle, DateTimeOffset.UnixEpoch.AddSeconds(2));

        Assert.Throws<DomainRuleViolation>(() => task.Rebind(attempt.Id, "other-handle"));
        task.Rebind(attempt.Id, attempt.Handle);

        Assert.Equal(TaskStatus.Running, task.Status);
        Assert.Equal(AttemptState.Accepted, task.Attempts.Single().State);
    }
}
