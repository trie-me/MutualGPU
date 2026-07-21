using System.Text.Json.Serialization;

namespace MutualGPU.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceTier
{
    Unspecified = 0,
    Automatic = 1,
    Small = 2,
    Medium = 3,
    Large = 4,
    ExtraLarge = 5,
}

public static class ResourceTierPolicy
{
    public static bool IsValidRequest(ResourceTier tier) =>
        tier is >= ResourceTier.Automatic and <= ResourceTier.ExtraLarge;

    public static bool IsConcrete(ResourceTier tier) =>
        tier is >= ResourceTier.Small and <= ResourceTier.ExtraLarge;

    public static bool Satisfies(ResourceTier candidate, ResourceTier requested) =>
        IsConcrete(candidate) && (requested is ResourceTier.Automatic || candidate >= requested);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapabilityInputType
{
    String,
    Integer,
    Number,
    Boolean,
    Date,
    DateTime,
    DateTimeOffset,
    Image,
}

public sealed record InputDefinition(
    string Key,
    CapabilityInputType Type,
    bool Required,
    string Label,
    string? Description = null,
    string? Default = null,
    decimal? Minimum = null,
    decimal? Maximum = null,
    IReadOnlyList<string>? AllowedValues = null,
    IReadOnlyList<string>? ContentTypes = null,
    int DisplayOrder = 0)
{
    public void Validate()
    {
        if (String.IsNullOrWhiteSpace(Key))
        {
            throw new DomainRuleViolation("capability_input_key_required", "A capability input key is required.");
        }

        if (Minimum is decimal minimum && Maximum is decimal maximum && minimum > maximum)
        {
            throw new DomainRuleViolation("capability_input_range_invalid", $"The range for '{Key}' is invalid.");
        }

        if (AllowedValues is { Count: > 0 } && Type is not CapabilityInputType.String)
        {
            throw new DomainRuleViolation("capability_input_allowed_values_invalid", $"Only string input '{Key}' may declare allowed values.");
        }

        if (ContentTypes is { Count: > 0 } && Type is not CapabilityInputType.Image)
        {
            throw new DomainRuleViolation("capability_input_content_types_invalid", $"Only image input '{Key}' may declare content types.");
        }
    }
}

public sealed record OutputDefinition(
    bool HasThumbnail = false,
    bool HasPreview = false,
    bool HasMetadata = false,
    bool HasLogs = false,
    IReadOnlyList<string>? PreviewContentTypes = null,
    string? MetadataSchema = null);

public sealed record CapabilityDefinition(
    CapabilityId Id,
    string Name,
    IReadOnlyList<InputDefinition> Inputs,
    OutputDefinition Output,
    string ContractHash,
    string? Description = null)
{
    public void Validate()
    {
        if (String.IsNullOrWhiteSpace(Name))
        {
            throw new DomainRuleViolation("capability_name_required", "A capability name is required.");
        }

        if (String.IsNullOrWhiteSpace(ContractHash))
        {
            throw new DomainRuleViolation("capability_contract_hash_required", "A capability contract hash is required.");
        }

        if (Inputs.GroupBy(static input => input.Key, StringComparer.Ordinal).Any(static group => group.Count() > 1))
        {
            throw new DomainRuleViolation("capability_input_keys_unique", "Capability input keys must be unique.");
        }

        foreach (var input in Inputs)
        {
            input.Validate();
        }

        if (Inputs.Count(static input => input.Type is CapabilityInputType.Image) > 1)
        {
            throw new DomainRuleViolation("capability_maximum_one_image", "A capability may define at most one image input.");
        }
    }
}

/// <summary>Coarse CPU/GPU and memory dimensions exposed to requestors.</summary>
public sealed record MachineSpecifications(ResourceTier ComputeTier, int MemoryGiB);

public static class MachineSpecificationsPolicy
{
    public const int MinimumMemoryGiB = 8;
    public const int MaximumMemoryGiB = 128;

    /// <summary>
    /// Coarse Apple-silicon MacBook WebGPU profiles. Memory is an inclusive range,
    /// rather than an exact model SKU: a profile may request any value from 8 GiB
    /// through the profile's published upper bound.
    /// </summary>
    public static int MaximumMemoryFor(ResourceTier computeTier) => computeTier switch
    {
        ResourceTier.Small => 16,       // M1-class: 8 CPU / 8 GPU cores
        ResourceTier.Medium => 24,      // M2/M3-class: 8 CPU / 10 GPU cores
        ResourceTier.Large => 48,       // Pro-class: 12 CPU / 18–20 GPU cores
        ResourceTier.ExtraLarge => MaximumMemoryGiB, // Max-class: 16 CPU / 40 GPU cores
        _ => 0,
    };

    public static bool IsValid(MachineSpecifications specifications) =>
        ResourceTierPolicy.IsConcrete(specifications.ComputeTier) &&
        specifications.MemoryGiB >= MinimumMemoryGiB &&
        specifications.MemoryGiB <= MaximumMemoryFor(specifications.ComputeTier);

    /// <summary>A node may satisfy a smaller CPU/GPU and memory request.</summary>
    public static bool Satisfies(MachineSpecifications candidate, MachineSpecifications requested) =>
        IsValid(candidate) && IsValid(requested) &&
        candidate.ComputeTier >= requested.ComputeTier && candidate.MemoryGiB >= requested.MemoryGiB;
}

/// <summary>
/// Tier is provider classification metadata. Specifications describe the capacity that
/// requestors select and that scheduling matches.
/// </summary>
public sealed record MachineProfile(ResourceTier Tier, MachineSpecifications Specifications);

public sealed record EnrollmentDefinition(MachineProfile Machine, IReadOnlyList<CapabilityDefinition> Capabilities)
{
    public void Validate()
    {
        if (!ResourceTierPolicy.IsConcrete(Machine.Tier))
        {
            throw new DomainRuleViolation("machine_tier_invalid", "A machine must advertise a concrete T-shirt tier.");
        }

        if (!MachineSpecificationsPolicy.IsValid(Machine.Specifications))
        {
            throw new DomainRuleViolation("machine_specifications_invalid", "Machine specifications require a concrete CPU/GPU tier and positive memory GiB.");
        }

        if (Capabilities.GroupBy(static capability => capability.Name, StringComparer.Ordinal).Any(static group => group.Count() > 1))
        {
            throw new DomainRuleViolation("capability_names_unique", "Capability names must be unique within an enrollment.");
        }

        foreach (var capability in Capabilities)
        {
            capability.Validate();
        }
    }
}
