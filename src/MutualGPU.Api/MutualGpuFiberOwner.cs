using NetCats.AspNetCore;
using NetCats.Core;
using NetCats.Runtime;

namespace MutualGPU.Api;

/// <summary>
/// Application-lifetime structured ownership for all MutualGPU hosted work. Transport
/// adapters create short-lived children; shutdown closes this one root and therefore
/// cancels and joins every remaining child scope and fiber.
/// </summary>
public sealed class MutualGpuFiberOwner : IHostedService
{
    public MutualGpuFiberOwner(IServiceProvider services)
    {
        Root = FiberScope.CreateRoot(new FiberScopeOptions("mutualgpu", services.GetService<FiberDiagnosticsRegistry>()));
        Scheduler = Root.CreateChild(new FiberScopeOptions("scheduler"));
        ProviderSessions = Root.CreateChild(new FiberScopeOptions("provider-sessions"));
        Startup = Root.CreateChild(new FiberScopeOptions("startup-projection"));
    }

    public FiberScope Root { get; }

    public FiberScope Scheduler { get; }

    public FiberScope ProviderSessions { get; }

    public FiberScope Startup { get; }

    /// <summary>
    /// Runs one meaningful unit of adapter work in its own short-lived scope. The
    /// scope is intentionally retained by the diagnostics projection after it
    /// completes, so the runtime forest shows work arriving and leaving without
    /// changing the ownership semantics of the application operation.
    /// </summary>
    public async Task<T> RunObservedAsync<T>(
        FiberScope parent,
        string scopeName,
        string fiberName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var scope = parent.CreateChild(new FiberScopeOptions(scopeName));
        try
        {
            var fiber = scope.Start(Latent<T>.DelayAsync(operation), new FiberDescriptor(fiberName));
            using var cancellation = cancellationToken.Register(static state => ((Fiber<T>)state!).RequestCancellation(), fiber);
            var outcome = await fiber.JoinAsync().ConfigureAwait(false);
            return outcome switch
            {
                Outcome<T>.Succeeded succeeded => succeeded.Value,
                Outcome<T>.Faulted faulted => throw faulted.Error,
                Outcome<T>.Cancelled => throw new OperationCanceledException(cancellationToken),
                _ => throw new InvalidOperationException("Unknown fiber outcome."),
            };
        }
        finally
        {
            await scope.CloseAsync().ConfigureAwait(false);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Root.CloseAsync();
}
