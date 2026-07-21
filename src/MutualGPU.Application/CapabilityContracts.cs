using System.Security.Cryptography;
using System.Text;
using MutualGPU.Domain;

namespace MutualGPU.Application;

public sealed record CapabilityContractConflict(
    CapabilityId CapabilityId,
    string CapabilityName,
    IReadOnlyList<string> Paths);

/// <summary>Canonicalizes data-continuity fields only; presentation text and ordering are deliberately excluded.</summary>
public static class CapabilityContracts
{
    public static string ComputeHash(IReadOnlyList<InputDefinition> inputs, OutputDefinition output)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(output);
        var builder = new StringBuilder();
        foreach (var input in inputs.OrderBy(static input => input.Key, StringComparer.Ordinal))
        {
            Append(builder, input.Key);
            Append(builder, input.Type.ToString());
            Append(builder, input.Required ? "required" : "optional");
            Append(builder, input.Default);
            Append(builder, input.Minimum?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(builder, input.Maximum?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendSequence(builder, input.AllowedValues);
            AppendSequence(builder, input.ContentTypes);
        }

        Append(builder, output.HasThumbnail ? "thumbnail" : "no-thumbnail");
        Append(builder, output.HasPreview ? "preview" : "no-preview");
        Append(builder, output.HasMetadata ? "metadata" : "no-metadata");
        Append(builder, output.HasLogs ? "logs" : "no-logs");
        AppendSequence(builder, output.PreviewContentTypes);
        Append(builder, output.MetadataSchema);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    public static IReadOnlyList<string> GetDelta(CapabilityDefinition existing, CapabilityDefinition submitted)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(submitted);
        var paths = new List<string>();
        var existingInputs = existing.Inputs.ToDictionary(static input => input.Key, StringComparer.Ordinal);
        var submittedInputs = submitted.Inputs.ToDictionary(static input => input.Key, StringComparer.Ordinal);
        foreach (var key in existingInputs.Keys.Union(submittedInputs.Keys, StringComparer.Ordinal).OrderBy(static key => key, StringComparer.Ordinal))
        {
            if (!existingInputs.TryGetValue(key, out var left) || !submittedInputs.TryGetValue(key, out var right))
            {
                paths.Add($"inputs.{key}");
                continue;
            }

            AddIfDifferent(paths, $"inputs.{key}.type", left.Type, right.Type);
            AddIfDifferent(paths, $"inputs.{key}.required", left.Required, right.Required);
            AddIfDifferent(paths, $"inputs.{key}.default", left.Default, right.Default);
            AddIfDifferent(paths, $"inputs.{key}.minimum", left.Minimum, right.Minimum);
            AddIfDifferent(paths, $"inputs.{key}.maximum", left.Maximum, right.Maximum);
            AddIfDifferent(paths, $"inputs.{key}.allowedValues", left.AllowedValues, right.AllowedValues);
            AddIfDifferent(paths, $"inputs.{key}.contentTypes", left.ContentTypes, right.ContentTypes);
        }

        AddIfDifferent(paths, "outputs.thumbnail", existing.Output.HasThumbnail, submitted.Output.HasThumbnail);
        AddIfDifferent(paths, "outputs.preview", existing.Output.HasPreview, submitted.Output.HasPreview);
        AddIfDifferent(paths, "outputs.metadata", existing.Output.HasMetadata, submitted.Output.HasMetadata);
        AddIfDifferent(paths, "outputs.logs", existing.Output.HasLogs, submitted.Output.HasLogs);
        AddIfDifferent(paths, "outputs.preview.contentTypes", existing.Output.PreviewContentTypes, submitted.Output.PreviewContentTypes);
        AddIfDifferent(paths, "outputs.metadata.schema", existing.Output.MetadataSchema, submitted.Output.MetadataSchema);
        return paths;
    }

    private static void Append(StringBuilder builder, string? value) => builder.Append(value?.Length ?? -1).Append(':').Append(value).Append('|');

    private static void AppendSequence(StringBuilder builder, IReadOnlyList<string>? values)
    {
        IEnumerable<string> ordered = values is null
            ? []
            : values.OrderBy(static value => value, StringComparer.Ordinal);
        foreach (var value in ordered)
        {
            Append(builder, value);
        }

        Append(builder, ";");
    }

    private static void AddIfDifferent<T>(List<string> paths, string path, T left, T right)
    {
        if (!EqualityComparer<T>.Default.Equals(left, right))
        {
            paths.Add(path);
        }
    }

    private static void AddIfDifferent(List<string> paths, string path, IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (!(left ?? []).OrderBy(static value => value, StringComparer.Ordinal).SequenceEqual(
            (right ?? []).OrderBy(static value => value, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            paths.Add(path);
        }
    }
}
