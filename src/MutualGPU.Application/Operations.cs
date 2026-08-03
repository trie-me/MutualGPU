using System.Text.Json;
using System.Text.Json.Serialization;
using MutualGPU.Domain;

namespace MutualGPU.Application;

public interface IOperationUnitOfWork
{
    Task<T> ExecuteAsync<T>(
        Func<IOperationContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}

public interface IOperationContext
{
    ITaskRepository Tasks { get; }

    IExecutionUnitRepository ExecutionUnits { get; }

    ICapabilityRepository Capabilities { get; }

    IPartnerResourceRepository PartnerResources { get; }

    IResultUploadRepository ResultUploads { get; }

    IArtifactRepository Artifacts { get; }

    void Publish(OperationEvent message);
}

public interface ICapabilityRepository
{
    Task<CapabilityDefinition?> GetAsync(CapabilityId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<CapabilityDefinition>> GetAllAsync(CancellationToken cancellationToken);

    void Add(CapabilityDefinition capability);
}

public interface IPartnerResourceRepository
{
    Task<PartnerResourceRequest?> GetAsync(Guid id, CancellationToken cancellationToken);

    void Add(PartnerResourceRequest request);

    void Update(PartnerResourceRequest request);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArtifactDirection
{
    Input,
    Output,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArtifactState
{
    Staged,
    Available,
    Orphaned,
    Deleted,
}

/// <summary>
/// Stable storage-target identifiers used by persisted artifact locations.
/// The existing AWS deployment is intentionally named rather than inferred
/// from a provider implementation.
/// </summary>
public static class ArtifactStorageTargetIds
{
    public const string AwsPrimary = "aws-primary";

    /// <summary>
    /// A target must be named at every new-write boundary; it is never inferred
    /// from a repository default or substituted with another provider.
    /// </summary>
    public static string RequireExplicit(string? storageTargetId, string parameterName)
    {
        if (String.IsNullOrWhiteSpace(storageTargetId))
        {
            throw new ArgumentException("An explicit storage target is required.", parameterName);
        }

        return storageTargetId;
    }
}

/// <summary>
/// Immutable, explicitly configured selection for new artifact writes. It is
/// deliberately separate from a provider implementation so a database row or
/// a missing setting cannot silently route a write to a different store.
/// </summary>
public sealed class ArtifactStorageTargetSelection
{
    public ArtifactStorageTargetSelection(string writeStorageTargetId) =>
        WriteStorageTargetId = ArtifactStorageTargetIds.RequireExplicit(
            writeStorageTargetId,
            nameof(writeStorageTargetId));

    public string WriteStorageTargetId { get; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArtifactLocationState
{
    Staged,
    Available,
    Orphaned,
    Deleted,
    Failed,
}

/// <summary>
/// A provider-specific placement of a logical artifact. Provider ETags are
/// opaque values supplied by object storage and are never calculated here.
/// </summary>
public sealed record ArtifactLocation(
    string StorageTargetId,
    string ObjectKey,
    string? ProviderETag,
    ArtifactLocationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastVerifiedAt = null);

public sealed record ArtifactDescriptor(
    ArtifactId Id,
    TaskId TaskId,
    AttemptId? AttemptId,
    ArtifactDirection Direction,
    string Role,
    string S3ObjectKey,
    string ContentType,
    long Length,
    string Sha256,
    ArtifactState State,
    DateTimeOffset CreatedAt,
    ResultUploadOperationId? ResultUploadOperationId = null)
{
    public IReadOnlyList<ArtifactLocation>? Locations { get; init; }
}

public interface IArtifactRepository
{
    Task<IReadOnlyList<ArtifactDescriptor>> GetForTaskAsync(TaskId taskId, CancellationToken cancellationToken);

    void Add(ArtifactDescriptor artifact);

    void Update(ArtifactDescriptor artifact);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResultUploadState
{
    Authorized,
    Uploading,
    Uploaded,
    Completed,
    Expired,
    Failed,
}

public sealed record ResultUploadOperation(
    ResultUploadOperationId Id,
    TaskId TaskId,
    AttemptId AttemptId,
    ExecutionUnitId ExecutionUnitId,
    string HandleDigest,
    string? TokenDigest,
    string? Receipt,
    ResultUploadState State,
    DateTimeOffset? ExpiresAt,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UploadedAt = null,
    DateTimeOffset? CompletedAt = null)
{
    public required string WriteStorageTargetId { get; init; }
}

public interface IResultUploadRepository
{
    Task<ResultUploadOperation?> GetAsync(ResultUploadOperationId id, CancellationToken cancellationToken);

    Task<ResultUploadOperation?> GetByReceiptAsync(
        TaskId taskId,
        AttemptId attemptId,
        string receipt,
        CancellationToken cancellationToken);

    Task<ResultUploadOperation?> GetUploadingAsync(
        TaskId taskId,
        AttemptId attemptId,
        ExecutionUnitId executionUnitId,
        string handleDigest,
        CancellationToken cancellationToken);

    void Add(ResultUploadOperation operation);

    void Update(ResultUploadOperation operation);
}

public sealed record OperationEvent(
    OperationEventId Id,
    string Kind,
    JsonElement Payload,
    DateTimeOffset OccurredAt,
    DateTimeOffset AvailableAt)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static OperationEvent Scheduler(DateTimeOffset occurredAt) =>
        Create("scheduler.trigger", new { }, occurredAt);

    public static OperationEvent TaskChanged(RequestorId requestorId, DateTimeOffset occurredAt) =>
        Create("task.changed", new { requestorId = requestorId.Value }, occurredAt);

    public static OperationEvent PartnerOriginsChanged(DateTimeOffset occurredAt) =>
        Create("partner-origins.changed", new { }, occurredAt);

    public static OperationEvent AssignmentReady(TaskId taskId, AttemptId attemptId, ExecutionUnitId executionUnitId, DateTimeOffset occurredAt) =>
        Create("assignment.ready", new
        {
            taskId = taskId.Value,
            attemptId = attemptId.Value,
            executionUnitId = executionUnitId.Value,
        }, occurredAt);

    private static OperationEvent Create(string kind, object payload, DateTimeOffset occurredAt) =>
        new(
            OperationEventId.New(),
            kind,
            JsonSerializer.SerializeToElement(payload, JsonOptions),
            occurredAt,
            occurredAt);
}

public sealed class OptimisticConcurrencyException(
    string aggregateType,
    Guid aggregateId,
    long expectedVersion)
    : InvalidOperationException($"{aggregateType} '{aggregateId:D}' changed while version {expectedVersion} was being committed.")
{
    public string AggregateType { get; } = aggregateType;

    public Guid AggregateId { get; } = aggregateId;

    public long ExpectedVersion { get; } = expectedVersion;
}

public sealed class TaskIdempotencyConflictException(RequestorId requestorId, string idempotencyKey)
    : InvalidOperationException("The task idempotency key was committed by another operation.")
{
    public RequestorId RequestorId { get; } = requestorId;

    public string IdempotencyKey { get; } = idempotencyKey;
}

public sealed class CapabilityNameConflictException(string normalizedName)
    : InvalidOperationException("The capability name was committed by another enrollment.")
{
    public string NormalizedName { get; } = normalizedName;
}
