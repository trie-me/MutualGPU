using System.Collections.Concurrent;
using NetCats.Core;
using NetCats.Runtime;

namespace MutualGPU.Api;

/// <summary>
/// Opt-in demo scenery for the runtime forest. These scopes are deliberately
/// named as simulations and are never registered outside the local composition.
/// They make the visualisation useful before a presenter submits real work.
/// </summary>
public sealed class DemoForestSimulationHostedService(MutualGpuFiberOwner fibers, DemoSimulationRegistry registrations, TimeProvider timeProvider) : IHostedService
{
    private static readonly Scenario[] Scenarios =
    [
        new("portrait", "simulation · portrait splat", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), null),
        new("catalogue", "simulation · catalogue splat", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), "simulated result upload rejected"),
        new("high-detail", "simulation · high-detail splat", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(1), null),
        new("unsafe-input", "simulation · unsafe input", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), "simulated safety check failed"),
    ];

    private readonly ConcurrentDictionary<string, Task> active = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();

    public IReadOnlyList<DemoSimulationScenario> List() => Scenarios
        .Select(static scenario => new DemoSimulationScenario(scenario.Id, scenario.Name, scenario.FailureCondition ?? "synthetic result completes"))
        .ToArray();

    public bool Run(string id)
    {
        var scenario = Scenarios.SingleOrDefault(scenario => StringComparer.Ordinal.Equals(scenario.Id, id));
        if (scenario is null || active.ContainsKey(id)) return false;
        var run = RunScenarioAsync(scenario, stopping.Token);
        if (!active.TryAdd(id, run)) return false;
        _ = run.ContinueWith(_ => { active.TryRemove(id, out var ignored); }, TaskScheduler.Default);
        return true;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        await Task.WhenAll(active.Values).WaitAsync(cancellationToken).ConfigureAwait(false);
        stopping.Dispose();
    }

    private async Task RunScenarioAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        registrations.Set(scenario.Id, scenario.Name, "Registered", "awaiting simulated provider", timeProvider.GetUtcNow());
        var scope = fibers.Root.CreateChild(new FiberScopeOptions("simulated-task"));
        try
        {
            var fiber = scope.Start(Latent<int>.DelayAsync(async token =>
            {
                await RunStepAsync(scope, "simulated-input", "prepare input", scenario.Prepare, token).ConfigureAwait(false);
                registrations.Set(scenario.Id, scenario.Name, "Running", "simulated GPU inference", timeProvider.GetUtcNow());
                await RunStepAsync(scope, "simulated-inference", "simulate GPU inference", scenario.Inference, token).ConfigureAwait(false);
                if (scenario.FailureCondition is { } failure)
                {
                    await RunStepAsync(scope, "simulated-failure", failure, TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    registrations.Set(scenario.Id, scenario.Name, "Failed", failure, timeProvider.GetUtcNow());
                    throw new DemoSimulationFailureException(failure);
                }
                await RunStepAsync(scope, "simulated-packaging", "package Gaussian splat", scenario.Package, token).ConfigureAwait(false);
                await RunStepAsync(scope, "simulated-upload", "upload synthetic result", TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                registrations.Set(scenario.Id, scenario.Name, "Succeeded", "synthetic result completed", timeProvider.GetUtcNow());
                return 0;
            }), new FiberDescriptor(scenario.Name));
            await fiber.JoinAsync().ConfigureAwait(false);
        }
        finally
        {
            await scope.CloseAsync().ConfigureAwait(false);
        }
    }

    private Task RunStepAsync(FiberScope taskScope, string scopeName, string operation, TimeSpan duration, CancellationToken cancellationToken) =>
        fibers.RunObservedAsync(taskScope, scopeName, operation, async token =>
        {
            await Task.Delay(duration, token).ConfigureAwait(false);
            return 0;
        }, cancellationToken);

    private sealed record Scenario(string Id, string Name, TimeSpan Prepare, TimeSpan Inference, TimeSpan Package, string? FailureCondition);

    private sealed class DemoSimulationFailureException(string message) : Exception(message);
}

public sealed record DemoSimulationScenario(string Id, string Name, string ExpectedOutcome);
