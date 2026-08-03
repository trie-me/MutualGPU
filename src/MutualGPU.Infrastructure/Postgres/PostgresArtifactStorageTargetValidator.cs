using MutualGPU.Application;
using Npgsql;

namespace MutualGPU.Infrastructure;

/// <summary>
/// Fails startup before serving downloads when durable, available artifact rows
/// refer to a target that is no longer configured. This only inspects target
/// identifiers; it never constructs or contacts the corresponding provider.
/// </summary>
public sealed class PostgresArtifactStorageTargetValidator(
    NpgsqlDataSource dataSource,
    IObjectStoreRegistry stores)
{
    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var targetIds = new List<string>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select distinct storage_target_id
            from artifact_locations
            where state = 'available'
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            targetIds.Add(reader.GetString(0));
        }

        var missing = targetIds.Where(id => !stores.IsConfigured(id)).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidOperationException(
                $"Available artifacts reference unconfigured object-storage target IDs: {String.Join(", ", missing)}.");
        }
    }
}
