using MutualGPU.Application;
using MutualGPU.Infrastructure;
using NetCats.Core;
using NetCats.Runtime;

namespace MutualGPU.Api;

public sealed class StartupProjectionState
{
    private int ready;
    private int dependenciesHealthy;

    public bool IsReady =>
        Volatile.Read(ref ready) == 1 &&
        Volatile.Read(ref dependenciesHealthy) == 1;

    public void MarkReady()
    {
        Volatile.Write(ref dependenciesHealthy, 1);
        Volatile.Write(ref ready, 1);
    }

    public void MarkDependenciesHealthy() => Volatile.Write(ref dependenciesHealthy, 1);

    public void MarkDependenciesUnavailable() => Volatile.Write(ref dependenciesHealthy, 0);
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

/// <summary>
/// Keeps readiness tied to the currently reachable durable dependencies after
/// startup. It deliberately reports only aggregate availability and never
/// surfaces a connection string, object key, or storage provider credential.
/// </summary>
public sealed class RuntimeDependencyHealthHostedService(
    IObjectStoreHealth storeHealth,
    StartupProjectionState state,
    TimeProvider timeProvider,
    ILogger<RuntimeDependencyHealthHostedService> logger,
    IPostgresHealth? postgresHealth = null) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await storeHealth.CheckHealthAsync(stoppingToken).ConfigureAwait(false);
                if (postgresHealth is not null)
                {
                    await postgresHealth.CheckHealthAsync(stoppingToken).ConfigureAwait(false);
                }
                state.MarkDependenciesHealthy();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                state.MarkDependenciesUnavailable();
                logger.LogError(error, "MutualGPU durable dependency health check failed; readiness is unavailable.");
            }

            await Task.Delay(Interval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
