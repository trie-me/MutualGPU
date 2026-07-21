using MutualGPU.Domain;

namespace MutualGPU.Contracts;

public sealed record MachineAvailabilityDto(
    ResourceTier ComputeTier,
    int MemoryGiB,
    int ConnectedCount,
    int IdleCount);

public sealed record CapabilityAvailabilityDto(
    Guid CapabilityId,
    string Name,
    string ContractHash,
    IReadOnlyList<MachineAvailabilityDto> MachineAvailability,
    IReadOnlyList<CapabilityInputDto> Inputs);

public sealed record CapabilityInputDto(string Key, string Type, bool Required, string Label, string? Description, string? Default, decimal? Minimum, decimal? Maximum, IReadOnlyList<string>? AllowedValues, IReadOnlyList<string>? ContentTypes);

public sealed record ResourceMatrixDto(
    IReadOnlyList<int> MemoryGiBAxis,
    IReadOnlyList<ResourceMatrixRowDto> ComputeAxis);

public sealed record ResourceMatrixRowDto(ResourceTier ComputeTier, IReadOnlyList<ResourceMatrixCellDto> Cells);

public sealed record ResourceMatrixCellDto(int MemoryGiB, int ConnectedCount, int IdleCount, bool IsAvailable);
