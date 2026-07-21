using MutualGPU.Application;

namespace MutualGPU.Api;

/// <summary>
/// One-time provider credential issuance for the hosted browser demo.
/// The raw credential is deliberately returned only in this response; the registry retains
/// only its digest and binding.
/// </summary>
public static class WebGpuEnrollmentEndpoints
{
    public static async Task<IResult> Create(
        ProviderKeyIssuer issuer,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var issued = (await issuer.IssueAsync(1, cancellationToken).ConfigureAwait(false)).Single();
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return TypedResults.Ok(new WebGpuEnrollmentResponse(
            issued.ExecutionUnitId.Value.ToString("D"),
            issued.PresharedKey));
    }

}

public sealed record WebGpuEnrollmentResponse(string ExecutionUnitId, string ProviderKey);
