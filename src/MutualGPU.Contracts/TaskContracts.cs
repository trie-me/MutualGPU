using MutualGPU.Domain;

namespace MutualGPU.Contracts;

public sealed record SubmitTaskRequestDto(
    Guid CapabilityId,
    string ContractHash,
    IReadOnlyDictionary<string, string> Scalars,
    MachineSpecifications Resources,
    string? IdempotencyKey = null);

public sealed record TaskDto(
    Guid TaskId,
    string CapabilityName,
    DateTimeOffset CreatedAt,
    MachineSpecifications Resources,
    MutualGPU.Domain.TaskStatus Status,
    int AttemptCount,
    string? FailureStep,
    bool CanReevaluate,
    bool CanRetrieveResult,
    TaskProgressDto? Progress = null);

public sealed record TaskProgressDto(ulong SequenceNumber, DateTimeOffset ObservedAt, string? Phase, double? Percent, string? Message);

public sealed record ResultArtifactDto(string Name, string ContentType, long Length, string Sha256, DateTimeOffset ExpiresAt, Uri DownloadUrl);

public sealed record TaskResultDto(Guid TaskId, IReadOnlyList<ResultArtifactDto> Artifacts);
