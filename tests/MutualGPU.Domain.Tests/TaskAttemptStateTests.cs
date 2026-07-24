using MutualGPU.Domain;

namespace MutualGPU.Domain.Tests;

public sealed class TaskAttemptStateTests
{
    [Fact]
    public void Failed_attempt_preserves_its_machine_code_and_user_facing_reason()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UnixEpoch);
        var attempt = task.Assign(AttemptId.New(), ExecutionUnitId.New(), "valid-handle", DateTimeOffset.UnixEpoch);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UnixEpoch.AddSeconds(1));

        task.Requeue(attempt.Id, attempt.Handle, AttemptState.Failed, "triposplat", "The model manifest could not be downloaded.");

        var failed = Assert.Single(task.Attempts);
        Assert.Equal("triposplat", failed.FailureStep);
        Assert.Equal("The model manifest could not be downloaded.", failed.FailureReason);
    }

    [Fact]
    public void Non_retryable_failure_ends_the_task_on_its_first_attempt()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "flux2-klein-4b", [], new OutputDefinition(), "hash");
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UnixEpoch);
        var attempt = task.Assign(AttemptId.New(), ExecutionUnitId.New(), "valid-handle", DateTimeOffset.UnixEpoch);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UnixEpoch.AddSeconds(1));

        task.Fail(attempt.Id, attempt.Handle, "content_safety", "The generated image was blocked as inappropriate content.");

        var failed = Assert.Single(task.Attempts);
        Assert.Equal(TaskStatus.Failed, task.Status);
        Assert.Equal(AttemptState.Failed, failed.State);
        Assert.Equal("content_safety", failed.FailureStep);
        Assert.Equal("The generated image was blocked as inappropriate content.", failed.FailureReason);
    }

    [Fact]
    public void Disconnected_attempt_can_only_be_rebound_by_its_current_handle()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UnixEpoch);
        var attempt = task.Assign(AttemptId.New(), ExecutionUnitId.New(), "valid-handle", DateTimeOffset.UnixEpoch);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UnixEpoch.AddSeconds(1));
        task.Disconnect(attempt.Id, attempt.Handle, DateTimeOffset.UnixEpoch.AddSeconds(2));

        Assert.Throws<DomainRuleViolation>(() => task.Rebind(attempt.Id, "other-handle"));
        task.Rebind(attempt.Id, attempt.Handle);

        Assert.Equal(TaskStatus.Running, task.Status);
        Assert.Equal(AttemptState.Accepted, task.Attempts.Single().State);
        Assert.Null(task.Attempts.Single().DisconnectedAt);
    }

    [Fact]
    public void Fourth_provider_failure_ends_the_task_and_preserves_the_actionable_reason()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "TripoSplat", [], new OutputDefinition(), "hash");
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UnixEpoch);

        for (var index = 1; index <= 4; index++)
        {
            var attempt = task.Assign(AttemptId.New(), ExecutionUnitId.New(), $"handle-{index}", DateTimeOffset.UnixEpoch.AddMinutes(index));
            task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UnixEpoch.AddMinutes(index).AddSeconds(1));
            task.Requeue(attempt.Id, attempt.Handle, AttemptState.Failed, "triposplat", "ONNX inference failed for 'triposplat/vae'.");
        }

        Assert.Equal(TaskStatus.Failed, task.Status);
        Assert.Equal(4, task.AssignmentCount);
        var failed = task.Attempts[^1];
        Assert.Equal(AttemptState.Failed, failed.State);
        Assert.Equal("triposplat", failed.FailureStep);
        Assert.Equal("ONNX inference failed for 'triposplat/vae'.", failed.FailureReason);
        Assert.Null(task.Result);
    }

    [Fact]
    public void Requestor_cancellation_is_terminal_and_invalidates_the_active_handle()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UnixEpoch);
        var attempt = task.Assign(AttemptId.New(), ExecutionUnitId.New(), "valid-handle", DateTimeOffset.UnixEpoch);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UnixEpoch.AddSeconds(1));

        var cancelled = task.Cancel();

        Assert.Equal(attempt.Id, cancelled?.Id);
        Assert.Equal(TaskStatus.Cancelled, task.Status);
        Assert.Equal(AttemptState.Cancelled, task.Attempts.Single().State);
        Assert.Throws<DomainRuleViolation>(() => task.Complete(attempt.Id, attempt.Handle, new TaskResult(new ResultArtifact(ArtifactId.New(), "application/zip", 1, "digest"))));
    }
}
