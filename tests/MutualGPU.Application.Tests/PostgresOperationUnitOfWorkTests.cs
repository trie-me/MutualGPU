using MutualGPU.Application;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace MutualGPU.Application.Tests;

public sealed class PostgresOperationUnitOfWorkTests
{
    [PostgresFact]
    public async Task Concurrent_writers_cannot_both_commit_the_same_task_version()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var task = await fixture.InsertQueuedTaskAsync();
        var bothLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadedCount = 0;

        async Task<bool> WriteAsync()
        {
            try
            {
                return await fixture.Operations.ExecuteAsync(
                    async (context, token) =>
                    {
                        var current = await context.Tasks.GetAsync(task.Id, token);
                        Assert.NotNull(current);
                        current.Cancel();
                        context.Tasks.Update(current);
                        if (Interlocked.Increment(ref loadedCount) == 2) bothLoaded.TrySetResult();
                        await release.Task.WaitAsync(token);
                        return true;
                    },
                    CancellationToken.None);
            }
            catch (OptimisticConcurrencyException)
            {
                return false;
            }
        }

        var first = WriteAsync();
        var second = WriteAsync();
        await bothLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, static committed => committed);
        Assert.Single(results, static committed => !committed);
        var persisted = await fixture.Tasks.GetAsync(task.Id, CancellationToken.None);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Cancelled, persisted?.Status);
    }

    [PostgresFact]
    public async Task Later_flush_failure_rolls_back_task_audit_artifact_and_outbox_changes()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var task = await fixture.InsertQueuedTaskAsync();
        var duplicateKey = $"mutualgpu/test/{Guid.CreateVersion7():N}/same.bin";
        var firstArtifact = new ArtifactDescriptor(
            ArtifactId.New(),
            task.Id,
            null,
            ArtifactDirection.Input,
            "input",
            duplicateKey,
            "application/octet-stream",
            1,
            new string('a', 64),
            ArtifactState.Available,
            DateTimeOffset.UtcNow);
        await fixture.Operations.ExecuteAsync(
            (context, _) =>
            {
                context.Artifacts.Add(firstArtifact);
                return Task.FromResult(true);
            },
            CancellationToken.None);

        await Assert.ThrowsAsync<PostgresException>(() => fixture.Operations.ExecuteAsync(
            async (context, token) =>
            {
                var current = await context.Tasks.GetAsync(task.Id, token);
                Assert.NotNull(current);
                current.Cancel();
                context.Tasks.Update(current);
                context.Artifacts.Add(firstArtifact with { Id = ArtifactId.New() });
                context.Publish(OperationEvent.TaskChanged(current.RequestorId, DateTimeOffset.UtcNow));
                return true;
            },
            CancellationToken.None));

        var persisted = await fixture.Tasks.GetAsync(task.Id, CancellationToken.None);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Queued, persisted?.Status);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var outbox = new NpgsqlCommand(
            "select count(*) from operation_outbox where payload ->> 'requestorId' = @requestor_id",
            connection);
        outbox.Parameters.AddWithValue("requestor_id", task.RequestorId.Value.ToString("D"));
        Assert.Equal(0L, await outbox.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task Upload_token_is_one_time_and_completion_replays_after_store_restart()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var (task, unitId, attemptId, handle) = await fixture.InsertAcceptedTaskAsync();
        var uploads = fixture.CreateUploadStore();
        var now = DateTimeOffset.UtcNow;

        var authorization = await uploads.IssueAsync(
            unitId,
            task.Id,
            attemptId,
            handle,
            now,
            CancellationToken.None);
        Assert.True(await uploads.TryConsumeAsync(
            unitId,
            task.Id,
            attemptId,
            handle,
            authorization.Token,
            now,
            CancellationToken.None));
        Assert.False(await uploads.TryConsumeAsync(
            unitId,
            task.Id,
            attemptId,
            handle,
            authorization.Token,
            now,
            CancellationToken.None));

        var result = new StagedResult(
            $"receipt-{Guid.CreateVersion7():N}",
            new TaskResult(new ResultArtifact(
                ArtifactId.New(),
                "application/zip",
                42,
                new string('b', 64))));
        await uploads.StageAsync(
            unitId,
            task.Id,
            attemptId,
            handle,
            result,
            CancellationToken.None);

        // A fresh adapter simulates loss of every process-local upload object.
        uploads = fixture.CreateUploadStore();
        var sessions = new ProviderSessionApplication(
            fixture.Tasks,
            new NoAssignments(),
            uploads,
            new NoProgress(),
            new NoEvents(),
            TimeProvider.System,
            fixture.Operations);
        Assert.True(await sessions
            .Complete(unitId, task.Id, attemptId, handle, result.Receipt)
            .RunAsync(CancellationToken.None));

        sessions = new ProviderSessionApplication(
            fixture.Tasks,
            new NoAssignments(),
            fixture.CreateUploadStore(),
            new NoProgress(),
            new NoEvents(),
            TimeProvider.System,
            fixture.Operations);
        Assert.True(await sessions
            .Complete(unitId, task.Id, attemptId, handle, result.Receipt)
            .RunAsync(CancellationToken.None));

        var persisted = await fixture.Tasks.GetAsync(task.Id, CancellationToken.None);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Completed, persisted?.Status);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select u.token_digest, u.state::text, a.state::text, a.s3_object_key
            from result_upload_operations u
            join artifacts a on a.result_upload_operation_id = u.id
            where u.task_id = @task_id and a.role = 'result'
            """,
            connection);
        command.Parameters.AddWithValue("task_id", task.Id.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.NotEqual(authorization.Token, reader.GetString(0).Trim());
        Assert.Equal(64, reader.GetString(0).Trim().Length);
        Assert.Equal("completed", reader.GetString(1));
        Assert.Equal("available", reader.GetString(2));
        Assert.Equal(
            fixture.ObjectKeys.ResultZip(task.RequestorId, task.Id, attemptId).Value,
            reader.GetString(3));
    }

    [PostgresFact]
    public async Task Requestor_task_reader_uses_stable_keyset_pagination()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var requestorId = RequestorId.New();
        var oldest = await fixture.InsertQueuedTaskAsync(requestorId, DateTimeOffset.UtcNow.AddMinutes(-2));
        var middle = await fixture.InsertQueuedTaskAsync(requestorId, DateTimeOffset.UtcNow.AddMinutes(-1));
        var newest = await fixture.InsertQueuedTaskAsync(requestorId, DateTimeOffset.UtcNow);

        var first = await fixture.Tasks.ListSummariesPageAsync(
            requestorId,
            new PageRequest(2),
            CancellationToken.None);
        var second = await fixture.Tasks.ListSummariesPageAsync(
            requestorId,
            new PageRequest(2, first.NextCursor),
            CancellationToken.None);

        Assert.Equal([newest.Id, middle.Id], first.Items.Select(static item => item.TaskId));
        Assert.NotNull(first.NextCursor);
        Assert.Equal([oldest.Id], second.Items.Select(static item => item.TaskId));
        Assert.Null(second.NextCursor);
    }

    [PostgresFact]
    public async Task Disconnected_attempt_rebinds_from_database_on_another_replica()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var (task, unitId, attemptId, handle) = await fixture.InsertAcceptedTaskAsync();
        var firstReplica = new ProviderConnectionRegistry();
        firstReplica.Track(unitId, task, task.Attempts.Single(item => item.Id == attemptId));
        var firstSessions = new ProviderSessionApplication(
            fixture.Tasks,
            firstReplica,
            fixture.CreateUploadStore(),
            firstReplica,
            new NoEvents(),
            TimeProvider.System,
            fixture.Operations);

        Assert.Equal(1, await firstSessions.Disconnect(unitId).RunAsync(CancellationToken.None));
        var disconnected = await fixture.Tasks.GetAsync(task.Id, CancellationToken.None);
        Assert.Equal(
            AttemptState.Disconnected,
            disconnected?.Attempts.Single(item => item.Id == attemptId).State);

        var secondReplica = new ProviderConnectionRegistry();
        var secondSessions = new ProviderSessionApplication(
            fixture.Tasks,
            secondReplica,
            fixture.CreateUploadStore(),
            secondReplica,
            new NoEvents(),
            TimeProvider.System,
            fixture.Operations);
        Assert.True(await secondSessions.Rebind(unitId, handle).RunAsync(CancellationToken.None));
        Assert.True(secondReplica.TryGet(unitId, task.Id, attemptId, handle, out _));

        var rebound = await fixture.Tasks.GetAsync(task.Id, CancellationToken.None);
        Assert.Equal(
            AttemptState.Accepted,
            rebound?.Attempts.Single(item => item.Id == attemptId).State);
        Assert.Equal(
            0,
            await fixture.Tasks.RecoverAsync(
                DateTimeOffset.UtcNow.AddDays(1),
                CancellationToken.None));
    }

    [PostgresFact]
    public async Task Accepted_attempt_updates_the_local_projection_before_immediate_progress()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var (task, unitId, attemptId, handle) = await fixture.InsertAcceptedTaskAsync();
        var assigned = await fixture.Operations.ExecuteAsync(
            async (context, token) =>
            {
                var current = await context.Tasks.GetAsync(task.Id, token);
                Assert.NotNull(current);
                var attempt = current.Attempts.Single(item => item.Id == attemptId);
                current.Requeue(
                    attempt.Id,
                    attempt.Handle,
                    AttemptState.Revoked,
                    "test_reset",
                    "Reset accepted fixture to assigned state.");
                var replacement = current.Assign(
                    AttemptId.New(),
                    unitId,
                    "immediate-progress-handle",
                    DateTimeOffset.UtcNow);
                context.Tasks.Update(current);
                return (Task: current, Attempt: replacement);
            },
            CancellationToken.None);
        var registry = new ProviderConnectionRegistry();
        registry.Track(unitId, assigned.Task, assigned.Attempt);
        var sessions = new ProviderSessionApplication(
            fixture.Tasks,
            registry,
            fixture.CreateUploadStore(),
            registry,
            new NoEvents(),
            TimeProvider.System,
            fixture.Operations);

        Assert.True(await sessions.Accept(unitId, assigned.Task.Id, assigned.Attempt.Id, assigned.Attempt.Handle, DateTimeOffset.UtcNow)
            .RunAsync(CancellationToken.None));
        Assert.Equal(
            ProviderProgressDisposition.Accepted,
            sessions.ReportProgress(
                unitId,
                assigned.Task.Id,
                assigned.Attempt.Id,
                assigned.Attempt.Handle,
                new TaskProgress(1, DateTimeOffset.UtcNow, "immediate", 1)));
    }

    [PostgresFact]
    public async Task Orphan_reconciliation_never_deletes_a_key_referenced_by_another_committed_descriptor()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var (task, unitId, attemptId, handle) = await fixture.InsertAcceptedTaskAsync();
        var now = DateTimeOffset.UtcNow.AddDays(1);
        var operation = new ResultUploadOperation(
            ResultUploadOperationId.New(),
            task.Id,
            attemptId,
            unitId,
            new HandleCipher(fixture.Options).Digest(handle),
            null,
            null,
            ResultUploadState.Expired,
            now.AddHours(-1),
            1,
            now.AddHours(-2));
        var key = fixture.ObjectKeys.ResultZip(task.RequestorId, task.Id, attemptId);
        await fixture.Operations.ExecuteAsync(
            (context, _) =>
            {
                context.ResultUploads.Add(operation);
                context.Artifacts.Add(new ArtifactDescriptor(
                    ArtifactId.New(),
                    task.Id,
                    attemptId,
                    ArtifactDirection.Output,
                    "result",
                    key.Value,
                    "application/zip",
                    1,
                    new string('c', 64),
                    ArtifactState.Available,
                    now.AddHours(-1)));
                return Task.FromResult(true);
            },
            CancellationToken.None);

        var objects = new InMemoryObjectStore();
        await using (var content = new MemoryStream([1]))
        {
            await objects.PutAsync(
                key,
                content,
                ObjectWriteConditions.IfNotExists,
                CancellationToken.None);
        }
        var reconciler = new PostgresOrphanArtifactReconciler(
            fixture.DataSource,
            objects,
            fixture.ObjectKeys,
            new FixedTimeProvider(now),
            NullLogger<PostgresOrphanArtifactReconciler>.Instance);
        await reconciler.ReconcileOnceAsync(CancellationToken.None);

        await using var retained = await objects.GetAsync(key, CancellationToken.None);
        Assert.NotNull(retained);
    }

    private sealed class PostgresFixture : IAsyncDisposable
    {
        private PostgresFixture(
            NpgsqlDataSource dataSource,
            PostgresOperationUnitOfWork operations,
            PostgresTaskStore tasks,
            PostgresOptions options,
            MutualGpuObjectKeys objectKeys)
        {
            DataSource = dataSource;
            Operations = operations;
            Tasks = tasks;
            Options = options;
            ObjectKeys = objectKeys;
        }

        public NpgsqlDataSource DataSource { get; }

        public PostgresOperationUnitOfWork Operations { get; }

        public PostgresTaskStore Tasks { get; }

        public PostgresOptions Options { get; }

        public MutualGpuObjectKeys ObjectKeys { get; }

        public static async Task<PostgresFixture> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("MUTUALGPU_TEST_POSTGRES")
                ?? throw new InvalidOperationException("MUTUALGPU_TEST_POSTGRES is required.");
            var options = new PostgresOptions
            {
                ConnectionString = connectionString,
                HandleEncryptionKey = "bXV0dWFsZ3B1LWxvY2FsLWRldmVsb3BtZW50LWtleSE=",
                MaximumPoolSize = 10,
            };
            var dataSource = PostgresDataSourceFactory.Create(options);
            await new PostgresMigrator(dataSource).MigrateAsync(CancellationToken.None);
            var objectKeys = new MutualGpuObjectKeys();
            var operations = new PostgresOperationUnitOfWork(
                dataSource,
                new HandleCipher(options),
                objectKeys);
            return new PostgresFixture(
                dataSource,
                operations,
                new PostgresTaskStore(dataSource, operations, new HandleCipher(options)),
                options,
                objectKeys);
        }

        public PostgresResultUploadStore CreateUploadStore() =>
            new(DataSource, Operations, new HandleCipher(Options), ObjectKeys);

        public async Task<TaskRequest> InsertQueuedTaskAsync(
            RequestorId? requestorId = null,
            DateTimeOffset? createdAt = null)
        {
            var capability = new CapabilityDefinition(
                CapabilityId.New(),
                $"postgres-test-{Guid.CreateVersion7():N}",
                [],
                new OutputDefinition(),
                Guid.CreateVersion7().ToString("N"));
            var task = new TaskRequest(
                TaskId.New(),
                requestorId ?? RequestorId.New(),
                capability,
                new MachineSpecifications(ResourceTier.Small, 8),
                new TaskParameters(new Dictionary<string, string>(), null),
                createdAt ?? DateTimeOffset.UtcNow);
            await Operations.ExecuteAsync(
                (context, _) =>
                {
                    context.Capabilities.Add(capability);
                    context.Tasks.Add(task);
                    return Task.FromResult(true);
                },
                CancellationToken.None);
            return task;
        }

        public async Task<(TaskRequest Task, ExecutionUnitId UnitId, AttemptId AttemptId, string Handle)> InsertAcceptedTaskAsync()
        {
            var capability = new CapabilityDefinition(
                CapabilityId.New(),
                $"postgres-upload-test-{Guid.CreateVersion7():N}",
                [],
                new OutputDefinition(),
                Guid.CreateVersion7().ToString("N"));
            var unitId = ExecutionUnitId.New();
            var unit = new ExecutionUnit(
                unitId,
                new EnrollmentDefinition(
                    new MachineProfile(
                        ResourceTier.Small,
                        new MachineSpecifications(ResourceTier.Small, 8)),
                    [capability]));
            var task = new TaskRequest(
                TaskId.New(),
                RequestorId.New(),
                capability,
                new MachineSpecifications(ResourceTier.Small, 8),
                new TaskParameters(new Dictionary<string, string>(), null),
                DateTimeOffset.UtcNow);
            var attemptId = AttemptId.New();
            const string handle = "postgres-upload-restart-handle";
            task.Assign(attemptId, unitId, handle, DateTimeOffset.UtcNow);
            task.Accept(attemptId, handle, DateTimeOffset.UtcNow);
            await Operations.ExecuteAsync(
                (context, _) =>
                {
                    context.Capabilities.Add(capability);
                    context.ExecutionUnits.Add(unit);
                    context.Tasks.Add(task);
                    return Task.FromResult(true);
                },
                CancellationToken.None);
            return (task, unitId, attemptId, handle);
        }

        public async ValueTask DisposeAsync() =>
            await DataSource.DisposeAsync();
    }

    private sealed class NoAssignments : IProviderAssignments
    {
        public bool TryDeliver(ExecutionUnitId executionUnitId, ProviderAssignment assignment) => false;
        public bool TryCancel(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId, string handle) => false;
        public void Track(ExecutionUnitId executionUnitId, TaskRequest task, TaskAttempt attempt) { }
        public bool TryGet(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId, string handle, out TaskRequest task)
        {
            task = null!;
            return false;
        }
        public void Remove(ExecutionUnitId executionUnitId, TaskId taskId, AttemptId attemptId) { }
        public IReadOnlyList<ActiveProviderAssignment> GetUnacceptedBefore(DateTimeOffset deadline) => [];
        public IReadOnlyList<ActiveProviderAssignment> GetForExecutionUnit(ExecutionUnitId executionUnitId) => [];
        public IReadOnlyList<ActiveProviderAssignment> GetDisconnectedBefore(DateTimeOffset deadline) => [];
        public bool TryGetByHandle(ExecutionUnitId executionUnitId, string handle, out ActiveProviderAssignment assignment)
        {
            assignment = null!;
            return false;
        }
    }

    private sealed class NoProgress : IProviderProgress
    {
        public ProviderProgressDisposition Report(
            ExecutionUnitId unitId,
            TaskId taskId,
            AttemptId attemptId,
            string handle,
            TaskProgress progress) => ProviderProgressDisposition.Accepted;
        public TaskProgress? Get(TaskId taskId) => null;
        public void Remove(TaskId taskId, AttemptId attemptId) { }
    }

    private sealed class NoEvents : IApplicationEventSink
    {
        public void TriggerScheduler() { }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUTUALGPU_TEST_POSTGRES")))
            {
                Skip = "Set MUTUALGPU_TEST_POSTGRES to run PostgreSQL integration tests.";
            }
        }
    }
}
