using MutualGPU.Application;

namespace MutualGPU.Api;

/// <summary>Keeps direct browser reads of presigned application objects aligned
/// with the configured and administrator-approved browser origins.</summary>
public sealed class BrowserObjectCorsSynchronizer(
    IReadOnlyCollection<string> configuredOrigins,
    IPartnerResourceRegistry registry,
    IBrowserObjectCorsPolicy policy)
{
    public async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        var approved = await registry.ListApprovedAsync(cancellationToken).ConfigureAwait(false);
        var origins = configuredOrigins
            .Concat(approved.Select(static request => request.Origin))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        await policy.SynchronizeAsync(origins, cancellationToken).ConfigureAwait(false);
    }
}
