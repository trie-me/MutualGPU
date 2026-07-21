namespace MutualGPU.Api;

/// <summary>Small in-memory read model for the explicitly synthetic forest demo.</summary>
public sealed class DemoSimulationRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, DemoSimulationRegistration> registrations = new(StringComparer.Ordinal);

    public void Set(string id, string name, string state, string condition, DateTimeOffset observedAt)
    {
        lock (gate)
        {
            registrations[id] = new DemoSimulationRegistration(id, name, state, condition, observedAt);
        }
    }

    public IReadOnlyList<DemoSimulationRegistration> Snapshot()
    {
        lock (gate)
        {
            return registrations.Values.OrderBy(static registration => registration.Id).ToArray();
        }
    }
}

public sealed record DemoSimulationRegistration(string Id, string Name, string State, string Condition, DateTimeOffset ObservedAt);
