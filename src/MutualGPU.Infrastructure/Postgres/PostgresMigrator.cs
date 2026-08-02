using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace MutualGPU.Infrastructure;

public sealed class PostgresMigrator(NpgsqlDataSource dataSource)
{
    private const long MigrationLockId = 0x4d_47_50_55;
    private const string ResourcePrefix = "MutualGPU.Infrastructure.Postgres.Migrations.";

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var claim = new NpgsqlCommand("select pg_advisory_lock(@lock_id)", connection))
        {
            claim.Parameters.AddWithValue("lock_id", MigrationLockId);
            await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await EnsureLedgerAsync(connection, cancellationToken).ConfigureAwait(false);
            var migrations = LoadMigrations();
            var ledger = await ReadLedgerAsync(connection, cancellationToken).ConfigureAwait(false);
            if (!LedgerContainsOnlyExpectedMigrations(ledger, migrations))
            {
                throw new InvalidOperationException("The PostgreSQL migration ledger does not match this MutualGPU build.");
            }

            foreach (var migration in migrations)
            {
                await ApplyAsync(connection, migration, cancellationToken).ConfigureAwait(false);
            }
            // The datasource is constructed before the startup migrator runs.
            // Refresh its type metadata after creating PostgreSQL enum types so
            // an empty database can serve enum-backed writes without a restart.
            await dataSource.ReloadTypesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await using var release = new NpgsqlCommand("select pg_advisory_unlock(@lock_id)", connection);
            release.Parameters.AddWithValue("lock_id", MigrationLockId);
            await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<bool> IsCompatibleAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var migrations = LoadMigrations();
            var ledger = await ReadLedgerAsync(connection, cancellationToken).ConfigureAwait(false);
            return ledger.Count == migrations.Count &&
                   LedgerContainsOnlyExpectedMigrations(ledger, migrations);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return false;
        }
    }

    private static async Task EnsureLedgerAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            create table if not exists schema_migrations (
                version integer primary key,
                name text not null unique,
                sha256 char(64) not null,
                applied_at timestamptz not null
            )
            """,
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyAsync(
        NpgsqlConnection connection,
        Migration migration,
        CancellationToken cancellationToken)
    {
        await using var check = new NpgsqlCommand(
            "select name, sha256 from schema_migrations where version = @version",
            connection);
        check.Parameters.AddWithValue("version", migration.Version);
        {
            await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!StringComparer.Ordinal.Equals(reader.GetString(0), migration.Name) ||
                    !StringComparer.Ordinal.Equals(reader.GetString(1).Trim(), migration.Sha256))
                {
                    throw new InvalidOperationException($"PostgreSQL migration {migration.Version} checksum does not match the applied schema.");
                }
                return;
            }
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var apply = new NpgsqlCommand(migration.Sql, connection, transaction))
            {
                await apply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var record = new NpgsqlCommand(
                """
                insert into schema_migrations(version, name, sha256, applied_at)
                values (@version, @name, @sha256, now())
                """,
                connection,
                transaction))
            {
                record.Parameters.AddWithValue("version", migration.Version);
                record.Parameters.AddWithValue("name", migration.Name);
                record.Parameters.Add("sha256", NpgsqlDbType.Char).Value = migration.Sha256;
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static IReadOnlyList<Migration> LoadMigrations()
    {
        var assembly = typeof(PostgresMigrator).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                var fileName = name[ResourcePrefix.Length..];
                if (!Int32.TryParse(fileName.AsSpan(0, 4), out var version))
                {
                    throw new InvalidOperationException($"PostgreSQL migration resource '{fileName}' does not begin with a four-digit version.");
                }

                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"PostgreSQL migration resource '{name}' is missing.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var sql = reader.ReadToEnd();
                var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
                return new Migration(version, fileName, checksum, sql);
            })
            .ToArray();
    }

    private static async Task<IReadOnlyList<AppliedMigration>> ReadLedgerAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select version, name, sha256 from schema_migrations order by version",
            connection);
        var ledger = new List<AppliedMigration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ledger.Add(new AppliedMigration(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2).Trim()));
        }
        return ledger;
    }

    private static bool LedgerContainsOnlyExpectedMigrations(
        IReadOnlyList<AppliedMigration> ledger,
        IReadOnlyList<Migration> migrations)
    {
        var expected = migrations.ToDictionary(static migration => migration.Version);
        return ledger.All(applied =>
            expected.TryGetValue(applied.Version, out var migration) &&
            StringComparer.Ordinal.Equals(applied.Name, migration.Name) &&
            StringComparer.Ordinal.Equals(applied.Sha256, migration.Sha256));
    }

    private sealed record Migration(int Version, string Name, string Sha256, string Sql);
    private sealed record AppliedMigration(int Version, string Name, string Sha256);
}

public interface IPostgresHealth
{
    Task CheckHealthAsync(CancellationToken cancellationToken);
}

public sealed class PostgresHealth(NpgsqlDataSource dataSource, PostgresMigrator migrator) : IPostgresHealth
{
    public async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select 1", connection);
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!await migrator.IsCompatibleAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The PostgreSQL schema is not compatible with this MutualGPU build.");
        }
    }
}
