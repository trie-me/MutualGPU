using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MutualGPU.Application;
using MutualGPU.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace MutualGPU.Application.Tests;

[Collection(PostgresMigrationCollection.Name)]
public sealed class PostgresArtifactLocationMigrationTests
{
    [PostgresFact]
    public async Task Empty_schema_applies_the_artifact_location_migration()
    {
        await using var schema = await IsolatedSchema.CreateAsync();

        await new PostgresMigrator(schema.DataSource).MigrateAsync(CancellationToken.None);

        Assert.Equal(4L, await schema.ScalarAsync<long>("select count(*) from schema_migrations"));
        Assert.True(await schema.ScalarAsync<bool>("select to_regclass('artifact_locations') is not null"));
        Assert.True(await schema.ScalarAsync<bool>("select to_regtype('artifact_location_state') is not null"));
    }

    [PostgresFact]
    public async Task Upgrade_backfills_legacy_artifacts_and_upload_write_targets()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await ApplyLegacyMigrationsAsync(schema);
        var artifactId = await InsertLegacyArtifactAsync(schema);

        await new PostgresMigrator(schema.DataSource).MigrateAsync(CancellationToken.None);

        await using var command = new NpgsqlCommand(
            """
            select l.storage_target_id, l.object_key, l.provider_etag,
                   l.state::text, u.write_storage_target_id
            from artifacts a
            join artifact_locations l on l.artifact_id = a.id
            join result_upload_operations u on u.id = a.result_upload_operation_id
            where a.id = @artifact_id
            """,
            schema.DataSource.CreateConnection());
        await using var connection = command.Connection!;
        await connection.OpenAsync();
        command.Parameters.AddWithValue("artifact_id", artifactId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(ArtifactStorageTargetIds.AwsPrimary, reader.GetString(0));
        Assert.Equal("mutualgpu/v3/legacy/result.zip", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal("available", reader.GetString(3));
        Assert.Equal(ArtifactStorageTargetIds.AwsPrimary, reader.GetString(4));
        Assert.False(await reader.ReadAsync());
    }

    [PostgresFact]
    public async Task Migration_compatibility_requires_the_exact_expected_ledger()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        var migrator = new PostgresMigrator(schema.DataSource);

        await migrator.MigrateAsync(CancellationToken.None);
        await migrator.MigrateAsync(CancellationToken.None);
        Assert.True(await migrator.IsCompatibleAsync(CancellationToken.None));

        await schema.ExecuteAsync(
            "update schema_migrations set sha256 = repeat('0', 64) where version = 4");

        Assert.False(await migrator.IsCompatibleAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => migrator.MigrateAsync(CancellationToken.None));
    }

    [PostgresFact]
    public async Task Available_historical_targets_must_remain_configured_without_constructing_a_provider()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await new PostgresMigrator(schema.DataSource).MigrateAsync(CancellationToken.None);
        var artifactId = await InsertLegacyArtifactAsync(schema);
        await using (var command = schema.CreateCommand(
            """
            insert into artifact_locations(
                artifact_id, storage_target_id, object_key, provider_etag,
                state, created_at, last_verified_at)
            values (
                @artifact_id, 'backblaze-archive', 'compatibility/historical/result.zip', null,
                'available', now(), null)
            """))
        await using (var connection = command.Connection!)
        {
            await connection.OpenAsync();
            command.Parameters.AddWithValue("artifact_id", artifactId);
            await command.ExecuteNonQueryAsync();
        }

        var aws = new InMemoryObjectStore();
        var backblaze = new InMemoryObjectStore();
        using var configuredTargets = new ObjectStoreRegistry(
            ArtifactStorageTargetIds.AwsPrimary,
            [
                new ObjectStoreTarget(
                    ArtifactStorageTargetIds.AwsPrimary,
                    aws,
                    aws,
                    new NoOpBrowserObjectCorsPolicy()),
                new ObjectStoreTarget(
                    "backblaze-archive",
                    backblaze,
                    backblaze,
                    new NoOpBrowserObjectCorsPolicy()),
            ],
            ownsTargets: false);
        await new PostgresArtifactStorageTargetValidator(schema.DataSource, configuredTargets)
            .ValidateAsync(CancellationToken.None);

        using var missingHistoricalTarget = new ObjectStoreRegistry(
            ArtifactStorageTargetIds.AwsPrimary,
            [new ObjectStoreTarget(
                ArtifactStorageTargetIds.AwsPrimary,
                aws,
                aws,
                new NoOpBrowserObjectCorsPolicy())],
            ownsTargets: false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresArtifactStorageTargetValidator(schema.DataSource, missingHistoricalTarget)
                .ValidateAsync(CancellationToken.None));
        Assert.Contains("backblaze-archive", error.Message, StringComparison.Ordinal);
    }

    private static async Task ApplyLegacyMigrationsAsync(IsolatedSchema schema)
    {
        var migrations = Enumerable.Range(1, 3).Select(ReadMigration).ToArray();
        foreach (var migration in migrations)
        {
            await schema.ExecuteAsync(migration.Sql);
        }

        await schema.ExecuteAsync(
            """
            create table schema_migrations (
                version integer primary key,
                name text not null unique,
                sha256 char(64) not null,
                applied_at timestamptz not null
            )
            """);
        foreach (var migration in migrations)
        {
            await using var command = schema.CreateCommand(
            """
                insert into schema_migrations(version, name, sha256, applied_at)
                values (@version, @name, @sha256, now())
                """);
            await using var connection = command.Connection!;
            await connection.OpenAsync();
            command.Parameters.AddWithValue("version", migration.Version);
            command.Parameters.AddWithValue("name", migration.Name);
            command.Parameters.Add("sha256", NpgsqlDbType.Char).Value = migration.Sha256;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<Guid> InsertLegacyArtifactAsync(IsolatedSchema schema)
    {
        var capabilityId = Guid.CreateVersion7();
        var executionUnitId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var attemptId = Guid.CreateVersion7();
        var uploadId = Guid.CreateVersion7();
        var artifactId = Guid.CreateVersion7();
        await using var command = schema.CreateCommand(
            """
            insert into capabilities(
                id, name, normalized_name, contract_hash, definition, created_at)
            values (@capability_id, 'legacy-capability', 'legacy-capability', 'legacy-hash', '{}'::jsonb, now());

            insert into execution_units(
                id, enrollment_version, persistence_version, machine, current_enrollment, created_at, updated_at)
            values (@execution_unit_id, 1, 1, '{}'::jsonb, '{}'::jsonb, now(), now());

            insert into tasks(
                id, requestor_id, capability_id, capability_contract_hash, capability_snapshot,
                compute_tier, memory_gib, scalar_parameters, status, version, created_at, updated_at)
            values (
                @task_id, @requestor_id, @capability_id, 'legacy-hash', '{}'::jsonb,
                2, 8, '{}'::jsonb, 'assigned', 1, now(), now());

            insert into task_attempts(
                id, task_id, execution_unit_id, assignment_number, handle_digest,
                handle_ciphertext, state, assigned_at)
            values (
                @attempt_id, @task_id, @execution_unit_id, 1, @handle_digest,
                decode(repeat('00', 32), 'hex'), 'assigned', now());

            insert into result_upload_operations(
                id, task_id, attempt_id, execution_unit_id, handle_digest,
                state, version, created_at)
            values (
                @upload_id, @task_id, @attempt_id, @execution_unit_id, @handle_digest,
                'uploaded', 1, now());

            insert into artifacts(
                id, task_id, attempt_id, result_upload_operation_id, direction, role,
                s3_object_key, content_type, length, sha256, state, created_at)
            values (
                @artifact_id, @task_id, @attempt_id, @upload_id, 'output', 'result',
                'mutualgpu/v3/legacy/result.zip', 'application/zip', 1, @sha256, 'available', now());
            """);
        await using var connection = command.Connection!;
        await connection.OpenAsync();
        command.Parameters.AddWithValue("capability_id", capabilityId);
        command.Parameters.AddWithValue("execution_unit_id", executionUnitId);
        command.Parameters.AddWithValue("task_id", taskId);
        command.Parameters.AddWithValue("attempt_id", attemptId);
        command.Parameters.AddWithValue("upload_id", uploadId);
        command.Parameters.AddWithValue("artifact_id", artifactId);
        command.Parameters.AddWithValue("requestor_id", Guid.CreateVersion7());
        command.Parameters.Add("handle_digest", NpgsqlDbType.Char).Value = new string('a', 64);
        command.Parameters.Add("sha256", NpgsqlDbType.Char).Value = new string('b', 64);
        await command.ExecuteNonQueryAsync();
        return artifactId;
    }

    private static MigrationResource ReadMigration(int version)
    {
        var assembly = typeof(PostgresMigrator).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.StartsWith(
                $"MutualGPU.Infrastructure.Postgres.Migrations.{version:D4}_",
                StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Migration resource '{name}' is unavailable.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sql = reader.ReadToEnd();
        return new MigrationResource(
            version,
            name["MutualGPU.Infrastructure.Postgres.Migrations.".Length..],
            sql,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant());
    }

    private sealed record MigrationResource(int Version, string Name, string Sql, string Sha256);

    private sealed class IsolatedSchema : IAsyncDisposable
    {
        private readonly string connectionString;

        private IsolatedSchema(string connectionString, string name, NpgsqlDataSource dataSource)
        {
            this.connectionString = connectionString;
            Name = name;
            DataSource = dataSource;
        }

        public string Name { get; }

        public NpgsqlDataSource DataSource { get; }

        public static async Task<IsolatedSchema> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("MUTUALGPU_TEST_POSTGRES")
                ?? throw new InvalidOperationException("MUTUALGPU_TEST_POSTGRES is required.");
            var name = $"artifact_location_{Guid.CreateVersion7():N}";
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"create schema \"{name}\"", connection);
                await command.ExecuteNonQueryAsync();
            }
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = name,
            };
            return new IsolatedSchema(
                connectionString,
                name,
                new NpgsqlDataSourceBuilder(builder.ConnectionString).Build());
        }

        public NpgsqlCommand CreateCommand(string sql) => new(sql, DataSource.CreateConnection());

        public async Task ExecuteAsync(string sql)
        {
            await using var command = CreateCommand(sql);
            await using var connection = command.Connection!;
            await connection.OpenAsync();
            await command.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using var command = CreateCommand(sql);
            await using var connection = command.Connection!;
            await connection.OpenAsync();
            return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("The scalar query returned null."));
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"drop schema if exists \"{Name}\" cascade", connection);
            await command.ExecuteNonQueryAsync();
        }
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

[CollectionDefinition(PostgresMigrationCollection.Name, DisableParallelization = true)]
public sealed class PostgresMigrationCollection
{
    public const string Name = "PostgreSQL migration schemas";
}
