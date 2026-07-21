using System.Threading.Channels;
using MutualGPU.Application;
using NetCats.AspNetCore;
using NetCats.Core;
using NetCats.Runtime;

namespace MutualGPU.Api;

/// <summary>One process-local, non-reentrant scheduler loop. Event signals coalesce in a capacity-one channel.</summary>
public sealed class SchedulerHostedService(SchedulerApplication scheduler, ProviderSessionApplication sessions, SchedulerSignal signal, TimeProvider timeProvider, MutualGpuTelemetry telemetry, MutualGpuFiberOwner fibers) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var schedulerScope = fibers.Scheduler;
        var fiber = schedulerScope.Start(Latent<int>.DelayAsync(token => RunLoopAsync(token)), new FiberDescriptor("evaluation-loop"));
        using var shutdown = stoppingToken.Register(static state => _ = ((FiberScope)state!).CloseAsync(), schedulerScope);
        await fiber.JoinAsync().ConfigureAwait(false);
        await schedulerScope.CloseAsync().ConfigureAwait(false);
    }

    private async Task<int> RunLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var timerTick = Task.Delay(TimeSpan.FromSeconds(1), timeProvider, waitCancellation.Token);
            var signalTick = signal.WaitAsync(waitCancellation.Token).AsTask();
            var completed = await Task.WhenAny(timerTick, signalTick).ConfigureAwait(false);
            var wasSignalled = completed == signalTick;
            if (wasSignalled) await signalTick.ConfigureAwait(false);
            waitCancellation.Cancel();
            try { await (completed == timerTick ? signalTick : timerTick).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (wasSignalled)
            {
                await fibers.RunObservedAsync(
                    fibers.Scheduler,
                    "scheduler-evaluation",
                    "assign-queued-tasks",
                    EvaluateAsync,
                    stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await EvaluateAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        return 0;
    }

    private async Task<int> EvaluateAsync(CancellationToken cancellationToken)
    {
        await sessions.RevokeExpired(timeProvider.GetUtcNow().Subtract(TimeSpan.FromSeconds(30))).RunAsync(cancellationToken).ConfigureAwait(false);
        await sessions.RevokeDisconnected(timeProvider.GetUtcNow().Subtract(TimeSpan.FromSeconds(155))).RunAsync(cancellationToken).ConfigureAwait(false);
        using var activity = telemetry.Activities.StartActivity("mutualgpu.scheduler.evaluate");
        var assigned = await scheduler.Evaluate(timeProvider.GetUtcNow()).RunAsync(cancellationToken).ConfigureAwait(false);
        telemetry.AttemptsAssigned(assigned);
        return assigned;
    }
}

public sealed class SchedulerSignal : IApplicationEventSink
{
    private readonly Channel<byte> signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });

    public void TriggerScheduler() => signals.Writer.TryWrite(0);

    public ValueTask<byte> WaitAsync(CancellationToken cancellationToken) => signals.Reader.ReadAsync(cancellationToken);
}
