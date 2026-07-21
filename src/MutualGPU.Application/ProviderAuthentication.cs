using MutualGPU.Domain;

namespace MutualGPU.Application;

public interface IExecutionUnitAuthenticator
{
    bool TryAuthenticate(string? presharedKey, out ExecutionUnitId executionUnitId);
}

public interface IExecutionUnitKeyResolver
{
    IReadOnlyCollection<ExecutionUnitId> ExecutionUnitIds { get; }

    bool TryGetPresharedKey(ExecutionUnitId executionUnitId, out string presharedKey);
}
