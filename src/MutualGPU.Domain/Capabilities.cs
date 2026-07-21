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

public sealed record ResourceProfile(ResourceTier Compute, ResourceTier Memory)
{
    public static ResourceProfile Automatic { get; } = new(ResourceTier.Automatic, ResourceTier.Automatic);

    public bool IsValid => IsValidTier(Compute) && IsValidTier(Memory);

    public bool Satisfies(ResourceProfile minimum) =>
        Satisfies(Compute, minimum.Compute) && Satisfies(Memory, minimum.Memory);

    private static bool Satisfies(ResourceTier candidate, ResourceTier minimum) =>
        minimum is ResourceTier.Automatic or ResourceTier.Unspecified || candidate >= minimum;

    private static bool IsValidTier(ResourceTier tier) => tier is >= ResourceTier.Automatic and <= ResourceTier.ExtraLarge;
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

public sealed record MachineProfile(ResourceTier Compute, ResourceTier Memory)
{
    public ResourceProfile Resources => new(Compute, Memory);
}

public sealed record EnrollmentDefinition(MachineProfile Machine, IReadOnlyList<CapabilityDefinition> Capabilities)
{
    public void Validate()
    {
        if (!Machine.Resources.IsValid)
        {
            throw new DomainRuleViolation("machine_resource_profile_invalid", "The machine resource profile is invalid.");
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
