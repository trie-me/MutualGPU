using MutualGPU.Application;
using MutualGPU.Domain;
using Npgsql;
using NpgsqlTypes;

namespace MutualGPU.Infrastructure;

internal sealed class PostgresCapabilityRepository(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    OperationUsageGuard guard) : ICapabilityRepository
{
    private readonly Dictionary<CapabilityId, CapabilityDefinition> added = [];

    public Task<CapabilityDefinition?> GetAsync(CapabilityId id, CancellationToken cancellationToken) =>
        guard.RunAsync(async () =>
        {
            if (added.TryGetValue(id, out var staged)) return staged;
            await using var command = new NpgsqlCommand(
                "select definition from capabilities where id = @id",
                connection,
                transaction);
            command.Parameters.AddWithValue("id", id.Value);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
                ? PostgresPersistence.Deserialize<CapabilityDefinition>(json)
                : null;
        });

    public Task<IReadOnlyList<CapabilityDefinition>> GetAllAsync(CancellationToken cancellationToken) =>
        guard.RunAsync(async () =>
        {
            await using var command = new NpgsqlCommand(
                "select definition from capabilities order by normalized_name, id",
                connection,
                transaction);
            var definitions = new List<CapabilityDefinition>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                definitions.Add(PostgresPersistence.Deserialize<CapabilityDefinition>(reader.GetString(0)));
            }
            definitions.AddRange(added.Values);
            return (IReadOnlyList<CapabilityDefinition>)definitions;
        });

    public void Add(CapabilityDefinition capability) => guard.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(capability);
        capability.Validate();
        added.TryAdd(capability.Id, capability);
    });

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var capability in added.Values)
        {
            try
            {
                await using var command = new NpgsqlCommand(
                    """
                    insert into capabilities(
                        id, name, normalized_name, contract_hash, definition, created_at)
                    values (@id, @name, @normalized_name, @contract_hash, @definition, @created_at)
                    """,
                    connection,
                    transaction);
                command.Parameters.AddWithValue("id", capability.Id.Value);
                command.Parameters.AddWithValue("name", capability.Name);
                command.Parameters.AddWithValue("normalized_name", PostgresPersistence.NormalizeCapabilityName(capability.Name));
                command.Parameters.AddWithValue("contract_hash", capability.ContractHash);
                command.Parameters.Add(PostgresPersistence.JsonParameter("definition", capability));
                command.Parameters.AddWithValue("created_at", DateTime.UtcNow);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation &&
                exception.ConstraintName == "capabilities_normalized_name_key")
            {
                throw new CapabilityNameConflictException(PostgresPersistence.NormalizeCapabilityName(capability.Name));
            }
        }
    }
}

internal sealed class PostgresExecutionUnitRepository(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    OperationUsageGuard guard) : IExecutionUnitRepository
{
    private readonly Dictionary<ExecutionUnitId, TrackedExecutionUnit> tracked = [];

    public Task<ExecutionUnit?> GetAsync(ExecutionUnitId id, CancellationToken cancellationToken) =>
        guard.RunAsync(async () => (await LoadAsync(id, cancellationToken).ConfigureAwait(false))?.Aggregate);

    public Task<IReadOnlyList<CapabilityDefinition>> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        guard.RunAsync(async () =>
        {
            await using var command = new NpgsqlCommand(
                "select definition from capabilities order by normalized_name, id",
                connection,
                transaction);
            var definitions = new List<CapabilityDefinition>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                definitions.Add(PostgresPersistence.Deserialize<CapabilityDefinition>(reader.GetString(0)));
            }
            return (IReadOnlyList<CapabilityDefinition>)definitions;
        });

    public Task SaveAsync(ExecutionUnit executionUnit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Use Add or Update inside the operation unit of work.");

    public void Add(ExecutionUnit executionUnit) => guard.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(executionUnit);
        if (tracked.ContainsKey(executionUnit.Id))
        {
            throw new InvalidOperationException($"Execution unit '{executionUnit.Id.Value:D}' is already tracked.");
        }
        tracked.Add(executionUnit.Id, new TrackedExecutionUnit(executionUnit, 0, Added: true, Updated: false));
    });

    public void Update(ExecutionUnit executionUnit) => guard.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(executionUnit);
        if (!tracked.TryGetValue(executionUnit.Id, out var current) || current.Added)
        {
            throw new InvalidOperationException($"Execution unit '{executionUnit.Id.Value:D}' must be loaded before it can be updated.");
        }
        tracked[executionUnit.Id] = current with { Aggregate = executionUnit, Updated = true };
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

    private async Task<TrackedExecutionUnit?> LoadAsync(
        ExecutionUnitId id,
        CancellationToken cancellationToken)
    {
        if (tracked.TryGetValue(id, out var existing)) return existing;
        await using var command = new NpgsqlCommand(
            """
            select enrollment_version, persistence_version, current_enrollment
            from execution_units
            where id = @id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var snapshot = new ExecutionUnitSnapshot(
            id,
            new EnrollmentVersion(checked((int)reader.GetInt64(0))),
            PostgresPersistence.Deserialize<EnrollmentDefinition>(reader.GetString(2)));
        var item = new TrackedExecutionUnit(
            ExecutionUnit.Hydrate(snapshot),
            reader.GetInt64(1),
            Added: false,
            Updated: false);
        tracked.Add(id, item);
        return item;
    }

    private async Task InsertAsync(ExecutionUnit unit, CancellationToken cancellationToken)
    {
        var snapshot = unit.ToSnapshot();
        await using (var command = new NpgsqlCommand(
            """
            insert into execution_units(
                id, enrollment_version, persistence_version, machine,
                current_enrollment, created_at, updated_at)
            values (
                @id, @enrollment_version, 1, @machine,
                @current_enrollment, @created_at, @updated_at)
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", unit.Id.Value);
            command.Parameters.AddWithValue("enrollment_version", (long)unit.Version.Value);
            command.Parameters.Add(PostgresPersistence.JsonParameter("machine", unit.CurrentEnrollment.Machine));
            command.Parameters.Add(PostgresPersistence.JsonParameter("current_enrollment", unit.CurrentEnrollment));
            command.Parameters.AddWithValue("created_at", DateTime.UtcNow);
            command.Parameters.AddWithValue("updated_at", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await ReplaceCapabilitiesAsync(unit, cancellationToken).ConfigureAwait(false);
        await InsertEnrollmentEventAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAsync(TrackedExecutionUnit trackedUnit, CancellationToken cancellationToken)
    {
        var unit = trackedUnit.Aggregate;
        await using (var command = new NpgsqlCommand(
            """
            update execution_units
            set enrollment_version = @enrollment_version,
                persistence_version = persistence_version + 1,
                machine = @machine,
                current_enrollment = @current_enrollment,
                updated_at = @updated_at
            where id = @id
              and persistence_version = @expected_version
            returning persistence_version
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("enrollment_version", (long)unit.Version.Value);
            command.Parameters.Add(PostgresPersistence.JsonParameter("machine", unit.CurrentEnrollment.Machine));
            command.Parameters.Add(PostgresPersistence.JsonParameter("current_enrollment", unit.CurrentEnrollment));
            command.Parameters.AddWithValue("updated_at", DateTime.UtcNow);
            command.Parameters.AddWithValue("id", unit.Id.Value);
            command.Parameters.AddWithValue("expected_version", trackedUnit.PersistenceVersion);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long)
            {
                throw new OptimisticConcurrencyException(
                    "execution_unit",
                    unit.Id.Value,
                    trackedUnit.PersistenceVersion);
            }
        }

        await using (var clear = new NpgsqlCommand(
            "delete from execution_unit_capabilities where execution_unit_id = @execution_unit_id",
            connection,
            transaction))
        {
            clear.Parameters.AddWithValue("execution_unit_id", unit.Id.Value);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await ReplaceCapabilitiesAsync(unit, cancellationToken).ConfigureAwait(false);
        await InsertEnrollmentEventAsync(unit.ToSnapshot(), cancellationToken).ConfigureAwait(false);
    }

    private async Task ReplaceCapabilitiesAsync(ExecutionUnit unit, CancellationToken cancellationToken)
    {
        foreach (var capability in unit.CurrentEnrollment.Capabilities)
        {
            await using var command = new NpgsqlCommand(
                """
                insert into execution_unit_capabilities(
                    execution_unit_id, capability_id, enrollment_version)
                values (@execution_unit_id, @capability_id, @enrollment_version)
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("execution_unit_id", unit.Id.Value);
            command.Parameters.AddWithValue("capability_id", capability.Id.Value);
            command.Parameters.AddWithValue("enrollment_version", (long)unit.Version.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InsertEnrollmentEventAsync(
        ExecutionUnitSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into enrollment_events(
                id, execution_unit_id, enrollment_version, occurred_at, snapshot)
            values (@id, @execution_unit_id, @enrollment_version, @occurred_at, @snapshot)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", EnrollmentEventId.New().Value);
        command.Parameters.AddWithValue("execution_unit_id", snapshot.Id.Value);
        command.Parameters.AddWithValue("enrollment_version", (long)snapshot.Version.Value);
        command.Parameters.AddWithValue("occurred_at", DateTime.UtcNow);
        command.Parameters.Add(PostgresPersistence.JsonParameter("snapshot", snapshot));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record TrackedExecutionUnit(
        ExecutionUnit Aggregate,
        long PersistenceVersion,
        bool Added,
        bool Updated);
}

internal sealed class PostgresPartnerResourceRepository(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    OperationUsageGuard guard) : IPartnerResourceRepository
{
    private readonly Dictionary<Guid, TrackedPartnerResource> tracked = [];

    public Task<PartnerResourceRequest?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        guard.RunAsync(async () => (await LoadAsync(id, cancellationToken).ConfigureAwait(false))?.Request);

    public void Add(PartnerResourceRequest request) => guard.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(request);
        if (tracked.ContainsKey(request.Id)) throw new InvalidOperationException("The partner request is already tracked.");
        tracked.Add(request.Id, new TrackedPartnerResource(request, 0, Added: true, Updated: false));
    });

    public void Update(PartnerResourceRequest request) => guard.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!tracked.TryGetValue(request.Id, out var current) || current.Added)
        {
            throw new InvalidOperationException("The partner request must be loaded before it can be updated.");
        }
        tracked[request.Id] = current with { Request = request, Updated = true };
    });

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var item in tracked.Values.Where(static item => item.Added))
        {
            await using var command = new NpgsqlCommand(
                """
                insert into partner_resource_requests(
                    id, partner_name, contact_email, origin, normalized_origin,
                    submitted_at, approved_at, revoked_at, version)
                values (
                    @id, @partner_name, @contact_email, @origin, @normalized_origin,
                    @submitted_at, @approved_at, @revoked_at, 1)
                """,
                connection,
                transaction);
            AddParameters(command, item.Request);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var item in tracked.Values.Where(static item => item.Updated))
        {
            await using var command = new NpgsqlCommand(
                """
                update partner_resource_requests
                set partner_name = @partner_name,
                    contact_email = @contact_email,
                    origin = @origin,
                    normalized_origin = @normalized_origin,
                    approved_at = @approved_at,
                    revoked_at = @revoked_at,
                    version = version + 1
                where id = @id and version = @expected_version
                returning version
                """,
                connection,
                transaction);
            AddParameters(command, item.Request);
            command.Parameters.AddWithValue("expected_version", item.Version);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long)
            {
                throw new OptimisticConcurrencyException(
                    "partner_resource_request",
                    item.Request.Id,
                    item.Version);
            }
        }
    }

    private async Task<TrackedPartnerResource?> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        if (tracked.TryGetValue(id, out var existing)) return existing;
        await using var command = new NpgsqlCommand(
            """
            select partner_name, contact_email, origin, submitted_at,
                   approved_at, revoked_at, version
            from partner_resource_requests
            where id = @id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var version = reader.GetInt64(6);
        var request = new PartnerResourceRequest(
            id,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            PostgresPersistence.Timestamp(reader, 3),
            PostgresPersistence.NullableTimestamp(reader, 4),
            PostgresPersistence.NullableTimestamp(reader, 5),
            version);
        var item = new TrackedPartnerResource(request, version, Added: false, Updated: false);
        tracked.Add(id, item);
        return item;
    }

    private static void AddParameters(NpgsqlCommand command, PartnerResourceRequest request)
    {
        command.Parameters.AddWithValue("id", request.Id);
        command.Parameters.AddWithValue("partner_name", request.PartnerName);
        command.Parameters.AddWithValue("contact_email", request.ContactEmail);
        command.Parameters.AddWithValue("origin", request.Origin);
        command.Parameters.AddWithValue("normalized_origin", PostgresPersistence.NormalizeOrigin(request.Origin));
        command.Parameters.AddWithValue("submitted_at", PostgresPersistence.Utc(request.SubmittedAt));
        PostgresPersistence.AddNullableTimestamp(command, "approved_at", request.ProcessedAt);
        PostgresPersistence.AddNullableTimestamp(command, "revoked_at", request.RevokedAt);
    }

    private sealed record TrackedPartnerResource(
        PartnerResourceRequest Request,
        long Version,
        bool Added,
        bool Updated);
}

internal sealed class PostgresResultUploadRepository(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    OperationUsageGuard guard) : IResultUploadRepository
{
    private readonly Dictionary<ResultUploadOperationId, TrackedUpload> tracked = [];

    public Task<ResultUploadOperation?> GetAsync(
        ResultUploadOperationId id,
        CancellationToken cancellationToken) =>
        guard.RunAsync(async () => (await LoadAsync(id, null, null, null, cancellationToken).ConfigureAwait(false))?.Operation);

    public Task<ResultUploadOperation?> GetByReceiptAsync(
        TaskId taskId,
        AttemptId attemptId,
        string receipt,
        CancellationToken cancellationToken) =>
        guard.RunAsync(async () => (await LoadAsync(null, taskId, attemptId, receipt, cancellationToken).ConfigureAwait(false))?.Operation);

    public Task<ResultUploadOperation?> GetUploadingAsync(
        TaskId taskId,
        AttemptId attemptId,
        ExecutionUnitId executionUnitId,
        string handleDigest,
        CancellationToken cancellationToken) =>
        guard.RunAsync(async () =>
        {
            await using var command = new NpgsqlCommand(
                """
                select id
                from result_upload_operations
                where task_id = @task_id
                  and attempt_id = @attempt_id
                  and execution_unit_id = @execution_unit_id
                  and handle_digest = @handle_digest
                  and state = 'uploading'
                order by created_at desc
                limit 1
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("task_id", taskId.Value);
            command.Parameters.AddWithValue("attempt_id", attemptId.Value);
            command.Parameters.AddWithValue("execution_unit_id", executionUnitId.Value);
            command.Parameters.Add("handle_digest", NpgsqlDbType.Char).Value = handleDigest;
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid id
                ? (await LoadAsync(
                    new ResultUploadOperationId(id),
                    null,
                    null,
                    null,
                    cancellationToken).ConfigureAwait(false))?.Operation
                : null;
        });

    public void Add(ResultUploadOperation operation) => guard.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArtifactStorageTargetIds.RequireAwsPrimary(
            operation.WriteStorageTargetId,
            nameof(operation.WriteStorageTargetId));
        if (tracked.ContainsKey(operation.Id)) throw new InvalidOperationException("The upload operation is already tracked.");
        tracked.Add(operation.Id, new TrackedUpload(operation, 0, Added: true, Updated: false));
    });

    public void Update(ResultUploadOperation operation) => guard.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArtifactStorageTargetIds.RequireAwsPrimary(
            operation.WriteStorageTargetId,
            nameof(operation.WriteStorageTargetId));
        if (!tracked.TryGetValue(operation.Id, out var current) || current.Added)
        {
            throw new InvalidOperationException("The upload operation must be loaded before it can be updated.");
        }
        if (!StringComparer.Ordinal.Equals(
            operation.WriteStorageTargetId,
            current.Operation.WriteStorageTargetId))
        {
            throw new InvalidOperationException("The upload operation write storage target is immutable.");
        }
        tracked[operation.Id] = current with { Operation = operation, Updated = true };
    });

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var item in tracked.Values.Where(static item => item.Added))
        {
            await using var command = new NpgsqlCommand(
                """
                insert into result_upload_operations(
                    id, task_id, attempt_id, execution_unit_id, handle_digest,
                    token_digest, receipt, state, expires_at, version, created_at,
                    uploaded_at, completed_at, write_storage_target_id)
                values (
                    @id, @task_id, @attempt_id, @execution_unit_id, @handle_digest,
                    @token_digest, @receipt, @state, @expires_at, 1, @created_at,
                    @uploaded_at, @completed_at, @write_storage_target_id)
                """,
                connection,
                transaction);
            AddParameters(command, item.Operation);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var item in tracked.Values.Where(static item => item.Updated))
        {
            await using var command = new NpgsqlCommand(
                """
                update result_upload_operations
                set receipt = @receipt,
                    state = @state,
                    expires_at = @expires_at,
                    uploaded_at = @uploaded_at,
                    completed_at = @completed_at,
                    version = version + 1
                where id = @id and version = @expected_version
                returning version
                """,
                connection,
                transaction);
            AddParameters(command, item.Operation);
            command.Parameters.AddWithValue("expected_version", item.Version);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long)
            {
                throw new OptimisticConcurrencyException(
                    "result_upload_operation",
                    item.Operation.Id.Value,
                    item.Version);
            }
        }
    }

    private async Task<TrackedUpload?> LoadAsync(
        ResultUploadOperationId? id,
        TaskId? taskId,
        AttemptId? attemptId,
        string? receipt,
        CancellationToken cancellationToken)
    {
        if (id is not null && tracked.TryGetValue(id.Value, out var existing)) return existing;
        await using var command = new NpgsqlCommand(
            """
            select id, task_id, attempt_id, execution_unit_id, handle_digest,
                   token_digest, receipt, state, expires_at, version, created_at,
                   uploaded_at, completed_at, write_storage_target_id
            from result_upload_operations
            where (@id is not null and id = @id)
               or (@id is null and task_id = @task_id and attempt_id = @attempt_id and receipt = @receipt)
            order by created_at desc
            limit 1
            """,
            connection,
            transaction);
        PostgresPersistence.AddNullableUuid(command, "id", id?.Value);
        PostgresPersistence.AddNullableUuid(command, "task_id", taskId?.Value);
        PostgresPersistence.AddNullableUuid(command, "attempt_id", attemptId?.Value);
        PostgresPersistence.AddNullableText(command, "receipt", receipt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var operation = new ResultUploadOperation(
            new ResultUploadOperationId(reader.GetGuid(0)),
            new TaskId(reader.GetGuid(1)),
            new AttemptId(reader.GetGuid(2)),
            new ExecutionUnitId(reader.GetGuid(3)),
            reader.GetString(4).Trim(),
            reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetFieldValue<ResultUploadState>(7),
            PostgresPersistence.NullableTimestamp(reader, 8),
            reader.GetInt64(9),
            PostgresPersistence.Timestamp(reader, 10),
            PostgresPersistence.NullableTimestamp(reader, 11),
            PostgresPersistence.NullableTimestamp(reader, 12))
        {
            WriteStorageTargetId = reader.GetString(13),
        };
        ArtifactStorageTargetIds.RequireAwsPrimary(
            operation.WriteStorageTargetId,
            nameof(operation.WriteStorageTargetId));
        var item = new TrackedUpload(operation, operation.Version, Added: false, Updated: false);
        tracked.TryAdd(operation.Id, item);
        return item;
    }

    private static void AddParameters(NpgsqlCommand command, ResultUploadOperation operation)
    {
        command.Parameters.AddWithValue("id", operation.Id.Value);
        command.Parameters.AddWithValue("task_id", operation.TaskId.Value);
        command.Parameters.AddWithValue("attempt_id", operation.AttemptId.Value);
        command.Parameters.AddWithValue("execution_unit_id", operation.ExecutionUnitId.Value);
        command.Parameters.Add("handle_digest", NpgsqlDbType.Char).Value = operation.HandleDigest;
        command.Parameters.Add("token_digest", NpgsqlDbType.Char).Value = (object?)operation.TokenDigest ?? DBNull.Value;
        PostgresPersistence.AddNullableText(command, "receipt", operation.Receipt);
        command.Parameters.AddWithValue("state", operation.State);
        PostgresPersistence.AddNullableTimestamp(command, "expires_at", operation.ExpiresAt);
        command.Parameters.AddWithValue("created_at", PostgresPersistence.Utc(operation.CreatedAt));
        PostgresPersistence.AddNullableTimestamp(command, "uploaded_at", operation.UploadedAt);
        PostgresPersistence.AddNullableTimestamp(command, "completed_at", operation.CompletedAt);
        command.Parameters.AddWithValue("write_storage_target_id", operation.WriteStorageTargetId);
    }

    private sealed record TrackedUpload(
        ResultUploadOperation Operation,
        long Version,
        bool Added,
        bool Updated);
}

internal sealed class PostgresArtifactRepository(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    OperationUsageGuard guard) : IArtifactRepository
{
    private readonly Dictionary<ArtifactId, TrackedArtifact> tracked = [];

    public Task<IReadOnlyList<ArtifactDescriptor>> GetForTaskAsync(
        TaskId taskId,
        CancellationToken cancellationToken) =>
        guard.RunAsync(async () =>
        {
            await using var command = new NpgsqlCommand(
                """
                select id, task_id, attempt_id, direction, role, s3_object_key,
                       content_type, length, sha256, state, created_at,
                       result_upload_operation_id
                from artifacts
                where task_id = @task_id
                order by direction, role
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("task_id", taskId.Value);
            var artifacts = new List<ArtifactDescriptor>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var artifact = Read(reader);
                    artifacts.Add(artifact);
                }
            }
            if (artifacts.Count == 0) return (IReadOnlyList<ArtifactDescriptor>)artifacts;

            var locations = await LoadLocationsAsync(
                artifacts.Select(static artifact => artifact.Id).ToArray(),
                cancellationToken).ConfigureAwait(false);
            var withLocations = artifacts
                .Select(artifact => tracked.TryGetValue(artifact.Id, out var existing)
                    ? existing.Artifact
                    : AttachValidatedLocation(
                        artifact,
                        locations.GetValueOrDefault(artifact.Id, [])))
                .ToArray();
            foreach (var artifact in withLocations)
            {
                if (!tracked.ContainsKey(artifact.Id))
                {
                    tracked.Add(artifact.Id, new TrackedArtifact(artifact, Added: false, Updated: false));
                }
            }
            return withLocations;
        });

    public void Add(ArtifactDescriptor artifact) => guard.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact = NormalizeLocation(artifact);
        ArtifactStorageTargetIds.RequireAwsPrimary(
            artifact.Locations![0].StorageTargetId,
            nameof(ArtifactLocation.StorageTargetId));
        if (tracked.ContainsKey(artifact.Id)) throw new InvalidOperationException("The artifact is already tracked.");
        tracked.Add(artifact.Id, new TrackedArtifact(artifact, Added: true, Updated: false));
    });

    public void Update(ArtifactDescriptor artifact) => guard.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!tracked.TryGetValue(artifact.Id, out var current) || current.Added)
        {
            throw new InvalidOperationException("The artifact must be loaded before it can be updated.");
        }
        artifact = NormalizeLocation(artifact, current.Artifact);
        tracked[artifact.Id] = current with { Artifact = artifact, Updated = true };
    });

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var item in tracked.Values.Where(static item => item.Added))
        {
            await using var command = new NpgsqlCommand(
                """
                insert into artifacts(
                    id, task_id, attempt_id, result_upload_operation_id,
                    direction, role, s3_object_key, content_type, length,
                    sha256, state, created_at)
                values (
                    @id, @task_id, @attempt_id, @result_upload_operation_id,
                    @direction, @role, @s3_object_key, @content_type, @length,
                    @sha256, @state, @created_at)
                """,
                connection,
                transaction);
            AddParameters(command, item.Artifact);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await InsertLocationsAsync(item.Artifact, cancellationToken).ConfigureAwait(false);
        }
        foreach (var item in tracked.Values.Where(static item => item.Updated))
        {
            await using var command = new NpgsqlCommand(
                """
                update artifacts
                set state = @state
                where id = @id
                """,
                connection,
                transaction);
            AddParameters(command, item.Artifact);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new OptimisticConcurrencyException("artifact", item.Artifact.Id.Value, 1);
            }
            await UpdateLocationAsync(item.Artifact, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<ArtifactId, IReadOnlyList<ArtifactLocation>>> LoadLocationsAsync(
        IReadOnlyList<ArtifactId> artifactIds,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select artifact_id, storage_target_id, object_key, provider_etag,
                   state, created_at, last_verified_at
            from artifact_locations
            where artifact_id = any(@artifact_ids)
            order by artifact_id, storage_target_id
            """,
            connection,
            transaction);
        command.Parameters.Add(
            "artifact_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = artifactIds
            .Select(static id => id.Value)
            .ToArray();
        var locations = new Dictionary<ArtifactId, List<ArtifactLocation>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var artifactId = new ArtifactId(reader.GetGuid(0));
            if (!locations.TryGetValue(artifactId, out var values))
            {
                values = [];
                locations.Add(artifactId, values);
            }
            values.Add(new ArtifactLocation(
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetFieldValue<ArtifactLocationState>(4),
                PostgresPersistence.Timestamp(reader, 5),
                PostgresPersistence.NullableTimestamp(reader, 6)));
        }
        return locations.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<ArtifactLocation>)pair.Value);
    }

    private async Task InsertLocationsAsync(
        ArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        foreach (var location in InitialLocations(artifact))
        {
            await using var command = new NpgsqlCommand(
                """
                insert into artifact_locations(
                    artifact_id, storage_target_id, object_key, provider_etag,
                    state, created_at, last_verified_at)
                values (
                    @artifact_id, @storage_target_id, @object_key, @provider_etag,
                    @state, @created_at, @last_verified_at)
                """,
                connection,
                transaction);
            AddLocationParameters(command, artifact.Id, location);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpdateLocationAsync(
        ArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update artifact_locations
            set state = @state
            where artifact_id = @artifact_id
              and storage_target_id = @storage_target_id
              and object_key = @object_key
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("artifact_id", artifact.Id.Value);
        var location = artifact.Locations![0];
        command.Parameters.AddWithValue("storage_target_id", location.StorageTargetId);
        command.Parameters.AddWithValue("object_key", location.ObjectKey);
        command.Parameters.AddWithValue("state", ToLocationState(artifact.State));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The artifact location is missing or no longer unique.");
        }
    }

    private static IReadOnlyList<ArtifactLocation> InitialLocations(ArtifactDescriptor artifact) =>
        artifact.Locations ?? throw new InvalidOperationException("Artifact writes require an explicit storage location.");

    private static ArtifactDescriptor AttachValidatedLocation(
        ArtifactDescriptor artifact,
        IReadOnlyList<ArtifactLocation> locations)
    {
        if (locations is not [var location])
        {
            throw new InvalidOperationException(
                "Every artifact must have exactly one explicit storage location.");
        }

        ValidateLocationIdentity(location);
        if (!StringComparer.Ordinal.Equals(location.ObjectKey, artifact.S3ObjectKey))
        {
            throw new InvalidOperationException(
                "The artifact location key does not match its compatibility key.");
        }

        if (location.State != ToLocationState(artifact.State))
        {
            throw new InvalidOperationException(
                "The artifact location state does not match its logical artifact state.");
        }

        return artifact with { Locations = [location] };
    }

    private static ArtifactDescriptor NormalizeLocation(
        ArtifactDescriptor artifact,
        ArtifactDescriptor? previous = null)
    {
        if (artifact.Locations is not [var location])
        {
            throw new InvalidOperationException("This release requires exactly one explicit artifact location.");
        }

        ValidateLocationIdentity(location);
        if (!StringComparer.Ordinal.Equals(location.ObjectKey, artifact.S3ObjectKey))
        {
            throw new InvalidOperationException("The artifact location key must match the compatibility key.");
        }

        if (previous?.Locations is [var prior] &&
            (!StringComparer.Ordinal.Equals(prior.StorageTargetId, location.StorageTargetId) ||
             !StringComparer.Ordinal.Equals(prior.ObjectKey, location.ObjectKey) ||
             !StringComparer.Ordinal.Equals(prior.ProviderETag, location.ProviderETag)))
        {
            throw new InvalidOperationException("Artifact location identity is immutable after persistence.");
        }

        return artifact with { Locations = [location with { State = ToLocationState(artifact.State) }] };
    }

    private static void ValidateLocationIdentity(ArtifactLocation location)
    {
        if (String.IsNullOrWhiteSpace(location.StorageTargetId))
        {
            throw new InvalidOperationException("Artifact locations require an explicit storage target ID.");
        }

        _ = new ObjectKey(location.ObjectKey);
    }

    private static ArtifactLocationState ToLocationState(ArtifactState state) => state switch
    {
        ArtifactState.Staged => ArtifactLocationState.Staged,
        ArtifactState.Available => ArtifactLocationState.Available,
        ArtifactState.Orphaned => ArtifactLocationState.Orphaned,
        ArtifactState.Deleted => ArtifactLocationState.Deleted,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The artifact state is unsupported."),
    };

    private static void AddLocationParameters(
        NpgsqlCommand command,
        ArtifactId artifactId,
        ArtifactLocation location)
    {
        command.Parameters.AddWithValue("artifact_id", artifactId.Value);
        command.Parameters.AddWithValue("storage_target_id", location.StorageTargetId);
        command.Parameters.AddWithValue("object_key", location.ObjectKey);
        PostgresPersistence.AddNullableText(command, "provider_etag", location.ProviderETag);
        command.Parameters.AddWithValue("state", location.State);
        command.Parameters.AddWithValue("created_at", PostgresPersistence.Utc(location.CreatedAt));
        PostgresPersistence.AddNullableTimestamp(command, "last_verified_at", location.LastVerifiedAt);
    }

    private static ArtifactDescriptor Read(NpgsqlDataReader reader) => new(
        new ArtifactId(reader.GetGuid(0)),
        new TaskId(reader.GetGuid(1)),
        reader.IsDBNull(2) ? null : new AttemptId(reader.GetGuid(2)),
        reader.GetFieldValue<ArtifactDirection>(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetInt64(7),
        reader.GetString(8).Trim(),
        reader.GetFieldValue<ArtifactState>(9),
        PostgresPersistence.Timestamp(reader, 10),
        reader.IsDBNull(11) ? null : new ResultUploadOperationId(reader.GetGuid(11)));

    private static void AddParameters(NpgsqlCommand command, ArtifactDescriptor artifact)
    {
        command.Parameters.AddWithValue("id", artifact.Id.Value);
        command.Parameters.AddWithValue("task_id", artifact.TaskId.Value);
        PostgresPersistence.AddNullableUuid(command, "attempt_id", artifact.AttemptId?.Value);
        PostgresPersistence.AddNullableUuid(
            command,
            "result_upload_operation_id",
            artifact.ResultUploadOperationId?.Value);
        command.Parameters.AddWithValue("direction", artifact.Direction);
        command.Parameters.AddWithValue("role", artifact.Role);
        command.Parameters.AddWithValue("s3_object_key", artifact.S3ObjectKey);
        command.Parameters.AddWithValue("content_type", artifact.ContentType);
        command.Parameters.AddWithValue("length", artifact.Length);
        command.Parameters.Add("sha256", NpgsqlDbType.Char).Value = artifact.Sha256.ToLowerInvariant();
        command.Parameters.AddWithValue("state", artifact.State);
        command.Parameters.AddWithValue("created_at", PostgresPersistence.Utc(artifact.CreatedAt));
    }

    private sealed record TrackedArtifact(ArtifactDescriptor Artifact, bool Added, bool Updated);
}
