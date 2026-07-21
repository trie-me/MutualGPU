using MutualGPU.Domain;

namespace MutualGPU.Application;

public interface IExecutionUnitRepository
{
    Task<ExecutionUnit?> GetAsync(ExecutionUnitId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<CapabilityDefinition>> GetCapabilitiesAsync(CancellationToken cancellationToken);

    Task SaveAsync(ExecutionUnit executionUnit, CancellationToken cancellationToken);
}

/// <summary>Serializes the read/resolve/save enrollment transaction inside the
/// single-process MVP. A multi-replica design will replace this with distributed
/// coordination rather than weakening contract uniqueness here.</summary>
public interface IEnrollmentGate
{
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
}

public interface ITaskRepository
{
    Task<TaskRequest?> GetAsync(RequestorId requestorId, TaskId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaskRequest>> GetByRequestorAsync(RequestorId requestorId, CancellationToken cancellationToken);

    Task SaveAsync(TaskRequest task, CancellationToken cancellationToken);
}

public sealed record TaskSummary(
    TaskId TaskId,
    string CapabilityName,
    DateTimeOffset CreatedAt,
    MachineSpecifications Resources,
    MutualGPU.Domain.TaskStatus Status,
    int AttemptCount,
    string? FailureStep = null,
    string? FailureReason = null);

public interface ITaskSummaryReader
{
    Task<IReadOnlyList<TaskSummary>> ListSummariesAsync(RequestorId requestorId, CancellationToken cancellationToken);
}

public interface IQueuedTaskReader
{
    Task<IReadOnlyList<TaskRequest>> GetQueuedAsync(CancellationToken cancellationToken);
}

public interface IStartupRecovery
{
    Task<int> RecoverAsync(CancellationToken cancellationToken);
}

public interface IEnrollmentStartupRecovery
{
    Task<int> RecoverAsync(CancellationToken cancellationToken);
}

public interface IProviderPresence
{
    IReadOnlyList<ProviderCandidate> GetConnectedCandidates(CapabilityId capabilityId);
}

public sealed record ProviderCandidate(
    ExecutionUnitId ExecutionUnitId,
    CapabilityId CapabilityId,
    ResourceTier Tier,
    MachineSpecifications Specifications,
    bool IsIdle);

/// <summary>Internal assignment data. The requestor identity is used only by the
/// transport adapter to create one scoped presigned input URL; it never crosses the wire.</summary>
public sealed record ProviderInputAssignment(
    RequestorId RequestorId,
    ArtifactId ArtifactId,
    string Extension,
    string ContentType,
    long Length,
    string Sha256);

public sealed record ProviderAssignment(
    TaskId TaskId,
    AttemptId AttemptId,
    string Handle,
    IReadOnlyDictionary<string, string> Scalars,
    ProviderInputAssignment? Input = null);

public interface IProviderAssignments
{
    bool TryDeliver(ExecutionUnitId executionUnitId, ProviderAssignment assignment);

    void Track(ExecutionUnitId executionUnitId, TaskRequest task, TaskAttempt attempt);

    bool TryGet(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId, string handle, out TaskRequest task);

    void Remove(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId);

    IReadOnlyList<ActiveProviderAssignment> GetUnacceptedBefore(DateTimeOffset deadline);

    IReadOnlyList<ActiveProviderAssignment> GetForExecutionUnit(ExecutionUnitId executionUnitId);

    IReadOnlyList<ActiveProviderAssignment> GetDisconnectedBefore(DateTimeOffset deadline);

    bool TryGetByHandle(ExecutionUnitId executionUnitId, string handle, out ActiveProviderAssignment assignment);
}

public sealed record ActiveProviderAssignment(ExecutionUnitId ExecutionUnitId, TaskRequest Task, TaskAttempt Attempt);

public sealed record ResultUploadAuthorization(string Token, DateTimeOffset ExpiresAt);

public interface IResultUploadAuthorizations
{
    ResultUploadAuthorization Issue(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, DateTimeOffset now);
    bool TryConsume(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string token, DateTimeOffset now);
}

public sealed record StagedResult(string Receipt, TaskResult Result);

public interface IStagedResults
{
    void Stage(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, StagedResult result);
    bool TryTake(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string receipt, out StagedResult result);
}

public sealed record TaskProgress(ulong SequenceNumber, DateTimeOffset ObservedAt, string? Phase = null, double? Percent = null, string? Message = null);

public interface IProviderProgress
{
    bool TryReport(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, TaskProgress progress);
    TaskProgress? Get(TaskId taskId);
    void Remove(TaskId taskId);
}

public interface IApplicationEventSink
{
    void TriggerScheduler();
}
