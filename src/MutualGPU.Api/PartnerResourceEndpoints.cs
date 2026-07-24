using System.Net.Mail;
using MutualGPU.Application;

namespace MutualGPU.Api;

public static class PartnerResourceEndpoints
{
    public static async Task<IResult> Submit(
        PartnerResourceSubmissionDto? input,
        IPartnerResourceRegistry registry,
        CancellationToken cancellationToken)
    {
        var errors = Validate(input, out var submission);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var request = await registry.SubmitAsync(submission!, cancellationToken).ConfigureAwait(false);
        return Results.Created($"/api/partner-resources/{request.Id:D}", PartnerResourceRequestDto.From(request));
    }

    internal static IReadOnlyDictionary<string, string[]> Validate(PartnerResourceSubmissionDto? input, out PartnerResourceSubmission? submission)
    {
        submission = null;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var partnerName = NormalizeText(input?.PartnerName, 2, 160);
        if (partnerName is null)
        {
            errors["partnerName"] = ["Enter a partner or organization name (2–160 characters)."];
        }

        var email = NormalizeEmail(input?.ContactEmail);
        if (email is null)
        {
            errors["contactEmail"] = ["Enter a valid contact email address."];
        }

        var origin = PartnerResourceOrigin.Normalize(input?.Origin);
        if (origin is null)
        {
            errors["origin"] = ["Enter one explicit HTTPS origin, such as https://partner.example. Wildcards, paths, and query strings are not accepted."];
        }

        if (errors.Count == 0)
        {
            submission = new PartnerResourceSubmission(partnerName!, email!, origin!);
        }
        return errors;
    }

    private static string? NormalizeText(string? value, int minimumLength, int maximumLength)
    {
        var normalized = value?.Trim();
        return String.IsNullOrWhiteSpace(normalized) ||
            normalized.Length < minimumLength || normalized.Length > maximumLength ||
            normalized.Any(char.IsControl)
            ? null
            : normalized;
    }

    private static string? NormalizeEmail(string? value)
    {
        var normalized = value?.Trim();
        if (String.IsNullOrWhiteSpace(normalized) || normalized.Length > 254 || normalized.Any(char.IsControl)) return null;
        try
        {
            var parsed = new MailAddress(normalized);
            return StringComparer.OrdinalIgnoreCase.Equals(parsed.Address, normalized) ? parsed.Address : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public static class PartnerResourceOrigin
{
    /// <summary>Normalizes a single browser origin. This deliberately excludes patterns,
    /// paths, and non-HTTPS schemes so approval is an exact CORS allow-list entry.</summary>
    public static string? Normalize(string? value)
    {
        var candidate = value?.Trim();
        if (String.IsNullOrWhiteSpace(candidate) || candidate.Length > 2048 || candidate.Contains('*')) return null;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !StringComparer.OrdinalIgnoreCase.Equals(uri.Scheme, Uri.UriSchemeHttps) ||
            uri.HostNameType is not UriHostNameType.Dns ||
            !uri.Host.Contains('.', StringComparison.Ordinal) ||
            !String.IsNullOrEmpty(uri.UserInfo) ||
            !String.IsNullOrEmpty(uri.Query) ||
            !String.IsNullOrEmpty(uri.Fragment) ||
            (uri.AbsolutePath is not "" and not "/"))
        {
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        return uri.IsDefaultPort ? $"https://{host}" : $"https://{host}:{uri.Port}";
    }
}

public sealed record PartnerResourceSubmissionDto(string? PartnerName, string? ContactEmail, string? Origin);

public sealed record PartnerResourceRequestDto(
    Guid Id,
    string PartnerName,
    string ContactEmail,
    string Origin,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset? RevokedAt)
{
    public static PartnerResourceRequestDto From(PartnerResourceRequest request) =>
        new(request.Id, request.PartnerName, request.ContactEmail, request.Origin, request.SubmittedAt, request.ProcessedAt, request.RevokedAt);
}
