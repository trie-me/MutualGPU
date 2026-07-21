using MutualGPU.Application;
using NetCats.Core;
using NetCats.Runtime;

namespace MutualGPU.Api;

public sealed class StartupProjectionState
{
    private int ready;
    public bool IsReady => Volatile.Read(ref ready) == 1;
    public void MarkReady() => Volatile.Write(ref ready, 1);
}

/// <summary>Rebuilds the durable task projection before readiness can succeed. Provider presence remains transient.</summary>
public sealed class StartupProjectionHostedService(
    IObjectStoreHealth storeHealth,
    IEnrollmentStartupRecovery enrollments,
    IStartupRecovery recovery,
    StartupProjectionState state,
    IApplicationEventSink events,
    MutualGpuFiberOwner fibers,
    ILogger<StartupProjectionHostedService> logger) : IHostedService
{
    private Task? startupRecovery;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A healthy object store is the only prerequisite for serving requests:
        // repositories read durable state directly. Reconciliation is deliberately
        // background work, because it can scan the provisioned-provider catalogue.
        // Do not hold readiness (or the ALB) behind that scan.
        _ = EstablishReadinessAsync(cancellationToken);
        startupRecovery = RecoverAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task EstablishReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await storeHealth.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            state.MarkReady();
            events.TriggerScheduler();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(error, "MutualGPU S3 health check failed; readiness remains unavailable.");
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var fiber = fibers.Startup.Start(Latent<int>.DelayAsync(async token =>
        {
            await enrollments.RecoverAsync(token).ConfigureAwait(false);
            await recovery.RecoverAsync(token).ConfigureAwait(false);
            return 0;
        }), new FiberDescriptor("startup-projection"));
        var outcome = await fiber.JoinAsync().ConfigureAwait(false);
        if (outcome is Outcome<int>.Faulted faulted)
        {
            logger.LogError(faulted.Error, "MutualGPU S3 startup recovery failed; readiness remains unavailable.");
            return;
        }
        if (outcome is Outcome<int>.Cancelled)
        {
            return;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await fibers.Startup.CloseAsync().ConfigureAwait(false);
        if (startupRecovery is not null)
        {
            await startupRecovery.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
