using System.Security.Cryptography;
using System.Text;
using MutualGPU.Application;

namespace MutualGPU.Api;

/// <summary>
/// Password-gated, one-time provider credential issuance for the hosted browser demo.
/// The raw credential is deliberately returned only in this response; the registry retains
/// only its digest and binding.
/// </summary>
public static class WebGpuEnrollmentEndpoints
{
    public static async Task<IResult> Create(
        WebGpuEnrollmentRequest request,
        WebGpuEnrollmentOptions options,
        ProviderKeyIssuer issuer,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(options.Password))
        {
            return TypedResults.NotFound();
        }

        if (!Matches(options.Password, request.Password))
        {
            return TypedResults.Unauthorized();
        }

        var issued = (await issuer.IssueAsync(1, cancellationToken).ConfigureAwait(false)).Single();
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return TypedResults.Ok(new WebGpuEnrollmentResponse(
            issued.ExecutionUnitId.Value.ToString("D"),
            issued.PresharedKey));
    }

    private static bool Matches(string expected, string? actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual ?? String.Empty);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}

public sealed record WebGpuEnrollmentOptions(string? Password);
public sealed record WebGpuEnrollmentRequest(string? Password);
public sealed record WebGpuEnrollmentResponse(string ExecutionUnitId, string ProviderKey);
