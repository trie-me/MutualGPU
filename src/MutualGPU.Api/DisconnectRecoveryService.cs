using System.Collections.Concurrent;
using MutualGPU.Application;
using MutualGPU.Domain;
using NetCats.AspNetCore;
using NetCats.Core;
using NetCats.Runtime;

namespace MutualGPU.Api;

/// <summary>
/// Owns the explicit 5/10/20/40/80-second reconnect grace sequence. Each sequence is
/// represented by a fiber so diagnostics show recovery without making the domain depend on NetCats.
/// </summary>
public sealed class DisconnectRecoveryService(
    ProviderSessionApplication sessions,
    IProviderAssignments assignments,
    TimeProvider timeProvider,
    MutualGpuFiberOwner fibers,
    ILogger<DisconnectRecoveryService> logger) : IHostedService
{
    private static readonly TimeSpan[] Delays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(80)];
    private readonly ConcurrentDictionary<ExecutionUnitId, FiberScope> scopes = [];

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Start(ExecutionUnitId executionUnitId)
    {
        var scope = fibers.Root.CreateChild(new FiberScopeOptions("disconnect-recovery"));
        if (!scopes.TryAdd(executionUnitId, scope))
        {
            _ = scope.CloseAsync();
            return;
        }

        var fiber = scope.Start(Latent<int>.DelayAsync(token => RecoverAsync(executionUnitId, token)), new FiberDescriptor("reconnect-grace"));
        _ = ObserveAsync(executionUnitId, scope, fiber);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var scope in scopes.Values)
        {
            await scope.CloseAsync().ConfigureAwait(false);
        }
        scopes.Clear();
    }

    private async Task<int> RecoverAsync(ExecutionUnitId executionUnitId, CancellationToken cancellationToken)
    {
        foreach (var delay in Delays)
        {
            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            if (!assignments.GetForExecutionUnit(executionUnitId).Any(IsDisconnected)) return 0;
        }

        return await sessions.RevokeDisconnected(executionUnitId).RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveAsync(ExecutionUnitId executionUnitId, FiberScope scope, Fiber<int> fiber)
    {
        try
        {
            var outcome = await fiber.JoinAsync().ConfigureAwait(false);
            if (outcome is Outcome<int>.Faulted faulted)
            {
                logger.LogError(faulted.Error, "MutualGPU reconnect recovery failed.");
            }
        }
        finally
        {
            scopes.TryRemove(executionUnitId, out _);
            await scope.CloseAsync().ConfigureAwait(false);
        }
    }

    private static bool IsDisconnected(ActiveProviderAssignment assignment) =>
        assignment.Task.Attempts.SingleOrDefault(attempt => attempt.Id == assignment.Attempt.Id)?.State is AttemptState.Disconnected;
}
