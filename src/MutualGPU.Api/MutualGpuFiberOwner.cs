using NetCats.AspNetCore;
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

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Root.CloseAsync();
}
