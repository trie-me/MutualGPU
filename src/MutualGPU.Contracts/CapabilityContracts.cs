using MutualGPU.Domain;

namespace MutualGPU.Contracts;

public sealed record ResourceAvailabilityDto(
    ResourceTier Compute,
    ResourceTier Memory,
    int ConnectedCount,
    int IdleCount);

public sealed record CapabilityAvailabilityDto(
    Guid CapabilityId,
    string Name,
    string ContractHash,
    IReadOnlyList<ResourceAvailabilityDto> ResourceAvailability,
    IReadOnlyList<CapabilityInputDto> Inputs);

public sealed record CapabilityInputDto(string Key, string Type, bool Required, string Label, string? Description, string? Default, decimal? Minimum, decimal? Maximum, IReadOnlyList<string>? AllowedValues, IReadOnlyList<string>? ContentTypes);

/// <summary>
/// An additional capacity view. The existing T-shirt selector remains the canonical task submission control.
/// </summary>
public sealed record ResourceMatrixDto(
    IReadOnlyList<ResourceTier> MemoryAxis,
    IReadOnlyList<ResourceMatrixRowDto> ComputeAxis);

public sealed record ResourceMatrixRowDto(ResourceTier Compute, IReadOnlyList<ResourceMatrixCellDto> Cells);

public sealed record ResourceMatrixCellDto(ResourceTier Memory, int ConnectedCount, int IdleCount, bool IsSelectable);
