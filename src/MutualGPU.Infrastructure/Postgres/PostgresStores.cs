using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using MutualGPU.Application;
using MutualGPU.Domain;
using Npgsql;
using NpgsqlTypes;

namespace MutualGPU.Infrastructure;

public sealed class PostgresTaskStore(
    NpgsqlDataSource dataSource,
    IOperationUnitOfWork operations,
    HandleCipher handles)
    : ITaskRepository, ITaskSummaryReader, IQueuedTaskReader, IAdminTaskReader, IStartupRecovery
{
    public Task<TaskRequest?> GetAsync(
        RequestorId requestorId,
        TaskId id,
        CancellationToken cancellationToken) =>
        operations.ExecuteAsync(
            (context, token) => context.Tasks.GetAsync(requestorId, id, token),
            cancellationToken);

    public Task<TaskRequest?> GetAsync(TaskId id, CancellationToken cancellationToken) =>
        operations.ExecuteAsync(
            (context, token) => context.Tasks.GetAsync(id, token),
            cancellationToken);

    public async Task<TaskRequest?> GetActiveByHandleAsync(
        ExecutionUnitId executionUnitId,
        string handle,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select task_id
            from task_attempts
            where execution_unit_id = @execution_unit_id
              and handle_digest = @handle_digest
              and state in ('accepted', 'disconnected')
            order by assigned_at desc
            limit 1
            """,
            connection);
        command.Parameters.AddWithValue("execution_unit_id", executionUnitId.Value);
        command.Parameters.Add("handle_digest", NpgsqlDbType.Char).Value = handles.Digest(handle);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid taskId
            ? await GetAsync(new TaskId(taskId), cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<IReadOnlyList<TaskRequest>> GetByAttemptStateBeforeAsync(
        AttemptState state,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        if (state is not (AttemptState.Assigned or AttemptState.Disconnected))
            throw new ArgumentOutOfRangeException(nameof(state));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select task_id
            from task_attempts
            where state = @state
              and case
                    when state = 'disconnected' then disconnected_at
                    else assigned_at
                  end <= @deadline
            order by assigned_at, task_id
            limit 200
            """,
            connection);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("deadline", PostgresPersistence.Utc(deadline));
        var ids = new List<TaskId>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(new TaskId(reader.GetGuid(0)));
        }
        var tasks = new List<TaskRequest>(ids.Count);
        foreach (var id in ids)
        {
            var task = await GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (task is not null) tasks.Add(task);
        }
        return tasks;
    }

    public Task<TaskRequest?> GetByIdempotencyKeyAsync(
        RequestorId requestorId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        operations.ExecuteAsync(
            (context, token) => context.Tasks.GetByIdempotencyKeyAsync(requestorId, idempotencyKey, token),
            cancellationToken);

    public Task<IReadOnlyList<TaskRequest>> GetByRequestorAsync(
        RequestorId requestorId,
        CancellationToken cancellationToken) =>
        operations.ExecuteAsync(
            (context, token) => context.Tasks.GetByRequestorAsync(requestorId, token),
            cancellationToken);

    public Task SaveAsync(TaskRequest task, CancellationToken cancellationToken) =>
        operations.ExecuteAsync(
            async (context, token) =>
            {
                var existing = await context.Tasks
                    .GetAsync(task.RequestorId, task.Id, token)
                    .ConfigureAwait(false);
                if (existing is null) context.Tasks.Add(task);
                else context.Tasks.Update(task);
                return true;
            },
            cancellationToken);

    public void Add(TaskRequest task) =>
        throw new NotSupportedException("Writes are available only inside an operation unit of work.");

    public void Update(TaskRequest task) =>
        throw new NotSupportedException("Writes are available only inside an operation unit of work.");

    public async Task<IReadOnlyList<TaskSummary>> ListSummariesAsync(
        RequestorId requestorId,
        CancellationToken cancellationToken)
    {
        var all = new List<TaskSummary>();
        string? cursor = null;
        do
        {
            var page = await ListSummariesPageAsync(
                requestorId,
                new PageRequest(200, cursor),
                cancellationToken).ConfigureAwait(false);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return all;
    }

    public async Task<PageResult<TaskSummary>> ListSummariesPageAsync(
        RequestorId requestorId,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        var cursor = PostgresPageCursor.Parse(page.Cursor);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select t.id,
                   t.capability_snapshot ->> 'name',
                   t.created_at,
                   t.compute_tier,
                   t.memory_gib,
                   t.status,
                   (select count(*)::integer from task_attempts a where a.task_id = t.id),
                   (
                       select a.failure_step
                       from task_attempts a
                       where a.task_id = t.id and a.failure_step is not null
                       order by a.assignment_number desc
                       limit 1
                   ),
                   (
                       select a.failure_reason
                       from task_attempts a
                       where a.task_id = t.id and a.failure_reason is not null
                       order by a.assignment_number desc
                       limit 1
                   )
            from tasks t
            where t.requestor_id = @requestor_id
              and (
                    @cursor_created_at is null
                    or (t.created_at, t.id) < (@cursor_created_at, @cursor_id)
                  )
            order by t.created_at desc, t.id desc
            limit @limit
            """,
            connection);
        command.Parameters.AddWithValue("requestor_id", requestorId.Value);
        command.Parameters.Add("cursor_created_at", NpgsqlDbType.TimestampTz).Value =
            cursor is null ? DBNull.Value : PostgresPersistence.Utc(cursor.Value.CreatedAt);
        command.Parameters.Add("cursor_id", NpgsqlDbType.Uuid).Value =
            cursor is null ? DBNull.Value : cursor.Value.Id;
        command.Parameters.AddWithValue("limit", page.BoundedLimit + 1);
        var summaries = new List<TaskSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            summaries.Add(new TaskSummary(
                new TaskId(reader.GetGuid(0)),
                reader.GetString(1),
                PostgresPersistence.Timestamp(reader, 2),
                new MachineSpecifications((ResourceTier)reader.GetInt16(3), reader.GetInt32(4)),
                reader.GetFieldValue<MutualGPU.Domain.TaskStatus>(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        var hasMore = summaries.Count > page.BoundedLimit;
        if (hasMore) summaries.RemoveAt(summaries.Count - 1);
        var last = summaries.LastOrDefault();
        return new PageResult<TaskSummary>(
            summaries,
            hasMore && last is not null
                ? PostgresPageCursor.Create(last.CreatedAt, last.TaskId.Value)
                : null);
    }

    public async Task<IReadOnlyList<TaskRequest>> GetQueuedAsync(CancellationToken cancellationToken)
    {
        var ids = await ReadTaskIdsAsync(
            """
            select id
            from tasks
            where status = 'queued'
            order by created_at, id
            """,
            cancellationToken).ConfigureAwait(false);
        var queued = new List<TaskRequest>(ids.Count);
        foreach (var id in ids)
        {
            var task = await GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (task?.Status is MutualGPU.Domain.TaskStatus.Queued) queued.Add(task);
        }
        return queued;
    }

    public async Task<IReadOnlyList<TaskRequest>> GetAllAsync(CancellationToken cancellationToken)
    {
        var all = new List<TaskRequest>();
        string? cursor = null;
        do
        {
            var page = await GetPageAsync(new PageRequest(200, cursor), cancellationToken).ConfigureAwait(false);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return all;
    }

    public async Task<PageResult<TaskRequest>> GetPageAsync(PageRequest page, CancellationToken cancellationToken)
    {
        var cursor = PostgresPageCursor.Parse(page.Cursor);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select id, created_at
            from tasks
            where @cursor_created_at is null
               or (created_at, id) < (@cursor_created_at, @cursor_id)
            order by created_at desc, id desc
            limit @limit
            """,
            connection);
        command.Parameters.Add("cursor_created_at", NpgsqlDbType.TimestampTz).Value =
            cursor is null ? DBNull.Value : PostgresPersistence.Utc(cursor.Value.CreatedAt);
        command.Parameters.Add("cursor_id", NpgsqlDbType.Uuid).Value =
            cursor is null ? DBNull.Value : cursor.Value.Id;
        command.Parameters.AddWithValue("limit", page.BoundedLimit + 1);
        var rows = new List<(TaskId Id, DateTimeOffset CreatedAt)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((new TaskId(reader.GetGuid(0)), PostgresPersistence.Timestamp(reader, 1)));
            }
        }
        var hasMore = rows.Count > page.BoundedLimit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var tasks = new List<TaskRequest>(rows.Count);
        foreach (var row in rows)
        {
            var task = await GetAsync(row.Id, cancellationToken).ConfigureAwait(false);
            if (task is not null) tasks.Add(task);
        }
        var last = rows.LastOrDefault();
        return new PageResult<TaskRequest>(
            tasks,
            hasMore && rows.Count > 0
                ? PostgresPageCursor.Create(last.CreatedAt, last.Id.Value)
                : null);
    }

    public async Task<int> RecoverAsync(
        DateTimeOffset processStartedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select distinct a.task_id
            from task_attempts a
            join tasks t on t.id = a.task_id
            where (
                    (a.state = 'assigned' and a.assigned_at <= @assigned_deadline)
                    or
                    (a.state = 'disconnected' and a.disconnected_at <= @disconnected_deadline)
                  )
              and t.status in ('assigned', 'running')
            order by a.task_id
            """,
            connection);
        command.Parameters.AddWithValue(
            "assigned_deadline",
            PostgresPersistence.Utc(processStartedAt.Subtract(TimeSpan.FromSeconds(30))));
        command.Parameters.AddWithValue(
            "disconnected_deadline",
            PostgresPersistence.Utc(processStartedAt.Subtract(TimeSpan.FromSeconds(155))));
        var ids = new List<TaskId>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(new TaskId(reader.GetGuid(0)));
            }
        }

        var recovered = 0;
        foreach (var id in ids)
        {
            try
            {
                recovered += await operations.ExecuteAsync(
                    async (context, token) =>
                    {
                        var task = await context.Tasks.GetAsync(id, token).ConfigureAwait(false);
                        var active = task?.Attempts.LastOrDefault(attempt =>
                            attempt.State is AttemptState.Assigned or AttemptState.Disconnected);
                        if (task is null || active is null) return 0;
                        var disconnected = active.State is AttemptState.Disconnected;
                        task.Requeue(
                            active.Id,
                            active.Handle,
                            AttemptState.Revoked,
                            disconnected ? "disconnect_recovery_expired" : "acknowledgement_timeout",
                            disconnected
                                ? "The provider disconnected and did not reconnect before the recovery window expired."
                                : "The provider did not accept the task before the acknowledgement deadline.");
                        context.Tasks.Update(task);
                        var now = DateTimeOffset.UtcNow;
                        context.Publish(OperationEvent.TaskChanged(task.RequestorId, now));
                        context.Publish(OperationEvent.Scheduler(now));
                        return 1;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OptimisticConcurrencyException)
            {
                // A newer command already recovered or advanced this task.
            }
        }
        return recovered;
    }

    private async Task<IReadOnlyList<TaskId>> ReadTaskIdsAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        var ids = new List<TaskId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(new TaskId(reader.GetGuid(0)));
        }
        return ids;
    }
}

internal static class PostgresPageCursor
{
    internal static string Create(DateTimeOffset createdAt, Guid id)
    {
        var value = $"{createdAt.UtcTicks}:{id:N}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static (DateTimeOffset CreatedAt, Guid Id)? Parse(string? cursor)
    {
        if (String.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var separator = value.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 ||
                !Int64.TryParse(value.AsSpan(0, separator), out var ticks) ||
                !Guid.TryParseExact(value[(separator + 1)..], "N", out var id))
            {
                throw new FormatException();
            }
            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
        {
            throw new ArgumentException("The pagination cursor is invalid.", nameof(cursor));
        }
    }
}

public sealed class PostgresExecutionUnitStore(IOperationUnitOfWork operations)
    : IExecutionUnitRepository, ICapabilityReader, IEnrollmentStartupRecovery
{
    public Task<ExecutionUnit?> GetAsync(ExecutionUnitId id, CancellationToken cancellationToken) =>
        operations.ExecuteAsync(
            (context, token) => context.ExecutionUnits.GetAsync(id, token),
            cancellationToken);

    public Task<IReadOnlyList<CapabilityDefinition>> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        operations.ExecuteAsync(
            (context, token) => context.Capabilities.GetAllAsync(token),
            cancellationToken);

    Task<CapabilityDefinition?> ICapabilityReader.GetAsync(
        CapabilityId capabilityId,
        CancellationToken cancellationToken) =>
        operations.ExecuteAsync(
            (context, token) => context.Capabilities.GetAsync(capabilityId, token),
            cancellationToken);

    public Task SaveAsync(ExecutionUnit executionUnit, CancellationToken cancellationToken) =>
        operations.ExecuteAsync(
            async (context, token) =>
            {
                var current = await context.ExecutionUnits
                    .GetAsync(executionUnit.Id, token)
                    .ConfigureAwait(false);
                if (current is null)
                {
                    foreach (var capability in executionUnit.CurrentEnrollment.Capabilities)
                    {
                        if (await context.Capabilities.GetAsync(capability.Id, token).ConfigureAwait(false) is null)
                        {
                            context.Capabilities.Add(capability);
                        }
                    }
                    context.ExecutionUnits.Add(executionUnit);
                }
                else
                {
                    context.ExecutionUnits.Update(executionUnit);
                }
                return true;
            },
            cancellationToken);

    public void Add(ExecutionUnit executionUnit) =>
        throw new NotSupportedException("Writes are available only inside an operation unit of work.");

    public void Update(ExecutionUnit executionUnit) =>
        throw new NotSupportedException("Writes are available only inside an operation unit of work.");

    public Task<int> RecoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult(0);
}

public sealed class PostgresProviderCredentialRegistry(
    NpgsqlDataSource dataSource,
    MutualGpuObjectKeys objectKeys,
    TimeProvider timeProvider)
    : IExecutionUnitKeyRegistry
{
    public async Task ProvisionAsync(
        ExecutionUnitId executionUnitId,
        string presharedKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presharedKey);
        var digest = objectKeys.ProviderDigest(presharedKey);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            insert into provider_credentials(
                digest, execution_unit_id, created_at, revoked_at, version)
            values (@digest, @execution_unit_id, @created_at, null, 1)
            on conflict (digest) do update
            set digest = excluded.digest
            where provider_credentials.execution_unit_id = excluded.execution_unit_id
            """,
            connection);
        command.Parameters.Add("digest", NpgsqlDbType.Char).Value = digest;
        command.Parameters.AddWithValue("execution_unit_id", executionUnitId.Value);
        command.Parameters.AddWithValue("created_at", PostgresPersistence.Utc(timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionUnitId?> AuthenticateAsync(
        string? presharedKey,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(presharedKey)) return null;
        var digest = objectKeys.ProviderDigest(presharedKey);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select execution_unit_id
            from provider_credentials
            where digest = @digest and revoked_at is null
            """,
            connection);
        command.Parameters.Add("digest", NpgsqlDbType.Char).Value = digest;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid value
            ? new ExecutionUnitId(value)
            : null;
    }

    public async Task<string?> GetProviderKeyDigestAsync(
        ExecutionUnitId executionUnitId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select digest
            from provider_credentials
            where execution_unit_id = @execution_unit_id and revoked_at is null
            """,
            connection);
        command.Parameters.AddWithValue("execution_unit_id", executionUnitId.Value);
        return (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string)?.Trim();
    }

    public async IAsyncEnumerable<ExecutionUnitId> ListExecutionUnitIdsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select execution_unit_id
            from provider_credentials
            where revoked_at is null
            order by execution_unit_id
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new ExecutionUnitId(reader.GetGuid(0));
        }
    }
}

public sealed class PostgresPartnerResourceRegistry(
    NpgsqlDataSource dataSource,
    IOperationUnitOfWork operations,
    TimeProvider timeProvider)
    : IPartnerResourceRegistry
{
    private readonly ConcurrentDictionary<string, byte> approvedOrigins = new(StringComparer.Ordinal);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var approved = await ListApprovedAsync(cancellationToken).ConfigureAwait(false);
        approvedOrigins.Clear();
        foreach (var request in approved)
        {
            approvedOrigins.TryAdd(PostgresPersistence.NormalizeOrigin(request.Origin), 0);
        }
    }

    public Task<PartnerResourceRequest> SubmitAsync(
        PartnerResourceSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var request = new PartnerResourceRequest(
            Guid.CreateVersion7(),
            submission.PartnerName,
            submission.ContactEmail,
            submission.Origin,
            timeProvider.GetUtcNow());
        return operations.ExecuteAsync(
            (context, _) =>
            {
                context.PartnerResources.Add(request);
                return Task.FromResult(request);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<PartnerResourceRequest>> ListPendingAsync(CancellationToken cancellationToken) =>
        ListAsync(
            "where approved_at is null and revoked_at is null order by submitted_at, id",
            cancellationToken);

    public Task<IReadOnlyList<PartnerResourceRequest>> ListApprovedAsync(CancellationToken cancellationToken) =>
        ListAsync(
            "where approved_at is not null and revoked_at is null order by approved_at desc, id desc",
            cancellationToken);

    public Task<PageResult<PartnerResourceRequest>> ListPendingPageAsync(
        PageRequest page,
        CancellationToken cancellationToken) =>
        ListPageAsync(
            "approved_at is null and revoked_at is null",
            "submitted_at",
            descending: false,
            page,
            cancellationToken);

    public Task<PageResult<PartnerResourceRequest>> ListApprovedPageAsync(
        PageRequest page,
        CancellationToken cancellationToken) =>
        ListPageAsync(
            "approved_at is not null and revoked_at is null",
            "approved_at",
            descending: true,
            page,
            cancellationToken);

    public async Task<PartnerResourceRequest?> ApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) return null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var request = await operations.ExecuteAsync(
                    async (context, token) =>
                    {
                        var current = await context.PartnerResources.GetAsync(id, token).ConfigureAwait(false);
                        if (current is null || current.RevokedAt is not null) return current;
                        if (current.ProcessedAt is null)
                        {
                            current = current with
                            {
                                ProcessedAt = timeProvider.GetUtcNow(),
                                Version = current.Version + 1,
                            };
                            context.PartnerResources.Update(current);
                            context.Publish(OperationEvent.PartnerOriginsChanged(timeProvider.GetUtcNow()));
                        }
                        return current;
                    },
                    cancellationToken).ConfigureAwait(false);
                if (request is { ProcessedAt: not null, RevokedAt: null })
                {
                    approvedOrigins.TryAdd(PostgresPersistence.NormalizeOrigin(request.Origin), 0);
                }
                return request;
            }
            catch (OptimisticConcurrencyException) when (attempt == 0)
            {
            }
        }
        return null;
    }

    public async Task<PartnerResourceRequest?> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) return null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var request = await operations.ExecuteAsync(
                    async (context, token) =>
                    {
                        var current = await context.PartnerResources.GetAsync(id, token).ConfigureAwait(false);
                        if (current is not { ProcessedAt: not null, RevokedAt: null }) return current;
                        current = current with
                        {
                            RevokedAt = timeProvider.GetUtcNow(),
                            Version = current.Version + 1,
                        };
                        context.PartnerResources.Update(current);
                        context.Publish(OperationEvent.PartnerOriginsChanged(timeProvider.GetUtcNow()));
                        return current;
                    },
                    cancellationToken).ConfigureAwait(false);
                await InitializeAsync(cancellationToken).ConfigureAwait(false);
                return request;
            }
            catch (OptimisticConcurrencyException) when (attempt == 0)
            {
            }
        }
        return null;
    }

    public bool IsApprovedOrigin(string? origin) =>
        !String.IsNullOrWhiteSpace(origin) &&
        approvedOrigins.ContainsKey(PostgresPersistence.NormalizeOrigin(origin));

    private async Task<IReadOnlyList<PartnerResourceRequest>> ListAsync(
        string whereAndOrder,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
             select id, partner_name, contact_email, origin, submitted_at,
                    approved_at, revoked_at, version
             from partner_resource_requests
             {whereAndOrder}
             """,
            connection);
        var requests = new List<PartnerResourceRequest>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            requests.Add(new PartnerResourceRequest(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                PostgresPersistence.Timestamp(reader, 4),
                PostgresPersistence.NullableTimestamp(reader, 5),
                PostgresPersistence.NullableTimestamp(reader, 6),
                reader.GetInt64(7)));
        }
        return requests;
    }

    private async Task<PageResult<PartnerResourceRequest>> ListPageAsync(
        string predicate,
        string timestampColumn,
        bool descending,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        var cursor = PostgresPageCursor.Parse(page.Cursor);
        var comparison = descending ? "<" : ">";
        var direction = descending ? "desc" : "asc";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
             select id, partner_name, contact_email, origin, submitted_at,
                    approved_at, revoked_at, version, {timestampColumn}
             from partner_resource_requests
             where {predicate}
               and (
                    @cursor_at is null
                    or ({timestampColumn}, id) {comparison} (@cursor_at, @cursor_id)
                   )
             order by {timestampColumn} {direction}, id {direction}
             limit @limit
             """,
            connection);
        command.Parameters.Add("cursor_at", NpgsqlDbType.TimestampTz).Value =
            cursor is null ? DBNull.Value : PostgresPersistence.Utc(cursor.Value.CreatedAt);
        command.Parameters.Add("cursor_id", NpgsqlDbType.Uuid).Value =
            cursor is null ? DBNull.Value : cursor.Value.Id;
        command.Parameters.AddWithValue("limit", page.BoundedLimit + 1);
        var rows = new List<(PartnerResourceRequest Request, DateTimeOffset CursorAt)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((
                new PartnerResourceRequest(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    PostgresPersistence.Timestamp(reader, 4),
                    PostgresPersistence.NullableTimestamp(reader, 5),
                    PostgresPersistence.NullableTimestamp(reader, 6),
                    reader.GetInt64(7)),
                PostgresPersistence.Timestamp(reader, 8)));
        }
        var hasMore = rows.Count > page.BoundedLimit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var last = rows.LastOrDefault();
        return new PageResult<PartnerResourceRequest>(
            rows.Select(static row => row.Request).ToArray(),
            hasMore && rows.Count > 0
                ? PostgresPageCursor.Create(last.CursorAt, last.Request.Id)
                : null);
    }
}
