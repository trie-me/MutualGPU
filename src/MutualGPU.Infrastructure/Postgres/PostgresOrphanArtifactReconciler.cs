using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MutualGPU.Application;
using MutualGPU.Domain;
using Npgsql;

namespace MutualGPU.Infrastructure;

/// <summary>
/// Destructive artifact reconciliation is intentionally disabled for the first
/// PostgreSQL release. Enabling it is a separate, reviewed retention action.
/// </summary>
public sealed class OrphanArtifactReconciliationOptions
{
    public bool Enabled { get; init; }

    /// <summary>
    /// When enabled, inspect bounded candidate counts without changing upload
    /// state or deleting S3 objects. A separately reviewed retention action
    /// must set this to false before destructive reconciliation can run.
    /// </summary>
    public bool ReportOnly { get; init; } = true;
}

/// <summary>
/// Expires abandoned upload authorizations and removes only old objects from the
/// configured AWS storage target that have no committed PostgreSQL location.
/// Object listing is used solely for bounded garbage collection, never to answer
/// an application query.
/// </summary>
public sealed class PostgresOrphanArtifactReconciler(
    NpgsqlDataSource dataSource,
    IObjectStore objectStore,
    MutualGpuObjectKeys objectKeys,
    TimeProvider timeProvider,
    OrphanArtifactReconciliationOptions options,
    ILogger<PostgresOrphanArtifactReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan SafetyAge = TimeSpan.FromMinutes(10);
    private const int MaximumOperationsPerPass = 25;
    private const int MaximumObjectsPerOperation = 16;
    private const int MaximumInputObjectsExaminedPerPass = 1_000;
    private const int MaximumInputObjectsDeletedPerPass = 25;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The orphan output-artifact reconciliation pass failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("PostgreSQL orphan-artifact reconciliation is disabled.");
            return;
        }

        if (options.ReportOnly)
        {
            await ReportAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await ExpireAsync(cancellationToken).ConfigureAwait(false);
        await CollectAsync(cancellationToken).ConfigureAwait(false);
        await CollectUncommittedInputsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReportAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select
                count(*) filter (where state in ('authorized', 'uploading') and expires_at < now()),
                count(*) filter (where state = 'expired' and expires_at < @safe_before)
            from result_upload_operations
            """,
            connection);
        command.Parameters.AddWithValue(
            "safe_before",
            PostgresPersistence.Utc(timeProvider.GetUtcNow().Subtract(SafetyAge)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "PostgreSQL orphan-artifact reconciliation report only: {ExpiredAuthorizations} expired authorizations and {ExpiredUploads} expired uploads require review; no state or object was changed.",
            reader.GetInt64(0),
            reader.GetInt64(1));
    }

    private async Task ExpireAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            update result_upload_operations
            set state = 'expired',
                version = version + 1
            where state in ('authorized', 'uploading')
              and expires_at < now()
            """,
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = new NpgsqlCommand(
            """
            select u.id, u.task_id, u.attempt_id, t.requestor_id,
                   u.write_storage_target_id
            from result_upload_operations u
            join tasks t on t.id = u.task_id
            where u.state = 'expired'
              and u.write_storage_target_id = @storage_target_id
              and u.expires_at < @safe_before
              and not exists (
                    select 1
                    from artifacts a
                    where a.result_upload_operation_id = u.id
                  )
            order by u.expires_at, u.id
            limit @limit
            """,
            connection))
        {
            command.Parameters.AddWithValue(
                "safe_before",
                PostgresPersistence.Utc(timeProvider.GetUtcNow().Subtract(SafetyAge)));
            command.Parameters.AddWithValue("storage_target_id", ArtifactStorageTargetIds.AwsPrimary);
            command.Parameters.AddWithValue("limit", MaximumOperationsPerPass);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(new Candidate(
                    new ResultUploadOperationId(reader.GetGuid(0)),
                    new TaskId(reader.GetGuid(1)),
                    new AttemptId(reader.GetGuid(2)),
                    new RequestorId(reader.GetGuid(3)),
                    reader.GetString(4)));
            }
        }

        foreach (var candidate in candidates)
        {
            var deleted = 0;
            await foreach (var entry in objectStore.ListAsync(
                objectKeys.ResultArtifacts(
                    candidate.RequestorId,
                    candidate.TaskId,
                    candidate.AttemptId),
                cancellationToken).ConfigureAwait(false))
            {
                if (entry.LastModified > timeProvider.GetUtcNow().Subtract(SafetyAge)) continue;
                if (await IsReferencedAsync(entry.Key, candidate.StorageTargetId, cancellationToken).ConfigureAwait(false)) continue;
                await objectStore.DeleteAsync(entry.Key, cancellationToken).ConfigureAwait(false);
                if (++deleted >= MaximumObjectsPerOperation) break;
            }

            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var complete = new NpgsqlCommand(
                """
                update result_upload_operations
                set state = 'failed',
                    version = version + 1
                where id = @id
                  and state = 'expired'
                  and not exists (
                        select 1
                        from artifacts a
                        where a.result_upload_operation_id = result_upload_operations.id
                      )
                """,
                connection);
            complete.Parameters.AddWithValue("id", candidate.Id.Value);
            await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CollectUncommittedInputsAsync(CancellationToken cancellationToken)
    {
        var examined = 0;
        var deleted = 0;
        await foreach (var entry in objectStore.ListAsync(
            new ObjectPrefix("mutualgpu/v3/requestors"),
            cancellationToken).ConfigureAwait(false))
        {
            if (++examined > MaximumInputObjectsExaminedPerPass ||
                deleted >= MaximumInputObjectsDeletedPerPass)
            {
                break;
            }
            if (!entry.Key.Value.Contains("/inputs/", StringComparison.Ordinal) ||
                entry.LastModified > timeProvider.GetUtcNow().Subtract(SafetyAge))
            {
                continue;
            }

            if (await IsReferencedAsync(entry.Key, ArtifactStorageTargetIds.AwsPrimary, cancellationToken).ConfigureAwait(false)) continue;

            await objectStore.DeleteAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            deleted++;
        }
    }

    private async Task<bool> IsReferencedAsync(
        ObjectKey key,
        string storageTargetId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select exists(
                select 1
                from artifact_locations
                where storage_target_id = @storage_target_id
                  and object_key = @object_key
                  and state in ('staged', 'available')
            )
            """,
            connection);
        command.Parameters.AddWithValue("storage_target_id", storageTargetId);
        command.Parameters.AddWithValue("object_key", key.Value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    private sealed record Candidate(
        ResultUploadOperationId Id,
        TaskId TaskId,
        AttemptId AttemptId,
        RequestorId RequestorId,
        string StorageTargetId);
}
