using System.Text.Json.Serialization;
using MutualGPU.Domain;

namespace MutualGPU.Application;

public interface IExecutionUnitRepository
{
    Task<ExecutionUnit?> GetAsync(ExecutionUnitId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<CapabilityDefinition>> GetCapabilitiesAsync(CancellationToken cancellationToken);

    Task SaveAsync(ExecutionUnit executionUnit, CancellationToken cancellationToken);

    void Add(ExecutionUnit executionUnit) =>
        throw new NotSupportedException("Writes are available only inside an operation unit of work.");

    void Update(ExecutionUnit executionUnit) =>
        throw new NotSupportedException("Writes are available only inside an operation unit of work.");
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

    Task<TaskRequest?> GetAsync(TaskId id, CancellationToken cancellationToken) =>
        Task.FromResult<TaskRequest?>(null);

    Task<TaskRequest?> GetActiveByHandleAsync(
        ExecutionUnitId executionUnitId,
        string handle,
        CancellationToken cancellationToken) =>
        Task.FromResult<TaskRequest?>(null);

    Task<IReadOnlyList<TaskRequest>> GetByAttemptStateBeforeAsync(
        AttemptState state,
        DateTimeOffset deadline,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TaskRequest>>([]);

    async Task<TaskRequest?> GetByIdempotencyKeyAsync(
        RequestorId requestorId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        (await GetByRequestorAsync(requestorId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(task => StringComparer.Ordinal.Equals(task.Parameters.IdempotencyKey, idempotencyKey));

    void Add(TaskRequest task) =>
        throw new NotSupportedException("Writes are available only inside an operation unit of work.");

    void Update(TaskRequest task) =>
        throw new NotSupportedException("Writes are available only inside an operation unit of work.");
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

public sealed record PageRequest(int Limit = 50, string? Cursor = null)
{
    public int BoundedLimit => Math.Clamp(Limit, 1, 200);
}

public sealed record PageResult<T>(IReadOnlyList<T> Items, string? NextCursor);

public interface ITaskSummaryReader
{
    Task<IReadOnlyList<TaskSummary>> ListSummariesAsync(RequestorId requestorId, CancellationToken cancellationToken);

    async Task<PageResult<TaskSummary>> ListSummariesPageAsync(
        RequestorId requestorId,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        var items = await ListSummariesAsync(requestorId, cancellationToken).ConfigureAwait(false);
        return new PageResult<TaskSummary>(items.Take(page.BoundedLimit).ToArray(), null);
    }
}

public interface IQueuedTaskReader
{
    Task<IReadOnlyList<TaskRequest>> GetQueuedAsync(CancellationToken cancellationToken);
}

/// <summary>Read-only operations projection across requestors. This is exposed only
/// through the separately authenticated administrator surface.</summary>
public interface IAdminTaskReader
{
    Task<IReadOnlyList<TaskRequest>> GetAllAsync(CancellationToken cancellationToken);

    async Task<PageResult<TaskRequest>> GetPageAsync(PageRequest page, CancellationToken cancellationToken)
    {
        var items = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return new PageResult<TaskRequest>(items.Take(page.BoundedLimit).ToArray(), null);
    }
}

/// <summary>
/// Durable review queue for partner origins. An approved origin is allowed to make
/// browser requests to MutualGPU; it is never expanded from a wildcard pattern.
/// </summary>
public interface IPartnerResourceRegistry
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<PartnerResourceRequest> SubmitAsync(PartnerResourceSubmission submission, CancellationToken cancellationToken);

    Task<IReadOnlyList<PartnerResourceRequest>> ListPendingAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PartnerResourceRequest>> ListApprovedAsync(CancellationToken cancellationToken);

    async Task<PageResult<PartnerResourceRequest>> ListPendingPageAsync(
        PageRequest page,
        CancellationToken cancellationToken)
    {
        var items = await ListPendingAsync(cancellationToken).ConfigureAwait(false);
        return new PageResult<PartnerResourceRequest>(items.Take(page.BoundedLimit).ToArray(), null);
    }

    async Task<PageResult<PartnerResourceRequest>> ListApprovedPageAsync(
        PageRequest page,
        CancellationToken cancellationToken)
    {
        var items = await ListApprovedAsync(cancellationToken).ConfigureAwait(false);
        return new PageResult<PartnerResourceRequest>(items.Take(page.BoundedLimit).ToArray(), null);
    }

    Task<PartnerResourceRequest?> ApproveAsync(Guid id, CancellationToken cancellationToken);

    Task<PartnerResourceRequest?> RevokeAsync(Guid id, CancellationToken cancellationToken);

    bool IsApprovedOrigin(string? origin);
}

/// <summary>Synchronizes the exact browser origins allowed to read presigned
/// application objects directly from the production object store.</summary>
public interface IBrowserObjectCorsPolicy
{
    Task SynchronizeAsync(IReadOnlyCollection<string> allowedOrigins, CancellationToken cancellationToken);
}

public sealed record PartnerResourceSubmission(string PartnerName, string ContactEmail, string Origin);

public sealed record PartnerResourceRequest(
    Guid Id,
    string PartnerName,
    string ContactEmail,
    string Origin,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ProcessedAt = null,
    DateTimeOffset? RevokedAt = null,
    long Version = 1);

public interface IStartupRecovery
{
    Task<int> RecoverAsync(DateTimeOffset processStartedAt, CancellationToken cancellationToken);
}

public interface IEnrollmentStartupRecovery
{
    Task<int> RecoverAsync(CancellationToken cancellationToken);
}

public interface IProviderPresence
{
    IReadOnlyList<ConnectedProviderCapability> GetConnectedCapabilities() => [];

    IReadOnlyList<ProviderCandidate> GetConnectedCandidates(CapabilityId capabilityId);
}

public sealed record ProviderCandidate(
    ExecutionUnitId ExecutionUnitId,
    CapabilityId CapabilityId,
    ResourceTier Tier,
    MachineSpecifications Specifications,
    bool IsIdle,
    Guid? SessionId = null,
    string? IpHash = null,
    string? IpClassAB = null,
    string? ProviderName = null,
    string? Transport = null);

public sealed record ConnectedProviderCapability(
    ExecutionUnitId ExecutionUnitId,
    CapabilityDefinition Capability,
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

public abstract record ProviderServerMessage;

public sealed record ProviderAssignmentMessage(ProviderAssignment Assignment) : ProviderServerMessage;

public sealed record ProviderCancellation(TaskId TaskId, AttemptId AttemptId, string Handle) : ProviderServerMessage;

public interface IProviderAssignments
{
    bool TryDeliver(ExecutionUnitId executionUnitId, ProviderAssignment assignment);

    bool TryCancel(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId, string handle);

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

    ValueTask<ResultUploadAuthorization> IssueAsync(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Issue(unitId, taskId, attemptId, handle, now));

    ValueTask<bool> TryConsumeAsync(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        string token,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(TryConsume(unitId, taskId, attemptId, handle, token, now));
}

public sealed record StagedResult(string Receipt, TaskResult Result);

public interface IStagedResults
{
    void Stage(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, StagedResult result);
    bool TryTake(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string receipt, out StagedResult result);
    void MarkCompleted(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string receipt);
    bool IsCompleted(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string receipt);

    ValueTask StageAsync(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        StagedResult result,
        CancellationToken cancellationToken)
    {
        Stage(unitId, taskId, attemptId, handle, result);
        return ValueTask.CompletedTask;
    }

    ValueTask<StagedResult?> GetAsync(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        string receipt,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(TryTake(unitId, taskId, attemptId, handle, receipt, out var result) ? result : null);

    ValueTask MarkCompletedAsync(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        string receipt,
        CancellationToken cancellationToken)
    {
        MarkCompleted(unitId, taskId, attemptId, handle, receipt);
        return ValueTask.CompletedTask;
    }

    ValueTask<bool> IsCompletedAsync(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        string receipt,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(IsCompleted(unitId, taskId, attemptId, handle, receipt));
}

public sealed record TaskProgress(ulong SequenceNumber, DateTimeOffset ObservedAt, string? Phase = null, double? Percent = null, string? Message = null);

/// <summary>Safe, closed outcomes for an advisory provider progress update.
/// Drops are deliberately distinct from authorization and lifecycle rejections.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderProgressDisposition
{
    Accepted,
    DroppedStaleSequence,
    DroppedSuperseded,
    RejectedUnknownAssignment,
    RejectedWrongExecutionUnit,
    RejectedWrongHandle,
    RejectedAttemptState,
    RejectedMalformed,
}

public interface IProviderProgress
{
    ProviderProgressDisposition Report(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, TaskProgress progress);
    TaskProgress? Get(TaskId taskId);
    void Remove(TaskId taskId, AttemptId attemptId);
}

public interface IApplicationEventSink
{
    void TriggerScheduler();

    /// <summary>Publishes a coalesced requestor-facing task-state change.</summary>
    void TaskChanged(RequestorId requestorId) { }
}
