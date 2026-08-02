using MutualGPU.Application;
using MutualGPU.Domain;
using Npgsql;
using NpgsqlTypes;

namespace MutualGPU.Infrastructure;

internal sealed class PostgresTaskRepository(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    HandleCipher handles,
    MutualGpuObjectKeys objectKeys,
    ArtifactStorageTargetSelection storageTarget,
    OperationUsageGuard guard) : ITaskRepository
{
    private readonly Dictionary<TaskId, TrackedTask> tracked = [];

    public Task<TaskRequest?> GetAsync(
        RequestorId requestorId,
        TaskId id,
        CancellationToken cancellationToken) =>
        guard.RunAsync(async () =>
        {
            var task = await LoadAsync(id, requestorId, cancellationToken).ConfigureAwait(false);
            return task?.Aggregate;
        });

    public Task<TaskRequest?> GetAsync(TaskId id, CancellationToken cancellationToken) =>
        guard.RunAsync(async () => (await LoadAsync(id, null, cancellationToken).ConfigureAwait(false))?.Aggregate);

    public Task<TaskRequest?> GetByIdempotencyKeyAsync(
        RequestorId requestorId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        guard.RunAsync(async () =>
        {
            await using var command = new NpgsqlCommand(
                """
                select id
                from tasks
                where requestor_id = @requestor_id
                  and idempotency_key = @idempotency_key
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("requestor_id", requestorId.Value);
            command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            var id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return id is Guid value
                ? (await LoadAsync(new TaskId(value), requestorId, cancellationToken).ConfigureAwait(false))?.Aggregate
                : null;
        });

    public Task<IReadOnlyList<TaskRequest>> GetByRequestorAsync(
        RequestorId requestorId,
        CancellationToken cancellationToken) =>
        guard.RunAsync(async () =>
        {
            await using var command = new NpgsqlCommand(
                """
                select id
                from tasks
                where requestor_id = @requestor_id
                order by created_at desc, id desc
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("requestor_id", requestorId.Value);
            var ids = new List<TaskId>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    ids.Add(new TaskId(reader.GetGuid(0)));
                }
            }

            var tasks = new List<TaskRequest>(ids.Count);
            foreach (var id in ids)
            {
                var loaded = await LoadAsync(id, requestorId, cancellationToken).ConfigureAwait(false);
                if (loaded is not null) tasks.Add(loaded.Aggregate);
            }
            return (IReadOnlyList<TaskRequest>)tasks;
        });

    public Task SaveAsync(TaskRequest task, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Use Add or Update inside the operation unit of work.");

    public void Add(TaskRequest task) => guard.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(task);
        if (tracked.ContainsKey(task.Id))
        {
            throw new InvalidOperationException($"Task '{task.Id.Value:D}' is already tracked.");
        }

        tracked.Add(task.Id, new TrackedTask(task, 0, null, Added: true, Updated: false));
    });

    public void Update(TaskRequest task) => guard.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!tracked.TryGetValue(task.Id, out var current) || current.Added)
        {
            throw new InvalidOperationException($"Task '{task.Id.Value:D}' must be loaded before it can be updated.");
        }

        tracked[task.Id] = current with { Aggregate = task, Updated = true };
    });

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var item in tracked.Values.Where(static item => item.Added))
        {
            await InsertAsync(item.Aggregate, cancellationToken).ConfigureAwait(false);
        }

        foreach (var item in tracked.Values.Where(static item => item.Updated))
        {
            await UpdateAsync(item, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TrackedTask?> LoadAsync(
        TaskId id,
        RequestorId? requestorId,
        CancellationToken cancellationToken)
    {
        if (tracked.TryGetValue(id, out var existing))
        {
            return requestorId is null || existing.Aggregate.RequestorId == requestorId
                ? existing
                : null;
        }

        await using var command = new NpgsqlCommand(
            """
            select requestor_id,
                   capability_snapshot,
                   scalar_parameters,
                   compute_tier,
                   memory_gib,
                   created_at,
                   status,
                   version
            from tasks
            where id = @id
              and (@requestor_id is null or requestor_id = @requestor_id)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", id.Value);
        PostgresPersistence.AddNullableUuid(command, "requestor_id", requestorId?.Value);

        RequestorId owner;
        CapabilityDefinition capability;
        TaskParameters parameters;
        MachineSpecifications resources;
        DateTimeOffset createdAt;
        MutualGPU.Domain.TaskStatus status;
        long version;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            owner = new RequestorId(reader.GetGuid(0));
            capability = PostgresPersistence.Deserialize<CapabilityDefinition>(reader.GetString(1));
            parameters = PostgresPersistence.Deserialize<TaskParameters>(reader.GetString(2));
            resources = new MachineSpecifications((ResourceTier)reader.GetInt16(3), reader.GetInt32(4));
            createdAt = PostgresPersistence.Timestamp(reader, 5);
            status = reader.GetFieldValue<MutualGPU.Domain.TaskStatus>(6);
            version = reader.GetInt64(7);
        }

        var attempts = await LoadAttemptsAsync(id, cancellationToken).ConfigureAwait(false);
        var result = await LoadResultAsync(id, cancellationToken).ConfigureAwait(false);
        var snapshot = new TaskRequestSnapshot(
            id,
            owner,
            capability,
            resources,
            parameters,
            createdAt,
            status,
            attempts,
            result);
        var trackedTask = new TrackedTask(TaskRequest.Hydrate(snapshot), version, snapshot, Added: false, Updated: false);
        tracked.Add(id, trackedTask);
        return trackedTask;
    }

    private async Task<IReadOnlyList<TaskAttempt>> LoadAttemptsAsync(
        TaskId taskId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select id,
                   execution_unit_id,
                   handle_ciphertext,
                   assigned_at,
                   state,
                   accepted_at,
                   failure_step,
                   failure_reason,
                   disconnected_at,
                   provider_session_id,
                   provider_ip_hash,
                   provider_ip_class_ab,
                   provider_name,
                   provider_transport
            from task_attempts
            where task_id = @task_id
            order by assignment_number
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("task_id", taskId.Value);
        var attempts = new List<TaskAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attempts.Add(new TaskAttempt(
                new AttemptId(reader.GetGuid(0)),
                new ExecutionUnitId(reader.GetGuid(1)),
                handles.Decrypt(reader.GetFieldValue<byte[]>(2)),
                PostgresPersistence.Timestamp(reader, 3),
                reader.GetFieldValue<AttemptState>(4),
                PostgresPersistence.NullableTimestamp(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                PostgresPersistence.NullableTimestamp(reader, 8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13)));
        }
        return attempts;
    }

    private async Task<TaskResult?> LoadResultAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, role, content_type, length, sha256
            from artifacts
            where task_id = @task_id
              and direction = 'output'
              and state = 'available'
            order by role
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("task_id", taskId.Value);
        var artifacts = new Dictionary<string, ResultArtifact>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            artifacts[reader.GetString(1)] = new ResultArtifact(
                new ArtifactId(reader.GetGuid(0)),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4).Trim());
        }

        return artifacts.TryGetValue("result", out var zip)
            ? new TaskResult(
                zip,
                artifacts.GetValueOrDefault("thumbnail"),
                artifacts.GetValueOrDefault("preview"),
                artifacts.GetValueOrDefault("logs"),
                artifacts.GetValueOrDefault("metadata"))
            : null;
    }

    private async Task InsertAsync(TaskRequest task, CancellationToken cancellationToken)
    {
        var fingerprint = PostgresPersistence.SubmissionFingerprint(task);
        try
        {
            await using var command = new NpgsqlCommand(
                """
                insert into tasks(
                    id, requestor_id, capability_id, capability_contract_hash,
                    capability_snapshot, compute_tier, memory_gib, scalar_parameters,
                    idempotency_key, submission_fingerprint, requestor_ip_hash,
                    requestor_ip_class_ab, status, version, created_at, updated_at)
                values (
                    @id, @requestor_id, @capability_id, @contract_hash,
                    @capability_snapshot, @compute_tier, @memory_gib, @parameters,
                    @idempotency_key, @fingerprint, @requestor_ip_hash,
                    @requestor_ip_class_ab, @status, 1, @created_at, @updated_at)
                """,
                connection,
                transaction);
            AddTaskParameters(command, task, fingerprint);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation &&
            exception.ConstraintName == "tasks_requestor_idempotency_uq" &&
            task.Parameters.IdempotencyKey is not null)
        {
            throw new TaskIdempotencyConflictException(task.RequestorId, task.Parameters.IdempotencyKey);
        }

        for (var index = 0; index < task.Attempts.Count; index++)
        {
            await InsertAttemptAsync(task.Id, task.Attempts[index], index + 1, cancellationToken).ConfigureAwait(false);
        }
        await UpsertResultArtifactsAsync(task, cancellationToken).ConfigureAwait(false);
        await InsertInputArtifactAsync(task, cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(task, 1, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAsync(TrackedTask trackedTask, CancellationToken cancellationToken)
    {
        var task = trackedTask.Aggregate;
        await using (var command = new NpgsqlCommand(
            """
            update tasks
            set status = @status,
                updated_at = @updated_at,
                version = version + 1
            where id = @id
              and requestor_id = @requestor_id
              and version = @expected_version
            returning version
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("status", task.Status);
            command.Parameters.AddWithValue("updated_at", DateTime.UtcNow);
            command.Parameters.AddWithValue("id", task.Id.Value);
            command.Parameters.AddWithValue("requestor_id", task.RequestorId.Value);
            command.Parameters.AddWithValue("expected_version", trackedTask.Version);
            var updatedVersion = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (updatedVersion is not long)
            {
                throw new OptimisticConcurrencyException("task", task.Id.Value, trackedTask.Version);
            }
        }

        var originalAttempts = trackedTask.Original?.Attempts.ToDictionary(static attempt => attempt.Id) ?? [];
        for (var index = 0; index < task.Attempts.Count; index++)
        {
            var attempt = task.Attempts[index];
            if (!originalAttempts.ContainsKey(attempt.Id))
            {
                await InsertAttemptAsync(task.Id, attempt, index + 1, cancellationToken).ConfigureAwait(false);
            }
            else if (originalAttempts[attempt.Id] != attempt)
            {
                await UpdateAttemptAsync(task.Id, attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        await UpsertResultArtifactsAsync(task, cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(task, trackedTask.Version + 1, trackedTask.Original, cancellationToken).ConfigureAwait(false);
    }

    private static void AddTaskParameters(NpgsqlCommand command, TaskRequest task, string fingerprint)
    {
        command.Parameters.AddWithValue("id", task.Id.Value);
        command.Parameters.AddWithValue("requestor_id", task.RequestorId.Value);
        command.Parameters.AddWithValue("capability_id", task.Capability.Id.Value);
        command.Parameters.AddWithValue("contract_hash", task.Capability.ContractHash);
        command.Parameters.Add(PostgresPersistence.JsonParameter("capability_snapshot", task.Capability));
        command.Parameters.AddWithValue("compute_tier", (short)task.Resources.ComputeTier);
        command.Parameters.AddWithValue("memory_gib", task.Resources.MemoryGiB);
        command.Parameters.Add(PostgresPersistence.JsonParameter("parameters", task.Parameters));
        PostgresPersistence.AddNullableText(command, "idempotency_key", task.Parameters.IdempotencyKey);
        command.Parameters.Add("fingerprint", NpgsqlDbType.Char).Value = fingerprint;
        PostgresPersistence.AddNullableText(command, "requestor_ip_hash", task.Parameters.RequestorIpHash);
        PostgresPersistence.AddNullableText(command, "requestor_ip_class_ab", task.Parameters.RequestorIpClassAB);
        command.Parameters.AddWithValue("status", task.Status);
        command.Parameters.AddWithValue("created_at", PostgresPersistence.Utc(task.CreatedAt));
        command.Parameters.AddWithValue("updated_at", PostgresPersistence.Utc(task.CreatedAt));
    }

    private async Task InsertAttemptAsync(
        TaskId taskId,
        TaskAttempt attempt,
        int assignmentNumber,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into task_attempts(
                id, task_id, execution_unit_id, assignment_number,
                handle_digest, handle_ciphertext, state, assigned_at,
                accepted_at, disconnected_at, terminal_at, failure_step,
                failure_reason, provider_session_id, provider_ip_hash,
                provider_ip_class_ab, provider_name, provider_transport)
            values (
                @id, @task_id, @execution_unit_id, @assignment_number,
                @handle_digest, @handle_ciphertext, @state, @assigned_at,
                @accepted_at, @disconnected_at, @terminal_at, @failure_step,
                @failure_reason, @provider_session_id, @provider_ip_hash,
                @provider_ip_class_ab, @provider_name, @provider_transport)
            """,
            connection,
            transaction);
        AddAttemptParameters(command, taskId, attempt);
        command.Parameters.AddWithValue("assignment_number", (short)assignmentNumber);
        command.Parameters.Add("handle_ciphertext", NpgsqlDbType.Bytea).Value = handles.Encrypt(attempt.Handle);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAttemptAsync(
        TaskId taskId,
        TaskAttempt attempt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update task_attempts
            set state = @state,
                accepted_at = @accepted_at,
                disconnected_at = @disconnected_at,
                terminal_at = @terminal_at,
                failure_step = @failure_step,
                failure_reason = @failure_reason,
                provider_session_id = @provider_session_id,
                provider_ip_hash = @provider_ip_hash,
                provider_ip_class_ab = @provider_ip_class_ab,
                provider_name = @provider_name,
                provider_transport = @provider_transport
            where id = @id and task_id = @task_id
            """,
            connection,
            transaction);
        AddAttemptParameters(command, taskId, attempt);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new OptimisticConcurrencyException("task_attempt", attempt.Id.Value, 1);
        }
    }

    private void AddAttemptParameters(NpgsqlCommand command, TaskId taskId, TaskAttempt attempt)
    {
        command.Parameters.AddWithValue("id", attempt.Id.Value);
        command.Parameters.AddWithValue("task_id", taskId.Value);
        command.Parameters.AddWithValue("execution_unit_id", attempt.ExecutionUnitId.Value);
        command.Parameters.Add("handle_digest", NpgsqlDbType.Char).Value = handles.Digest(attempt.Handle);
        command.Parameters.AddWithValue("state", attempt.State);
        command.Parameters.AddWithValue("assigned_at", PostgresPersistence.Utc(attempt.AssignedAt));
        PostgresPersistence.AddNullableTimestamp(command, "accepted_at", attempt.AcceptedAt);
        PostgresPersistence.AddNullableTimestamp(command, "disconnected_at", attempt.DisconnectedAt);
        command.Parameters.Add("terminal_at", NpgsqlDbType.TimestampTz).Value =
            IsTerminal(attempt.State) ? DateTime.UtcNow : DBNull.Value;
        PostgresPersistence.AddNullableText(command, "failure_step", Bound(attempt.FailureStep, 128));
        PostgresPersistence.AddNullableText(command, "failure_reason", Bound(attempt.FailureReason, 1024));
        PostgresPersistence.AddNullableUuid(command, "provider_session_id", attempt.ProviderSessionId);
        PostgresPersistence.AddNullableText(command, "provider_ip_hash", attempt.ProviderIpHash);
        PostgresPersistence.AddNullableText(command, "provider_ip_class_ab", attempt.ProviderIpClassAB);
        PostgresPersistence.AddNullableText(command, "provider_name", attempt.ProviderName);
        PostgresPersistence.AddNullableText(command, "provider_transport", attempt.ProviderTransport);
    }

    private async Task InsertInputArtifactAsync(TaskRequest task, CancellationToken cancellationToken)
    {
        if (task.Parameters.Image is not { } image ||
            task.Parameters.ImageSha256 is not { Length: > 0 } sha256)
        {
            return;
        }

        var key = objectKeys.TaskInput(
            task.RequestorId,
            task.Id,
            image,
            task.Parameters.ImageExtension ?? "bin");
        await using var command = new NpgsqlCommand(
            """
            insert into artifacts(
                id, task_id, attempt_id, direction, role, s3_object_key,
                content_type, length, sha256, state, created_at)
            values (
                @id, @task_id, null, 'input', 'input', @s3_object_key,
                @content_type, @length, @sha256, 'available', @created_at)
            on conflict (s3_object_key) do nothing
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", image.Value);
        command.Parameters.AddWithValue("task_id", task.Id.Value);
        command.Parameters.AddWithValue("s3_object_key", key.Value);
        command.Parameters.AddWithValue("content_type", task.Parameters.ImageContentType ?? "application/octet-stream");
        command.Parameters.AddWithValue("length", task.Parameters.ImageLength ?? 0);
        command.Parameters.Add("sha256", NpgsqlDbType.Char).Value = sha256.ToLowerInvariant();
        command.Parameters.AddWithValue("created_at", PostgresPersistence.Utc(task.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InsertSelectedLocationAsync(
            image,
            key.Value,
            ArtifactLocationState.Available,
            task.CreatedAt,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertResultArtifactsAsync(TaskRequest task, CancellationToken cancellationToken)
    {
        if (task.Result is null) return;
        var attempt = task.Attempts.LastOrDefault(static attempt => attempt.State is AttemptState.Completed)
            ?? throw new InvalidOperationException("A completed task result requires a completed attempt.");
        foreach (var (role, artifact, key) in ResultArtifacts(task, attempt))
        {
            if (artifact is null) continue;
            await using var command = new NpgsqlCommand(
                """
                insert into artifacts(
                    id, task_id, attempt_id, direction, role, s3_object_key,
                    content_type, length, sha256, state, created_at)
                values (
                    @id, @task_id, @attempt_id, 'output', @role, @s3_object_key,
                    @content_type, @length, @sha256, 'available', @created_at)
                on conflict (s3_object_key) do nothing
                returning id
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("id", artifact.Id.Value);
            command.Parameters.AddWithValue("task_id", task.Id.Value);
            command.Parameters.AddWithValue("attempt_id", attempt.Id.Value);
            command.Parameters.AddWithValue("role", role);
            command.Parameters.AddWithValue("s3_object_key", key.Value);
            command.Parameters.AddWithValue("content_type", artifact.ContentType);
            command.Parameters.AddWithValue("length", artifact.Length);
            command.Parameters.Add("sha256", NpgsqlDbType.Char).Value = artifact.Sha256.ToLowerInvariant();
            command.Parameters.AddWithValue("created_at", DateTime.UtcNow);
            var artifactId = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (artifactId is Guid persistedArtifactId)
            {
                await InsertSelectedLocationAsync(
                    new ArtifactId(persistedArtifactId),
                    key.Value,
                    ArtifactLocationState.Available,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task InsertSelectedLocationAsync(
        ArtifactId artifactId,
        string objectKey,
        ArtifactLocationState state,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into artifact_locations(
                artifact_id, storage_target_id, object_key, provider_etag,
                state, created_at, last_verified_at)
            values (
                @artifact_id, @storage_target_id, @object_key, null,
                @state, @created_at, null)
            on conflict (artifact_id, storage_target_id) do nothing
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("artifact_id", artifactId.Value);
        command.Parameters.AddWithValue("storage_target_id", storageTarget.WriteStorageTargetId);
        command.Parameters.AddWithValue("object_key", objectKey);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("created_at", PostgresPersistence.Utc(createdAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private IEnumerable<(string Role, ResultArtifact? Artifact, ObjectKey Key)> ResultArtifacts(
        TaskRequest task,
        TaskAttempt attempt)
    {
        yield return ("result", task.Result!.Zip, objectKeys.ResultZip(task.RequestorId, task.Id, attempt.Id));
        yield return ("thumbnail", task.Result.Thumbnail, objectKeys.ResultThumbnail(task.RequestorId, task.Id, attempt.Id, ExtensionFor(task.Result.Thumbnail)));
        yield return ("preview", task.Result.Preview, objectKeys.ResultPreview(task.RequestorId, task.Id, attempt.Id, ExtensionFor(task.Result.Preview)));
        yield return ("logs", task.Result.Logs, objectKeys.ResultLogs(task.RequestorId, task.Id, attempt.Id));
        yield return ("metadata", task.Result.Metadata, objectKeys.ResultMetadata(task.RequestorId, task.Id, attempt.Id));
    }

    private async Task InsertEventAsync(
        TaskRequest task,
        long taskVersion,
        TaskRequestSnapshot? original,
        CancellationToken cancellationToken)
    {
        var attemptId = PostgresPersistence.ChangedAttempt(original, task);
        await using var command = new NpgsqlCommand(
            """
            insert into task_events(
                id, task_id, task_version, attempt_id, event_type, occurred_at, payload)
            values (
                @id, @task_id, @task_version, @attempt_id, @event_type, @occurred_at, @payload)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("task_id", task.Id.Value);
        command.Parameters.AddWithValue("task_version", taskVersion);
        PostgresPersistence.AddNullableUuid(command, "attempt_id", attemptId?.Value);
        command.Parameters.AddWithValue("event_type", PostgresPersistence.EventType(original, task));
        command.Parameters.AddWithValue("occurred_at", DateTime.UtcNow);
        command.Parameters.Add(PostgresPersistence.JsonParameter("payload", new
        {
            status = task.Status,
            attemptState = attemptId is null
                ? (AttemptState?)null
                : task.Attempts.Single(attempt => attempt.Id == attemptId.Value).State,
        }));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTerminal(AttemptState state) =>
        state is AttemptState.Rejected or AttemptState.Revoked or AttemptState.Cancelled or AttemptState.Completed or AttemptState.Failed;

    private static string? Bound(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

    private static string ExtensionFor(ResultArtifact? artifact) => artifact?.ContentType switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/webp" => "webp",
        _ => "bin",
    };

    private sealed record TrackedTask(
        TaskRequest Aggregate,
        long Version,
        TaskRequestSnapshot? Original,
        bool Added,
        bool Updated);
}
