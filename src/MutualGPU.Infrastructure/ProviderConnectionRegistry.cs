using MutualGPU.Application;
using MutualGPU.Domain;
using System.Threading.Channels;

namespace MutualGPU.Infrastructure;

/// <summary>Process-local connection/presence projection; durable enrollment remains in the execution-unit repository.</summary>
public sealed class ProviderConnectionRegistry : IProviderPresence, IProviderAssignments, IProviderProgress
{
    private readonly object gate = new();
    private readonly Dictionary<ExecutionUnitId, Connection> connections = [];

    public ProviderSessionLease Connect(ExecutionUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        lock (gate)
        {
            var channel = Channel.CreateBounded<ProviderAssignment>(new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });
            var sessionId = Guid.CreateVersion7();
            if (connections.TryGetValue(unit.Id, out var previous)) previous.Outbound.Writer.TryComplete();
            connections[unit.Id] = new Connection(sessionId, unit.CurrentEnrollment.Machine, unit.CurrentEnrollment.Capabilities.Select(static capability => capability.Id).ToHashSet(), true, channel);
            return new ProviderSessionLease(unit.Id, sessionId, channel.Reader);
        }
    }

    public void Disconnect(ExecutionUnitId executionUnitId)
    {
        lock (gate)
        {
            if (connections.Remove(executionUnitId, out var connection)) connection.Outbound.Writer.TryComplete();
        }
    }

    /// <summary>Removes a session only when it is still the unit's current lease.
    /// A late cleanup from a replaced stream must never evict its replacement.</summary>
    public bool Disconnect(ProviderSessionLease lease)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(lease.ExecutionUnitId, out var connection) || connection.SessionId != lease.SessionId) return false;
            connections.Remove(lease.ExecutionUnitId);
            connection.Outbound.Writer.TryComplete();
            return true;
        }
    }

    public bool IsCurrent(ProviderSessionLease lease)
    {
        lock (gate) return connections.TryGetValue(lease.ExecutionUnitId, out var connection) && connection.SessionId == lease.SessionId;
    }

    public void SetBusy(ExecutionUnitId executionUnitId, bool isBusy)
    {
        lock (gate)
        {
            if (connections.TryGetValue(executionUnitId, out var connection)) connections[executionUnitId] = connection with { IsIdle = !isBusy };
        }
    }

    public IReadOnlyList<ProviderCandidate> GetConnectedCandidates(CapabilityId capabilityId)
    {
        lock (gate)
        {
            return connections.Where(pair => pair.Value.Capabilities.Contains(capabilityId))
                .Select(pair => new ProviderCandidate(pair.Key, capabilityId, pair.Value.Machine.Tier, pair.Value.Machine.Specifications, pair.Value.IsIdle)).ToArray();
        }
    }

    public bool TryDeliver(ExecutionUnitId executionUnitId, ProviderAssignment assignment)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(executionUnitId, out var connection) || !connection.IsIdle) return false;
            if (!connection.Outbound.Writer.TryWrite(assignment)) return false;
            connections[executionUnitId] = connection with { IsIdle = false };
            return true;
        }
    }

    public void Track(ExecutionUnitId executionUnitId, TaskRequest task, TaskAttempt attempt)
    {
        lock (gate) active[(executionUnitId, task.Id, attempt.Id)] = new ActiveProviderAssignment(executionUnitId, task, attempt);
    }

    public bool TryGet(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId, string handle, out TaskRequest task)
    {
        lock (gate)
        {
            if (active.TryGetValue((executionUnitId, taskId, attemptId), out var assignment) && StringComparer.Ordinal.Equals(assignment.Attempt.Handle, handle))
            {
                task = assignment.Task;
                return true;
            }
        }
        task = null!;
        return false;
    }

    public void Remove(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId)
    {
        lock (gate)
        {
            active.Remove((executionUnitId, taskId, attemptId));
            if (connections.TryGetValue(executionUnitId, out var connection)) connections[executionUnitId] = connection with { IsIdle = true };
        }
    }

    public IReadOnlyList<ActiveProviderAssignment> GetUnacceptedBefore(DateTimeOffset deadline)
    {
        lock (gate) return active.Values.Where(item => item.Task.Attempts.SingleOrDefault(attempt => attempt.Id == item.Attempt.Id) is { State: AttemptState.Assigned } attempt && attempt.AssignedAt <= deadline).ToArray();
    }

    public IReadOnlyList<ActiveProviderAssignment> GetForExecutionUnit(ExecutionUnitId executionUnitId)
    {
        lock (gate) return active.Values.Where(item => item.ExecutionUnitId == executionUnitId).ToArray();
    }

    public IReadOnlyList<ActiveProviderAssignment> GetDisconnectedBefore(DateTimeOffset deadline)
    {
        lock (gate) return active.Values.Where(item => item.Task.Attempts.SingleOrDefault(attempt => attempt.Id == item.Attempt.Id) is { State: AttemptState.Disconnected, DisconnectedAt: { } disconnectedAt } && disconnectedAt <= deadline).ToArray();
    }

    public bool TryGetByHandle(ExecutionUnitId executionUnitId, string handle, out ActiveProviderAssignment assignment)
    {
        lock (gate)
        {
            var found = active.Values.FirstOrDefault(item => item.ExecutionUnitId == executionUnitId && StringComparer.Ordinal.Equals(item.Attempt.Handle, handle));
            if (found is not null) { assignment = found; return true; }
        }
        assignment = null!;
        return false;
    }

    public bool TryReport(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, TaskProgress progress)
    {
        lock (gate)
        {
            if (!active.TryGetValue((unitId, taskId, attemptId), out var assignment) || !StringComparer.Ordinal.Equals(assignment.Attempt.Handle, handle) ||
                assignment.Task.Attempts.SingleOrDefault(attempt => attempt.Id == attemptId)?.State is not AttemptState.Accepted) return false;
            if (progresses.TryGetValue(taskId, out var previous) && (progress.SequenceNumber <= previous.SequenceNumber || progress.ObservedAt - previous.ObservedAt < TimeSpan.FromSeconds(1))) return false;
            progresses[taskId] = progress;
            return true;
        }
    }

    public TaskProgress? Get(TaskId taskId)
    {
        lock (gate) return progresses.GetValueOrDefault(taskId);
    }

    public void Remove(TaskId taskId)
    {
        lock (gate) progresses.Remove(taskId);
    }

    private readonly Dictionary<(ExecutionUnitId UnitId, TaskId TaskId, AttemptId AttemptId), ActiveProviderAssignment> active = [];
    private readonly Dictionary<TaskId, TaskProgress> progresses = [];

    private sealed record Connection(Guid SessionId, MachineProfile Machine, IReadOnlySet<CapabilityId> Capabilities, bool IsIdle, Channel<ProviderAssignment> Outbound);
}

public sealed record ProviderSessionLease(ExecutionUnitId ExecutionUnitId, Guid SessionId, ChannelReader<ProviderAssignment> Assignments);
