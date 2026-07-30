using System.Data;
using MutualGPU.Application;
using MutualGPU.Domain;
using Npgsql;

namespace MutualGPU.Infrastructure;

public sealed class PostgresOperationUnitOfWork(
    NpgsqlDataSource dataSource,
    HandleCipher handles,
    MutualGpuObjectKeys objectKeys) : IOperationUnitOfWork
{
    private static readonly AsyncLocal<int> ExecutionDepth = new();

    public async Task<T> ExecuteAsync<T>(
        Func<IOperationContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (ExecutionDepth.Value != 0)
        {
            throw new InvalidOperationException("Nested operation-unit-of-work execution is not supported.");
        }

        ExecutionDepth.Value++;
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
            var context = new PostgresOperationContext(
                OperationId.New(),
                connection,
                transaction,
                handles,
                objectKeys);
            try
            {
                var result = await operation(context, cancellationToken).ConfigureAwait(false);
                context.BeginFlush();
                await context.FlushAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                context.Complete();
                return result;
            }
            catch
            {
                context.Complete();
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original operation failure. Disposing the transaction
                    // and connection below still releases every server-side resource.
                }
                throw;
            }
        }
        finally
        {
            ExecutionDepth.Value--;
        }
    }
}

internal sealed class PostgresOperationContext : IOperationContext
{
    private readonly OperationUsageGuard guard = new();
    private readonly List<OperationEvent> messages = [];
    private bool flushing;

    public PostgresOperationContext(
        OperationId operationId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HandleCipher handles,
        MutualGpuObjectKeys objectKeys)
    {
        OperationId = operationId;
        Tasks = new PostgresTaskRepository(connection, transaction, handles, objectKeys, guard);
        Capabilities = new PostgresCapabilityRepository(connection, transaction, guard);
        ExecutionUnits = new PostgresExecutionUnitRepository(connection, transaction, guard);
        PartnerResources = new PostgresPartnerResourceRepository(connection, transaction, guard);
        ResultUploads = new PostgresResultUploadRepository(connection, transaction, guard);
        Artifacts = new PostgresArtifactRepository(connection, transaction, guard);
        Connection = connection;
        Transaction = transaction;
    }

    public OperationId OperationId { get; }

    internal NpgsqlConnection Connection { get; }

    internal NpgsqlTransaction Transaction { get; }

    public ITaskRepository Tasks { get; }

    public IExecutionUnitRepository ExecutionUnits { get; }

    public ICapabilityRepository Capabilities { get; }

    public IPartnerResourceRepository PartnerResources { get; }

    public IResultUploadRepository ResultUploads { get; }

    public IArtifactRepository Artifacts { get; }

    public void Publish(OperationEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        guard.Run(() => messages.Add(message));
    }

    internal void BeginFlush() => flushing = true;

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!flushing) throw new InvalidOperationException("The operation has not entered its flush phase.");

        await ((PostgresCapabilityRepository)Capabilities).FlushAsync(cancellationToken).ConfigureAwait(false);
        await ((PostgresExecutionUnitRepository)ExecutionUnits).FlushAsync(cancellationToken).ConfigureAwait(false);
        await ((PostgresTaskRepository)Tasks).FlushAsync(cancellationToken).ConfigureAwait(false);
        await ((PostgresResultUploadRepository)ResultUploads).FlushAsync(cancellationToken).ConfigureAwait(false);
        await ((PostgresArtifactRepository)Artifacts).FlushAsync(cancellationToken).ConfigureAwait(false);
        await ((PostgresPartnerResourceRepository)PartnerResources).FlushAsync(cancellationToken).ConfigureAwait(false);
        await FlushOutboxAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void Complete() => guard.Complete();

    private async Task FlushOutboxAsync(CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            await using var command = new NpgsqlCommand(
                """
                insert into operation_outbox(
                    id, operation_id, kind, payload, occurred_at, available_at)
                values (@id, @operation_id, @kind, @payload, @occurred_at, @available_at)
                """,
                Connection,
                Transaction);
            command.Parameters.AddWithValue("id", message.Id.Value);
            command.Parameters.AddWithValue("operation_id", OperationId.Value);
            command.Parameters.AddWithValue("kind", message.Kind);
            command.Parameters.Add(PostgresPersistence.JsonParameter("payload", message.Payload));
            command.Parameters.AddWithValue("occurred_at", PostgresPersistence.Utc(message.OccurredAt));
            command.Parameters.AddWithValue("available_at", PostgresPersistence.Utc(message.AvailableAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
