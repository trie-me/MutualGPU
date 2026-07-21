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
public sealed class StartupProjectionHostedService(IObjectStoreHealth storeHealth, IEnrollmentStartupRecovery enrollments, IStartupRecovery recovery, StartupProjectionState state, IApplicationEventSink events, MutualGpuFiberOwner fibers) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var fiber = fibers.Startup.Start(Latent<int>.DelayAsync(async token =>
        {
            await storeHealth.CheckHealthAsync(token).ConfigureAwait(false);
            await enrollments.RecoverAsync(token).ConfigureAwait(false);
            await recovery.RecoverAsync(token).ConfigureAwait(false);
            return 0;
        }), new FiberDescriptor("startup-projection"));
        var outcome = await fiber.JoinAsync().ConfigureAwait(false);
        if (outcome is Outcome<int>.Faulted faulted) throw faulted.Error;
        if (outcome is Outcome<int>.Cancelled) throw new OperationCanceledException(cancellationToken);
        state.MarkReady();
        events.TriggerScheduler();
    }

    public Task StopAsync(CancellationToken cancellationToken) => fibers.Startup.CloseAsync();
}
