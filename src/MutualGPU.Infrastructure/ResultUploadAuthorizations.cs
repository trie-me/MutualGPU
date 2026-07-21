using System.Security.Cryptography;
using MutualGPU.Application;
using MutualGPU.Domain;

namespace MutualGPU.Infrastructure;

public sealed class ResultUploadAuthorizations : IResultUploadAuthorizations, IStagedResults
{
    private readonly object gate = new();
    private readonly Dictionary<string, Authorization> authorizations = [];
    private readonly Dictionary<string, StagedResult> staged = [];

    public ResultUploadAuthorization Issue(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, DateTimeOffset now)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = now.AddMinutes(15);
        lock (gate) authorizations[token] = new Authorization(unitId, taskId, attemptId, handle, expires);
        return new ResultUploadAuthorization(token, expires);
    }

    public bool TryConsume(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string token, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!authorizations.Remove(token, out var authorization)) return false;
            return authorization.ExpiresAt >= now && authorization.UnitId == unitId && authorization.TaskId == taskId && authorization.AttemptId == attemptId && StringComparer.Ordinal.Equals(authorization.Handle, handle);
        }
    }

    public void Stage(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, StagedResult result)
    {
        lock (gate) staged[Key(unitId, taskId, attemptId, handle, result.Receipt)] = result;
    }

    public bool TryTake(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string receipt, out StagedResult result)
    {
        lock (gate) return staged.Remove(Key(unitId, taskId, attemptId, handle, receipt), out result!);
    }

    private static string Key(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string receipt) => $"{unitId.Value:N}/{taskId.Value:N}/{attemptId.Value:N}/{handle}/{receipt}";
    private sealed record Authorization(ExecutionUnitId UnitId, TaskId TaskId, AttemptId AttemptId, string Handle, DateTimeOffset ExpiresAt);
}
