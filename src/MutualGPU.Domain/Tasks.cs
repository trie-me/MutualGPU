using System.Text.Json.Serialization;

namespace MutualGPU.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskStatus
{
    Queued,
    Assigned,
    Running,
    Completed,
    Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttemptState
{
    Assigned,
    Accepted,
    Rejected,
    Disconnected,
    Revoked,
    Completed,
    Failed,
}

/// <summary>
/// Immutable requestor-supplied task data. Image storage metadata travels with the
/// capability snapshot so a provider assignment can expose a scoped descriptor
/// without listing a requestor prefix in object storage.
/// </summary>
public sealed record TaskParameters(
    IReadOnlyDictionary<string, string> Scalars,
    ArtifactId? Image,
    string? IdempotencyKey = null,
    string? ImageContentType = null,
    string? ImageExtension = null,
    long? ImageLength = null,
    string? ImageSha256 = null);

public sealed record TaskAttempt(
    AttemptId Id,
    ExecutionUnitId ExecutionUnitId,
    string Handle,
    DateTimeOffset AssignedAt,
    AttemptState State,
    DateTimeOffset? AcceptedAt = null,
    string? FailureStep = null,
    string? FailureReason = null,
    DateTimeOffset? DisconnectedAt = null);

public sealed record ResultArtifact(
    ArtifactId Id,
    string ContentType,
    long Length,
    string Sha256);

public sealed record TaskResult(
    ResultArtifact Zip,
    ResultArtifact? Thumbnail = null,
    ResultArtifact? Preview = null,
    ResultArtifact? Logs = null,
    ResultArtifact? Metadata = null);

public sealed class TaskRequest
{
    private const int MaximumAssignments = 4;
    private readonly List<TaskAttempt> attempts = [];

    public TaskRequest(
        TaskId id,
        RequestorId requestorId,
        CapabilityDefinition capability,
        MachineSpecifications resources,
        TaskParameters parameters,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(parameters);
        capability.Validate();
        if (!MachineSpecificationsPolicy.IsValid(resources))
        {
            throw new DomainRuleViolation("task_resources_invalid", "A task requires a concrete CPU/GPU tier and positive memory GiB.");
        }

        Id = id;
        RequestorId = requestorId;
        Capability = capability;
        Resources = resources;
        Parameters = parameters;
        CreatedAt = createdAt;
        Status = TaskStatus.Queued;
    }

    public TaskRequest(
        TaskId id,
        RequestorId requestorId,
        CapabilityDefinition capability,
        ResourceTier tier,
        TaskParameters parameters,
        DateTimeOffset createdAt)
        : this(id, requestorId, capability, LegacyResources(tier), parameters, createdAt)
    {
    }

    public TaskId Id { get; }

    public RequestorId RequestorId { get; }

    public CapabilityDefinition Capability { get; }

    public MachineSpecifications Resources { get; }

    public ResourceTier Tier => Resources.ComputeTier;

    public TaskParameters Parameters { get; }

    public DateTimeOffset CreatedAt { get; }

    public TaskStatus Status { get; private set; }

    public IReadOnlyList<TaskAttempt> Attempts => attempts;

    public int AssignmentCount => attempts.Count;

    public TaskResult? Result { get; private set; }

    public TaskAttempt Assign(AttemptId attemptId, ExecutionUnitId executionUnitId, string handle, DateTimeOffset assignedAt)
    {
        if (Status is not TaskStatus.Queued)
        {
            throw new DomainRuleViolation("task_not_queued", "Only a queued task may be assigned.");
        }

        if (attempts.Count >= MaximumAssignments)
        {
            throw new DomainRuleViolation("task_attempt_budget_exhausted", "A task cannot receive a fifth assignment.");
        }

        if (String.IsNullOrWhiteSpace(handle))
        {
            throw new DomainRuleViolation("task_handle_required", "An assignment handle is required.");
        }

        var attempt = new TaskAttempt(attemptId, executionUnitId, handle, assignedAt, AttemptState.Assigned);
        attempts.Add(attempt);
        Status = TaskStatus.Assigned;
        return attempt;
    }

    public void Accept(AttemptId attemptId, string handle, DateTimeOffset acceptedAt)
    {
        var attempt = GetOwnedAttempt(attemptId, handle, AttemptState.Assigned);
        ReplaceAttempt(attempt with { State = AttemptState.Accepted, AcceptedAt = acceptedAt });
        Status = TaskStatus.Running;
    }

    public void Requeue(
        AttemptId attemptId,
        string handle,
        AttemptState terminalState,
        string? failureStep = null,
        string? failureReason = null)
    {
        if (terminalState is not (AttemptState.Rejected or AttemptState.Revoked or AttemptState.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalState));
        }

        var attempt = GetOwnedAttempt(attemptId, handle, AttemptState.Assigned, AttemptState.Accepted, AttemptState.Disconnected);
        ReplaceAttempt(attempt with { State = terminalState, FailureStep = failureStep, FailureReason = failureReason });
        Status = attempts.Count >= MaximumAssignments ? TaskStatus.Failed : TaskStatus.Queued;
    }

    public void Disconnect(AttemptId attemptId, string handle, DateTimeOffset disconnectedAt)
    {
        var attempt = GetOwnedAttempt(attemptId, handle, AttemptState.Accepted);
        ReplaceAttempt(attempt with { State = AttemptState.Disconnected, DisconnectedAt = disconnectedAt });
        Status = TaskStatus.Running;
    }

    public void Rebind(AttemptId attemptId, string handle)
    {
        var attempt = GetOwnedAttempt(attemptId, handle, AttemptState.Disconnected);
        ReplaceAttempt(attempt with { State = AttemptState.Accepted });
        Status = TaskStatus.Running;
    }

    public void Complete(AttemptId attemptId, string handle, TaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var attempt = GetOwnedAttempt(attemptId, handle, AttemptState.Accepted);
        ReplaceAttempt(attempt with { State = AttemptState.Completed });
        Result = result;
        Status = TaskStatus.Completed;
    }

    private TaskAttempt GetOwnedAttempt(AttemptId attemptId, string handle, params AttemptState[] expectedStates)
    {
        var attempt = attempts.SingleOrDefault(attempt => attempt.Id == attemptId);
        if (attempt is null || !StringComparer.Ordinal.Equals(attempt.Handle, handle))
        {
            throw new DomainRuleViolation("task_handle_invalid", "The task handle is unknown, revoked, or owned by another attempt.");
        }

        if (!expectedStates.Contains(attempt.State))
        {
            throw new DomainRuleViolation("task_attempt_state_invalid", "The attempt is not in a state that accepts this operation.");
        }

        return attempt;
    }

    private void ReplaceAttempt(TaskAttempt replacement)
    {
        var index = attempts.FindIndex(attempt => attempt.Id == replacement.Id);
        attempts[index] = replacement;
    }

    public TaskRequestSnapshot ToSnapshot() => new(
        Id,
        RequestorId,
        Capability,
        Resources,
        Parameters,
        CreatedAt,
        Status,
        attempts.ToArray(),
        Result);

    public static TaskRequest Hydrate(TaskRequestSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var task = new TaskRequest(
            snapshot.Id,
            snapshot.RequestorId,
            snapshot.Capability,
            snapshot.Resources,
            snapshot.Parameters,
            snapshot.CreatedAt)
        {
            Status = snapshot.Status,
            Result = snapshot.Result,
        };
        task.attempts.AddRange(snapshot.Attempts);
        return task;
    }

    private static MachineSpecifications LegacyResources(ResourceTier tier) => tier switch
    {
        ResourceTier.Unspecified => new MachineSpecifications(ResourceTier.Unspecified, 0),
        ResourceTier.Automatic => new MachineSpecifications(ResourceTier.Small, MachineSpecificationsPolicy.MinimumMemoryGiB),
        _ => new MachineSpecifications(tier, MachineSpecificationsPolicy.MinimumMemoryGiB),
    };
}

public sealed record TaskRequestSnapshot(
    TaskId Id,
    RequestorId RequestorId,
    CapabilityDefinition Capability,
    MachineSpecifications Resources,
    TaskParameters Parameters,
    DateTimeOffset CreatedAt,
    TaskStatus Status,
    IReadOnlyList<TaskAttempt> Attempts,
    TaskResult? Result);
