using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MutualGPU.Api;
using MutualGPU.Application;
using MutualGPU.Domain;
using NetCats.Testing;

namespace MutualGPU.Api.Tests;

public sealed class DisconnectRecoveryTests
{
    [Fact]
    public async Task Recovery_checks_the_five_configured_delays_then_revokes_the_disconnected_attempt()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var fixture = new Fixture(time);
        await fixture.Session.Disconnect(fixture.UnitId).RunAsync(CancellationToken.None);
        fixture.Recovery.Start(fixture.UnitId);

        foreach (var delay in new[] { 5, 10, 20, 40, 80 })
        {
            time.Advance(TimeSpan.FromSeconds(delay));
            await DrainAsync();
        }

        var attempt = Assert.Single(fixture.Task.Attempts);
        Assert.Equal(AttemptState.Revoked, attempt.State);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Queued, fixture.Task.Status);
        // One read occurs during disconnect, five during the configured grace checks,
        // and one final read selects the disconnected attempt for revocation.
        Assert.Equal(7, fixture.Assignments.DisconnectedChecks);
        Assert.Equal(1, fixture.Events.TriggerCount);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task Recovery_preserves_rebound_assignment_instead_of_revoking_it()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var fixture = new Fixture(time);
        await fixture.Session.Disconnect(fixture.UnitId).RunAsync(CancellationToken.None);
        fixture.Recovery.Start(fixture.UnitId);

        time.Advance(TimeSpan.FromSeconds(5));
        await DrainAsync();
        Assert.True(await fixture.Session.Rebind(fixture.UnitId, fixture.Attempt.Handle).RunAsync(CancellationToken.None));

        time.Advance(TimeSpan.FromSeconds(10));
        await DrainAsync();

        Assert.Equal(AttemptState.Accepted, Assert.Single(fixture.Task.Attempts).State);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Running, fixture.Task.Status);
        Assert.Equal(3, fixture.Assignments.DisconnectedChecks);
        await fixture.DisposeAsync();
    }

    private static async Task DrainAsync()
    {
        for (var count = 0; count < 12; count++) await Task.Yield();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MutualGpuFiberOwner fibers;

        public Fixture(TimeProvider time)
        {
            UnitId = ExecutionUnitId.New();
            var capability = new CapabilityDefinition(CapabilityId.New(), "recovery", [], new OutputDefinition(), "contract");
            Task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), time.GetUtcNow());
            Attempt = Task.Assign(AttemptId.New(), UnitId, "handle", time.GetUtcNow());
            Task.Accept(Attempt.Id, Attempt.Handle, time.GetUtcNow());
            Assignments = new Assignments(UnitId, Task, Attempt);
            Events = new Events();
            var session = new ProviderSessionApplication(new Tasks(), Assignments, new StagedResults(), new Progress(), Events, time);
            Session = session;
            fibers = new MutualGpuFiberOwner(new ServiceCollection().BuildServiceProvider());
            Recovery = new DisconnectRecoveryService(session, Assignments, time, fibers, NullLogger<DisconnectRecoveryService>.Instance);
        }

        public ExecutionUnitId UnitId { get; }

        public TaskRequest Task { get; }

        public TaskAttempt Attempt { get; }

        public Assignments Assignments { get; }

        public Events Events { get; }

        public ProviderSessionApplication Session { get; }

        public DisconnectRecoveryService Recovery { get; }

        public async ValueTask DisposeAsync()
        {
            await Recovery.StopAsync(CancellationToken.None);
            await fibers.StopAsync(CancellationToken.None);
        }
    }

    private sealed class Tasks : ITaskRepository
    {
        public Task<TaskRequest?> GetAsync(RequestorId requestorId, TaskId id, CancellationToken cancellationToken) => Task.FromResult<TaskRequest?>(null);

        public Task<IReadOnlyList<TaskRequest>> GetByRequestorAsync(RequestorId requestorId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskRequest>>([]);

        public Task SaveAsync(TaskRequest task, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Assignments(ExecutionUnitId unitId, TaskRequest task, TaskAttempt attempt) : IProviderAssignments
    {
        private bool active = true;

        public int DisconnectedChecks { get; private set; }

        public bool TryDeliver(ExecutionUnitId executionUnitId, ProviderAssignment assignment) => false;

        public void Track(ExecutionUnitId executionUnitId, TaskRequest trackedTask, TaskAttempt trackedAttempt) { }

        public bool TryGet(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId, string handle, out TaskRequest found)
        {
            if (active && executionUnitId == unitId && taskId == task.Id && attemptId == attempt.Id && StringComparer.Ordinal.Equals(handle, attempt.Handle))
            {
                found = task;
                return true;
            }
            found = null!;
            return false;
        }

        public void Remove(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId)
        {
            if (executionUnitId == unitId && taskId == task.Id && attemptId == attempt.Id) active = false;
        }

        public IReadOnlyList<ActiveProviderAssignment> GetUnacceptedBefore(DateTimeOffset deadline) => [];

        public IReadOnlyList<ActiveProviderAssignment> GetForExecutionUnit(ExecutionUnitId executionUnitId)
        {
            DisconnectedChecks++;
            return active && executionUnitId == unitId ? [new ActiveProviderAssignment(unitId, task, attempt)] : [];
        }

        public IReadOnlyList<ActiveProviderAssignment> GetDisconnectedBefore(DateTimeOffset deadline) => [];

        public bool TryGetByHandle(ExecutionUnitId executionUnitId, string handle, out ActiveProviderAssignment assignment)
        {
            if (active && executionUnitId == unitId && StringComparer.Ordinal.Equals(handle, attempt.Handle))
            {
                assignment = new ActiveProviderAssignment(unitId, task, attempt);
                return true;
            }
            assignment = null!;
            return false;
        }
    }

    private sealed class Progress : IProviderProgress
    {
        public bool TryReport(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, TaskProgress progress) => false;
        public TaskProgress? Get(TaskId taskId) => null;
        public void Remove(TaskId taskId) { }
    }

    private sealed class StagedResults : IStagedResults
    {
        public void Stage(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, StagedResult result) { }
        public bool TryTake(ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string receipt, out StagedResult result) { result = null!; return false; }
    }

    private sealed class Events : IApplicationEventSink
    {
        public int TriggerCount { get; private set; }
        public void TriggerScheduler() => TriggerCount++;
    }
}
