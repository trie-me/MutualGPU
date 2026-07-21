using System.Globalization;
using MutualGPU.Domain;
using NetCats.Core;

namespace MutualGPU.Application;

public sealed record SubmitTaskCommand(
    RequestorId RequestorId,
    CapabilityId CapabilityId,
    string ContractHash,
    IReadOnlyDictionary<string, string> Scalars,
    ArtifactId? Image,
    ResourceProfile Resources,
    DateTimeOffset SubmittedAt,
    string? IdempotencyKey = null,
    TaskId? TaskId = null,
    string? ImageContentType = null,
    string? ImageExtension = null,
    long? ImageLength = null,
    string? ImageSha256 = null);

public abstract record SubmitTaskResult
{
    public sealed record Created(TaskRequest Task, bool CreatedNow = true) : SubmitTaskResult;
    public sealed record Unavailable : SubmitTaskResult;
    public sealed record Conflict(string Code) : SubmitTaskResult;
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SubmitTaskResult;
}

public interface ICapabilityReader
{
    Task<CapabilityDefinition?> GetAsync(CapabilityId capabilityId, CancellationToken cancellationToken);
}

public sealed class TaskSubmissionApplication(
    ICapabilityReader capabilities,
    IProviderPresence presence,
    ITaskRepository tasks,
    IApplicationEventSink events)
{
    public Latent<SubmitTaskResult> Submit(SubmitTaskCommand command) => Latent<SubmitTaskResult>.DelayAsync(async cancellationToken =>
    {
        var capability = await capabilities.GetAsync(command.CapabilityId, cancellationToken).ConfigureAwait(false);
        if (capability is null || presence.GetConnectedCandidates(command.CapabilityId).Count is 0)
        {
            return new SubmitTaskResult.Unavailable();
        }

        if (!StringComparer.Ordinal.Equals(capability.ContractHash, command.ContractHash))
        {
            return new SubmitTaskResult.Conflict("capability_contract_changed");
        }

        if (!String.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            var existing = (await tasks.GetByRequestorAsync(command.RequestorId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(task => StringComparer.Ordinal.Equals(task.Parameters.IdempotencyKey, command.IdempotencyKey));
            if (existing is not null)
            {
                return SameSubmission(existing, command)
                    ? new SubmitTaskResult.Created(existing, CreatedNow: false)
                    : new SubmitTaskResult.Conflict("idempotency_key_reused");
            }
        }

        var errors = Validate(capability, command);
        if (errors.Count > 0)
        {
            return new SubmitTaskResult.Invalid(errors);
        }

        var task = new TaskRequest(command.TaskId ?? MutualGPU.Domain.TaskId.New(), command.RequestorId, capability, command.Resources,
            new TaskParameters(command.Scalars, command.Image, command.IdempotencyKey, command.ImageContentType, command.ImageExtension, command.ImageLength, command.ImageSha256), command.SubmittedAt);
        await tasks.SaveAsync(task, cancellationToken).ConfigureAwait(false);
        events.TriggerScheduler();
        return new SubmitTaskResult.Created(task);
    });

    private static IReadOnlyDictionary<string, string[]> Validate(CapabilityDefinition capability, SubmitTaskCommand command)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!command.Resources.IsValid)
        {
            errors["resources"] = ["Choose a valid resource profile."];
        }

        foreach (var input in capability.Inputs)
        {
            if (input.Type is CapabilityInputType.Image)
            {
                if (input.Required && command.Image is null) errors[input.Key] = ["An image is required."];
                continue;
            }

            if (!command.Scalars.TryGetValue(input.Key, out var value))
            {
                if (input.Required) errors[input.Key] = ["A value is required."];
                continue;
            }

            if (!IsValidValue(input, value)) errors[input.Key] = ["The value does not satisfy the capability contract."];
        }

        if (command.Image is not null && capability.Inputs.All(static input => input.Type is not CapabilityInputType.Image))
        {
            errors["image"] = ["An image is not declared by this capability."];
        }

        foreach (var key in command.Scalars.Keys.Where(key => capability.Inputs.All(input => input.Type is CapabilityInputType.Image || !StringComparer.Ordinal.Equals(input.Key, key))))
        {
            errors[key] = ["The field is not declared by this capability."];
        }

        return errors;
    }

    private static bool SameSubmission(TaskRequest existing, SubmitTaskCommand command) =>
        existing.Capability.Id == command.CapabilityId &&
        StringComparer.Ordinal.Equals(existing.Capability.ContractHash, command.ContractHash) &&
        existing.Resources == command.Resources &&
        SameImage(existing.Parameters, command) &&
        existing.Parameters.Scalars.Count == command.Scalars.Count &&
        existing.Parameters.Scalars.All(pair => command.Scalars.TryGetValue(pair.Key, out var value) && StringComparer.Ordinal.Equals(pair.Value, value));

    private static bool SameImage(TaskParameters existing, SubmitTaskCommand command)
    {
        if (existing.Image is null || command.Image is null)
        {
            return existing.Image is null && command.Image is null;
        }

        // Artifact IDs are generated for each multipart request. The content digest,
        // size, and declared MIME type are the stable idempotency identity instead.
        return existing.ImageLength == command.ImageLength &&
            StringComparer.OrdinalIgnoreCase.Equals(existing.ImageSha256, command.ImageSha256) &&
            StringComparer.OrdinalIgnoreCase.Equals(existing.ImageContentType, command.ImageContentType);
    }

    private static bool IsValidValue(InputDefinition input, string value)
    {
        if (input.AllowedValues is { Count: > 0 } && !input.AllowedValues.Contains(value, StringComparer.Ordinal)) return false;
        decimal? numeric = input.Type switch
        {
            CapabilityInputType.Integer when Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
            CapabilityInputType.Number when Decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => number,
            CapabilityInputType.Boolean => Boolean.TryParse(value, out _) ? 0 : null,
            CapabilityInputType.Date => DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? 0 : null,
            CapabilityInputType.DateTime => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) ? 0 : null,
            CapabilityInputType.DateTimeOffset => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) ? 0 : null,
            CapabilityInputType.String => 0,
            _ => null,
        };
        return numeric is not null && (!input.Minimum.HasValue || numeric >= input.Minimum) && (!input.Maximum.HasValue || numeric <= input.Maximum);
    }
}
