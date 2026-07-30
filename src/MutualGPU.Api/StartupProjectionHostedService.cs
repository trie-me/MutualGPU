using MutualGPU.Application;
using MutualGPU.Infrastructure;
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
    TimeProvider timeProvider,
    MutualGpuFiberOwner fibers,
    ILogger<StartupProjectionHostedService> logger,
    IPostgresHealth? postgresHealth = null) : IHostedService
{
    private Task? startupRecovery;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var processStartedAt = timeProvider.GetUtcNow();
        // Both PostgreSQL and the artifact store are serving prerequisites.
        // Expired-attempt reconciliation is deliberately background work; indexed
        // database state, not an object-store projection scan, drives recovery.
        startupRecovery = InitializeAsync(processStartedAt, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task InitializeAsync(
        DateTimeOffset processStartedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await storeHealth.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            if (postgresHealth is not null)
            {
                await postgresHealth.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            }
            await RecoverAsync(processStartedAt, cancellationToken).ConfigureAwait(false);
            state.MarkReady();
            events.TriggerScheduler();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(error, "MutualGPU storage readiness check failed; readiness remains unavailable.");
        }
    }

    private async Task RecoverAsync(DateTimeOffset processStartedAt, CancellationToken cancellationToken)
    {
        var fiber = fibers.Startup.Start(Latent<int>.DelayAsync(async token =>
        {
            await enrollments.RecoverAsync(token).ConfigureAwait(false);
            await recovery.RecoverAsync(processStartedAt, token).ConfigureAwait(false);
            return 0;
        }), new FiberDescriptor("startup-projection"));
        var outcome = await fiber.JoinAsync().ConfigureAwait(false);
        if (outcome is Outcome<int>.Faulted faulted)
        {
            throw new InvalidOperationException(
                "MutualGPU PostgreSQL startup recovery failed.",
                faulted.Error);
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
