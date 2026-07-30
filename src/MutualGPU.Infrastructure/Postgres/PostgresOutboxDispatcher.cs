using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MutualGPU.Application;
using MutualGPU.Domain;
using Npgsql;

namespace MutualGPU.Infrastructure;

public sealed class PostgresOutboxDispatcher(
    NpgsqlDataSource dataSource,
    IApplicationEventSink events,
    IPartnerResourceRegistry partnerResources,
    ITaskRepository tasks,
    IProviderAssignments assignments,
    TimeProvider timeProvider,
    ILogger<PostgresOutboxDispatcher> logger) : BackgroundService
{
    private readonly string dispatcherId = Guid.CreateVersion7().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await ClaimAsync(stoppingToken).ConfigureAwait(false);
                if (messages.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var message in messages)
                {
                    await DispatchAsync(message, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The PostgreSQL outbox dispatch loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var messages = new List<OutboxMessage>();
        await using (var command = new NpgsqlCommand(
            """
            with candidates as (
                select id
                from operation_outbox
                where processed_at is null
                  and available_at <= now()
                  and (locked_until is null or locked_until < now())
                order by available_at, occurred_at, id
                for update skip locked
                limit 32
            )
            update operation_outbox o
            set locked_by = @locked_by,
                locked_until = now() + interval '30 seconds',
                attempt_count = attempt_count + 1
            from candidates c
            where o.id = c.id
            returning o.id, o.kind, o.payload
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("locked_by", dispatcherId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                messages.Add(new OutboxMessage(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    JsonDocument.Parse(reader.GetString(2))));
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return messages;
    }

    private async Task DispatchAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        try
        {
            switch (message.Kind)
            {
                case "scheduler.trigger":
                    events.TriggerScheduler();
                    break;
                case "task.changed":
                    if (message.Payload.RootElement.TryGetProperty("requestorId", out var requestor) &&
                        requestor.TryGetGuid(out var requestorId))
                    {
                        events.TaskChanged(new RequestorId(requestorId));
                    }
                    break;
                case "partner-origins.changed":
                    await partnerResources.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "assignment.ready":
                    await DeliverAssignmentAsync(message.Payload.RootElement, cancellationToken).ConfigureAwait(false);
                    break;
            }
            await MarkProcessedAsync(message.Id, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Outbox message {OutboxMessageId} could not be dispatched.", message.Id);
            await MarkProcessedAsync(message.Id, "dispatch_failed", cancellationToken, processed: false).ConfigureAwait(false);
        }
        finally
        {
            message.Payload.Dispose();
        }
    }

    private async Task DeliverAssignmentAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("taskId", out var taskValue) ||
            !taskValue.TryGetGuid(out var taskId) ||
            !payload.TryGetProperty("attemptId", out var attemptValue) ||
            !attemptValue.TryGetGuid(out var attemptId) ||
            !payload.TryGetProperty("executionUnitId", out var unitValue) ||
            !unitValue.TryGetGuid(out var unitId))
        {
            throw new InvalidOperationException("The assignment outbox payload is malformed.");
        }

        var task = await tasks.GetAsync(new TaskId(taskId), cancellationToken).ConfigureAwait(false);
        var attempt = task?.Attempts.SingleOrDefault(candidate =>
            candidate.Id == new AttemptId(attemptId) &&
            candidate.ExecutionUnitId == new ExecutionUnitId(unitId) &&
            candidate.State is AttemptState.Assigned);
        if (task is null || attempt is null) return;
        if (assignments.TryGet(attempt.ExecutionUnitId, task.Id, attempt.Id, attempt.Handle, out _)) return;

        assignments.Track(attempt.ExecutionUnitId, task, attempt);
        var input = task.Parameters.Image is { } image
            ? new ProviderInputAssignment(
                task.RequestorId,
                image,
                task.Parameters.ImageExtension ?? "bin",
                task.Parameters.ImageContentType ?? "application/octet-stream",
                task.Parameters.ImageLength ?? 0,
                task.Parameters.ImageSha256 ?? String.Empty)
            : null;
        if (!assignments.TryDeliver(
            attempt.ExecutionUnitId,
            new ProviderAssignment(
                task.Id,
                attempt.Id,
                attempt.Handle,
                task.Parameters.Scalars,
                input)))
        {
            assignments.Remove(attempt.ExecutionUnitId, task.Id, attempt.Id);
            throw new InvalidOperationException("The assigned provider is not currently connected.");
        }
    }

    private async Task MarkProcessedAsync(
        Guid id,
        string? errorCode,
        CancellationToken cancellationToken,
        bool processed = true)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            update operation_outbox
            set processed_at = case when @processed then now() else null end,
                last_error_code = @last_error_code,
                available_at = case when @processed then available_at else now() + interval '5 seconds' end,
                locked_by = null,
                locked_until = null
            where id = @id and locked_by = @locked_by
            """,
            connection);
        command.Parameters.AddWithValue("processed", processed);
        PostgresPersistence.AddNullableText(command, "last_error_code", errorCode);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("locked_by", dispatcherId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record OutboxMessage(Guid Id, string Kind, JsonDocument Payload);
}
