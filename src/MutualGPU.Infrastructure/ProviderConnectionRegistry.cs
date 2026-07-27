using MutualGPU.Application;
using MutualGPU.Domain;
using System.Threading.Channels;

namespace MutualGPU.Infrastructure;

/// <summary>Process-local connection/presence projection; durable enrollment remains in the execution-unit repository.</summary>
public sealed class ProviderConnectionRegistry : IProviderPresence, IProviderAssignments, IProviderProgress
{
    private const int MaximumRetainedSessions = 250;
    private const int MaximumEventsPerSession = 500;
    private readonly object gate = new();
    private readonly Dictionary<ExecutionUnitId, Connection> connections = [];
    private readonly Dictionary<Guid, MutableSession> sessions = [];
    private readonly Dictionary<AttemptId, Guid> assignmentSessions = [];
    private readonly NetworkIdentityProtector networkIdentities;
    private readonly TimeProvider timeProvider;

    public ProviderConnectionRegistry(TimeProvider? timeProvider = null)
        : this(new NetworkIdentityProtector(null), timeProvider ?? TimeProvider.System)
    {
    }

    public ProviderConnectionRegistry(NetworkIdentityProtector networkIdentities, TimeProvider timeProvider)
    {
        this.networkIdentities = networkIdentities;
        this.timeProvider = timeProvider;
    }

    public ProviderSessionLease Connect(ExecutionUnit unit, string transport = "unknown", bool isIdle = true, string? sourceIp = null, string? providerName = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        lock (gate)
        {
            var channel = Channel.CreateBounded<ProviderServerMessage>(new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });
            var sessionId = Guid.CreateVersion7();
            if (connections.TryGetValue(unit.Id, out var previous))
            {
                previous.Outbound.Writer.TryComplete();
                CloseSession(previous.SessionId, "replaced");
            }
            connections[unit.Id] = new Connection(sessionId, unit.CurrentEnrollment.Machine, unit.CurrentEnrollment.Capabilities.ToArray(), isIdle, channel);
            var now = timeProvider.GetUtcNow();
            var protectedNetwork = networkIdentities.Protect(sourceIp);
            var session = new MutableSession(
                sessionId,
                unit.Id,
                String.IsNullOrWhiteSpace(transport) ? "unknown" : transport,
                String.IsNullOrWhiteSpace(sourceIp) ? null : sourceIp.Trim(),
                protectedNetwork.IpHash,
                protectedNetwork.IpClassAB,
                String.IsNullOrWhiteSpace(providerName) ? null : providerName.Trim(),
                now);
            sessions[sessionId] = session;
            AddEvent(session, "connected", $"Provider connected over {session.Transport}.", now);
            TrimSessions();
            return new ProviderSessionLease(unit.Id, sessionId, channel.Reader);
        }
    }

    public void Disconnect(ExecutionUnitId executionUnitId)
    {
        lock (gate)
        {
            if (connections.Remove(executionUnitId, out var connection))
            {
                CaptureActiveStates(executionUnitId);
                connection.Outbound.Writer.TryComplete();
                CloseSession(connection.SessionId, "transport_closed");
            }
        }
    }

    /// <summary>Removes a session only when it is still the unit's current lease.
    /// A late cleanup from a replaced stream must never evict its replacement.</summary>
    public bool Disconnect(ProviderSessionLease lease)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(lease.ExecutionUnitId, out var connection) || connection.SessionId != lease.SessionId) return false;
            CaptureActiveStates(lease.ExecutionUnitId);
            connections.Remove(lease.ExecutionUnitId);
            connection.Outbound.Writer.TryComplete();
            CloseSession(connection.SessionId, "transport_closed");
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

    public IReadOnlyList<ConnectedProviderCapability> GetConnectedCapabilities()
    {
        lock (gate)
        {
            return connections.SelectMany(pair => pair.Value.Capabilities.Select(capability =>
                new ConnectedProviderCapability(
                    pair.Key,
                    capability,
                    pair.Value.Machine.Tier,
                    pair.Value.Machine.Specifications,
                    pair.Value.IsIdle))).ToArray();
        }
    }

    public IReadOnlyList<ProviderCandidate> GetConnectedCandidates(CapabilityId capabilityId)
    {
        lock (gate)
        {
            return connections.Where(pair => pair.Value.Capabilities.Any(capability => capability.Id == capabilityId))
                .Select(pair =>
                {
                    var session = sessions[pair.Value.SessionId];
                    return new ProviderCandidate(
                        pair.Key,
                        capabilityId,
                        pair.Value.Machine.Tier,
                        pair.Value.Machine.Specifications,
                        pair.Value.IsIdle,
                        session.SessionId,
                        session.IpHash,
                        session.IpClassAB,
                        session.ProviderName,
                        session.Transport);
                }).ToArray();
        }
    }

    public bool TryDeliver(ExecutionUnitId executionUnitId, ProviderAssignment assignment)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(executionUnitId, out var connection) || !connection.IsIdle) return false;
            if (!connection.Outbound.Writer.TryWrite(new ProviderAssignmentMessage(assignment))) return false;
            connections[executionUnitId] = connection with { IsIdle = false };
            if (sessions.TryGetValue(connection.SessionId, out var session))
                AddEvent(session, "assignment_delivered", "Assignment delivered to provider.", timeProvider.GetUtcNow(), assignment.TaskId, assignment.AttemptId);
            return true;
        }
    }

    public bool TryCancel(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId, string handle)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(executionUnitId, out var connection)) return false;
            if (!connection.Outbound.Writer.TryWrite(new ProviderCancellation(taskId, attemptId, handle))) return false;
            if (sessions.TryGetValue(connection.SessionId, out var session))
                AddEvent(session, "cancellation_requested", "Requestor cancelled the assignment.", timeProvider.GetUtcNow(), taskId, attemptId);
            return true;
        }
    }

    public void Track(ExecutionUnitId executionUnitId, TaskRequest task, TaskAttempt attempt)
    {
        lock (gate)
        {
            active[(executionUnitId, task.Id, attempt.Id)] = new ActiveProviderAssignment(executionUnitId, task, attempt);
            if (connections.TryGetValue(executionUnitId, out var connection) && sessions.TryGetValue(connection.SessionId, out var session))
            {
                assignmentSessions[attempt.Id] = session.SessionId;
                session.RecordedStates.Add((attempt.Id, AttemptState.Assigned));
                AddEvent(session, "assignment_created", "Task assigned to provider.", attempt.AssignedAt, task.Id, attempt.Id);
            }
        }
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
            CaptureAttemptState(executionUnitId, taskId, attemptId);
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

    public ProviderProgressDisposition Report(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, TaskProgress progress)
    {
        lock (gate)
        {
            if (!IsWellFormed(progress)) return ProviderProgressDisposition.RejectedMalformed;
            if (!active.TryGetValue((unitId, taskId, attemptId), out var assignment))
            {
                if (active.Keys.Any(key => key.TaskId == taskId && key.AttemptId == attemptId)) return ProviderProgressDisposition.RejectedWrongExecutionUnit;
                return ProviderProgressDisposition.RejectedUnknownAssignment;
            }
            if (!StringComparer.Ordinal.Equals(assignment.Attempt.Handle, handle)) return ProviderProgressDisposition.RejectedWrongHandle;
            if (assignment.Task.Attempts.SingleOrDefault(attempt => attempt.Id == attemptId)?.State is not AttemptState.Accepted)
                return ProviderProgressDisposition.RejectedAttemptState;
            var key = (taskId, attemptId);
            if (progresses.TryGetValue(key, out var previous) && progress.SequenceNumber <= previous.SequenceNumber)
                return ProviderProgressDisposition.DroppedStaleSequence;
            progresses[key] = progress;
            CaptureAttemptState(unitId, taskId, attemptId);
            if (assignmentSessions.TryGetValue(attemptId, out var sessionId) && sessions.TryGetValue(sessionId, out var session))
            {
                var percent = progress.Percent is { } value ? $" {value:0.#}%" : String.Empty;
                AddEvent(session, "progress", $"{progress.Phase ?? "Provider work"}{percent}", progress.ObservedAt, taskId, attemptId);
            }
            return ProviderProgressDisposition.Accepted;
        }
    }

    public void RecordIgnoredResultParts(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, IReadOnlyList<string> parts)
    {
        if (parts.Count == 0) return;
        lock (gate)
        {
            if (connections.TryGetValue(unitId, out var connection) && sessions.TryGetValue(connection.SessionId, out var session))
                AddEvent(session, "result_parts_ignored", $"Ignored optional result parts: {String.Join(", ", parts)}.", timeProvider.GetUtcNow(), taskId, attemptId);
        }
    }

    public TaskProgress? Get(TaskId taskId)
    {
        lock (gate)
        {
            var activeAttempt = active.Values
                .Where(item => item.Task.Id == taskId)
                .Select(item => item.Attempt.Id)
                .FirstOrDefault();
            return activeAttempt == default ? null : progresses.GetValueOrDefault((taskId, activeAttempt));
        }
    }

    public void Remove(TaskId taskId, AttemptId attemptId)
    {
        lock (gate) progresses.Remove((taskId, attemptId));
    }

    public AdminDiagnosticsSnapshot Snapshot()
    {
        lock (gate)
        {
            foreach (var assignment in active.Keys.ToArray()) CaptureAttemptState(assignment.UnitId, assignment.TaskId, assignment.AttemptId);
            var current = connections.Values.Select(static connection => connection.SessionId).ToHashSet();
            return new AdminDiagnosticsSnapshot(
                sessions.Values
                    .OrderByDescending(static session => session.ConnectedAt)
                    .Select(session => ToSnapshot(session, current.Contains(session.SessionId)))
                    .ToArray(),
                assignmentSessions.ToDictionary(static pair => pair.Key.Value, static pair => pair.Value));
        }
    }

    private void CaptureActiveStates(ExecutionUnitId executionUnitId)
    {
        foreach (var key in active.Keys.Where(key => key.UnitId == executionUnitId).ToArray()) CaptureAttemptState(key.UnitId, key.TaskId, key.AttemptId);
    }

    private void CaptureAttemptState(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId)
    {
        if (!active.TryGetValue((executionUnitId, taskId, attemptId), out var assignment) ||
            !assignmentSessions.TryGetValue(attemptId, out var sessionId) ||
            !sessions.TryGetValue(sessionId, out var session)) return;
        var attempt = assignment.Task.Attempts.SingleOrDefault(item => item.Id == attemptId);
        if (attempt is null || !session.RecordedStates.Add((attemptId, attempt.State))) return;
        var occurredAt = attempt.State switch
        {
            AttemptState.Assigned => attempt.AssignedAt,
            AttemptState.Accepted => attempt.AcceptedAt ?? timeProvider.GetUtcNow(),
            AttemptState.Disconnected => attempt.DisconnectedAt ?? timeProvider.GetUtcNow(),
            _ => timeProvider.GetUtcNow(),
        };
        var detail = attempt.State switch
        {
            AttemptState.Rejected => SafeDetail(attempt.FailureReason, "Provider rejected the assignment."),
            AttemptState.Failed => SafeDetail(attempt.FailureReason, "Provider reported an assignment failure."),
            AttemptState.Revoked => SafeDetail(attempt.FailureReason, "Assignment was revoked."),
            AttemptState.Completed => "Assignment completed.",
            AttemptState.Disconnected => "Provider disconnected while the assignment was active.",
            AttemptState.Accepted => "Provider accepted the assignment.",
            _ => "Task assigned to provider.",
        };
        AddEvent(session, attempt.State.ToString().ToLowerInvariant(), detail, occurredAt, taskId, attemptId);
    }

    private void CloseSession(Guid sessionId, string reason)
    {
        if (!sessions.TryGetValue(sessionId, out var session) || session.ClosedAt is not null) return;
        session.ClosedAt = timeProvider.GetUtcNow();
        session.CloseReason = reason;
        AddEvent(session, "disconnected", reason == "replaced" ? "Session replaced by a newer connection." : "Provider transport disconnected.", session.ClosedAt.Value);
    }

    private static string SafeDetail(string? value, string fallback)
    {
        if (String.IsNullOrWhiteSpace(value)) return fallback;
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static void AddEvent(MutableSession session, string type, string summary, DateTimeOffset occurredAt, TaskId? taskId = null, AttemptId? attemptId = null)
    {
        session.Events.Add(new AdminSessionEvent(Guid.CreateVersion7(), occurredAt, type, summary, taskId?.Value, attemptId?.Value));
        if (session.Events.Count > MaximumEventsPerSession) session.Events.RemoveRange(0, session.Events.Count - MaximumEventsPerSession);
    }

    private void TrimSessions()
    {
        if (sessions.Count <= MaximumRetainedSessions) return;
        foreach (var sessionId in sessions.Values.Where(static session => session.ClosedAt is not null).OrderBy(static session => session.ConnectedAt).Select(static session => session.SessionId).Take(sessions.Count - MaximumRetainedSessions).ToArray())
        {
            sessions.Remove(sessionId);
            foreach (var attemptId in assignmentSessions.Where(pair => pair.Value == sessionId).Select(static pair => pair.Key).ToArray()) assignmentSessions.Remove(attemptId);
        }
    }

    private static AdminSessionSnapshot ToSnapshot(MutableSession session, bool isCurrent)
    {
        var events = session.Events.OrderBy(static item => item.OccurredAt).ToArray();
        return new AdminSessionSnapshot(
            session.SessionId,
            session.ExecutionUnitId.Value,
            session.ProviderName,
            session.Transport,
            session.SourceIp,
            session.IpHash,
            session.IpClassAB,
            session.ConnectedAt,
            session.ClosedAt,
            isCurrent ? "connected" : "disconnected",
            session.CloseReason,
            events.Select(static item => item.AttemptId).Where(static id => id is not null).Distinct().Count(),
            new AdminSessionSummary(
                events.Length,
                events.Count(static item => item.Type == "assignment_created"),
                events.Count(static item => item.Type == "accepted"),
                events.Count(static item => item.Type == "rejected"),
                events.Count(static item => item.Type == "failed"),
                events.Count(static item => item.Type == "completed"),
                events.Count(static item => item.Type == "progress")),
            events);
    }

    private readonly Dictionary<(ExecutionUnitId UnitId, TaskId TaskId, AttemptId AttemptId), ActiveProviderAssignment> active = [];
    private static bool IsWellFormed(TaskProgress progress) =>
        progress.Phase is not { Length: > 128 } &&
        progress.Message is not { Length: > 2_048 } &&
        (progress.Percent is null || Double.IsFinite(progress.Percent.Value));

    private readonly Dictionary<(TaskId TaskId, AttemptId AttemptId), TaskProgress> progresses = [];

    private sealed record Connection(Guid SessionId, MachineProfile Machine, IReadOnlyList<CapabilityDefinition> Capabilities, bool IsIdle, Channel<ProviderServerMessage> Outbound);

    private sealed class MutableSession(
        Guid sessionId,
        ExecutionUnitId executionUnitId,
        string transport,
        string? sourceIp,
        string ipHash,
        string ipClassAB,
        string? providerName,
        DateTimeOffset connectedAt)
    {
        public Guid SessionId { get; } = sessionId;
        public ExecutionUnitId ExecutionUnitId { get; } = executionUnitId;
        public string? ProviderName { get; } = providerName;
        public string Transport { get; } = transport;
        public string? SourceIp { get; } = sourceIp;
        public string IpHash { get; } = ipHash;
        public string IpClassAB { get; } = ipClassAB;
        public DateTimeOffset ConnectedAt { get; } = connectedAt;
        public DateTimeOffset? ClosedAt { get; set; }
        public string? CloseReason { get; set; }
        public List<AdminSessionEvent> Events { get; } = [];
        public HashSet<(AttemptId AttemptId, AttemptState State)> RecordedStates { get; } = [];
    }
}

public sealed record ProviderSessionLease(ExecutionUnitId ExecutionUnitId, Guid SessionId, ChannelReader<ProviderServerMessage> Assignments);

public sealed record AdminDiagnosticsSnapshot(
    IReadOnlyList<AdminSessionSnapshot> Sessions,
    IReadOnlyDictionary<Guid, Guid> AssignmentSessions);

public sealed record AdminSessionSnapshot(
    Guid SessionId,
    Guid ExecutionUnitId,
    string? ProviderName,
    string Transport,
    string? SourceIp,
    string IpHash,
    string IpClassAB,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? ClosedAt,
    string Status,
    string? CloseReason,
    int AssignmentCount,
    AdminSessionSummary Summary,
    IReadOnlyList<AdminSessionEvent> Events);

public sealed record AdminSessionSummary(
    int EventCount,
    int Assigned,
    int Accepted,
    int Rejected,
    int Failed,
    int Completed,
    int ProgressUpdates);

public sealed record AdminSessionEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string Type,
    string Summary,
    Guid? TaskId,
    Guid? AttemptId);
