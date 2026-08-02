using System.Text.Json;
using MutualGPU.Application;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;
using Npgsql;

namespace MutualGPU.Application.Tests;

/// <summary>
/// Proves provider credentials are migrated as digest-only bindings and that a
/// resumed import or independent verification cannot report success when a
/// binding is missing, changed, or duplicated.
/// </summary>
[Collection(PostgresMigrationCollection.Name)]
public sealed class TransactionalDataMigratorProviderBindingTests
{
    private const string HandleEncryptionKey = "bXV0dWFsZ3B1LWxvY2FsLWRldmVsb3BtZW50LWtleSE=";

    [PostgresFact]
    public async Task Imports_every_digest_binding_and_verifies_postgres_authentication_without_storing_the_key()
    {
        await using var fixture = await Fixture.CreateAsync();
        var executionUnitId = ExecutionUnitId.New();
        const string testOnlyPresharedKey = "test-only-provider-credential";
        await fixture.SourceRegistry.ProvisionAsync(executionUnitId, testOnlyPresharedKey, CancellationToken.None);

        var imported = await fixture.CreateMigration().ImportAsync(CancellationToken.None);
        var verified = await fixture.CreateMigration().VerifyAsync(CancellationToken.None);
        var targetRegistry = new PostgresProviderCredentialRegistry(
            fixture.DataSource,
            fixture.ObjectKeys,
            TimeProvider.System);

        Assert.Empty(imported.Discrepancies);
        Assert.Empty(verified.Discrepancies);
        Assert.Equal(executionUnitId, await targetRegistry.AuthenticateAsync(testOnlyPresharedKey, CancellationToken.None));
        Assert.Equal(
            fixture.ObjectKeys.ProviderDigest(testOnlyPresharedKey),
            await targetRegistry.GetProviderKeyDigestAsync(executionUnitId, CancellationToken.None));
    }

    [PostgresFact]
    public async Task Resumed_import_fails_closed_when_a_previously_imported_binding_is_missing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var executionUnitId = ExecutionUnitId.New();
        await fixture.SourceRegistry.ProvisionAsync(executionUnitId, "test-only-resume-credential", CancellationToken.None);
        var imported = await fixture.CreateMigration().ImportAsync(CancellationToken.None);
        Assert.Empty(imported.Discrepancies);

        await fixture.ExecuteAsync("delete from provider_credentials");

        var resumed = await fixture.CreateMigration().ImportAsync(CancellationToken.None);

        Assert.True(resumed.HasUnexplainedDiscrepancies);
        Assert.Contains("provider_binding_missing_after_resume", resumed.Discrepancies.Keys);
    }

    [PostgresFact]
    public async Task Duplicate_source_bindings_for_one_execution_unit_fail_the_import()
    {
        await using var fixture = await Fixture.CreateAsync();
        var executionUnitId = ExecutionUnitId.New();
        await fixture.SourceRegistry.ProvisionAsync(executionUnitId, "test-only-duplicate-credential-a", CancellationToken.None);
        await fixture.SourceRegistry.ProvisionAsync(executionUnitId, "test-only-duplicate-credential-b", CancellationToken.None);

        var report = await fixture.CreateMigration().ImportAsync(CancellationToken.None);

        Assert.True(report.HasUnexplainedDiscrepancies);
        Assert.Contains("provider_binding_execution_unit_duplicate", report.Discrepancies.Keys);
    }

    [PostgresFact]
    public async Task Independent_verification_fails_closed_for_a_changed_binding_without_exposing_identifiers()
    {
        await using var fixture = await Fixture.CreateAsync();
        var executionUnitId = ExecutionUnitId.New();
        const string testOnlyPresharedKey = "test-only-verification-credential";
        await fixture.SourceRegistry.ProvisionAsync(executionUnitId, testOnlyPresharedKey, CancellationToken.None);
        var imported = await fixture.CreateMigration().ImportAsync(CancellationToken.None);
        Assert.Empty(imported.Discrepancies);

        await fixture.SetOnlyProviderDigestAsync(fixture.ObjectKeys.ProviderDigest("test-only-different-credential"));

        var verified = await fixture.CreateMigration().VerifyAsync(CancellationToken.None);
        var output = JsonSerializer.Serialize(verified);

        Assert.True(verified.HasUnexplainedDiscrepancies);
        Assert.Contains("provider_binding_digest_mismatch", verified.Discrepancies.Keys);
        Assert.DoesNotContain(executionUnitId.Value.ToString("D"), output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(testOnlyPresharedKey, output, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Independent_verification_rejects_a_revoked_database_binding()
    {
        await using var fixture = await Fixture.CreateAsync();
        var executionUnitId = ExecutionUnitId.New();
        await fixture.SourceRegistry.ProvisionAsync(executionUnitId, "test-only-revoked-credential", CancellationToken.None);
        var imported = await fixture.CreateMigration().ImportAsync(CancellationToken.None);
        Assert.Empty(imported.Discrepancies);

        await fixture.RevokeOnlyProviderBindingAsync();

        var verified = await fixture.CreateMigration().VerifyAsync(CancellationToken.None);

        Assert.True(verified.HasUnexplainedDiscrepancies);
        Assert.Contains("provider_binding_revoked_in_database", verified.Discrepancies.Keys);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string rootConnectionString;

        private Fixture(string rootConnectionString, string schema, NpgsqlDataSource dataSource)
        {
            this.rootConnectionString = rootConnectionString;
            Schema = schema;
            DataSource = dataSource;
            ObjectKeys = new MutualGpuObjectKeys();
            ApplicationStore = new InMemoryObjectStore();
            ProviderStore = new InMemoryObjectStore();
            SourceRegistry = new ObjectStoreProviderKeyRegistry(ProviderStore, ObjectKeys);
            Operations = new PostgresOperationUnitOfWork(
                DataSource,
                new HandleCipher(new PostgresOptions { HandleEncryptionKey = HandleEncryptionKey }),
                ObjectKeys,
                new ArtifactStorageTargetSelection(ArtifactStorageTargetIds.AwsPrimary));
        }

        public string Schema { get; }

        public NpgsqlDataSource DataSource { get; }

        public MutualGpuObjectKeys ObjectKeys { get; }

        public InMemoryObjectStore ApplicationStore { get; }

        public InMemoryObjectStore ProviderStore { get; }

        public ObjectStoreProviderKeyRegistry SourceRegistry { get; }

        public PostgresOperationUnitOfWork Operations { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var rootConnectionString = Environment.GetEnvironmentVariable("MUTUALGPU_TEST_POSTGRES")
                ?? throw new InvalidOperationException("MUTUALGPU_TEST_POSTGRES is required.");
            var schema = $"migrator_provider_{Guid.CreateVersion7():N}";
            await using (var connection = new NpgsqlConnection(rootConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"create schema \"{schema}\"", connection);
                await command.ExecuteNonQueryAsync();
            }
            var builder = new NpgsqlConnectionStringBuilder(rootConnectionString) { SearchPath = schema };
            var dataSource = PostgresDataSourceFactory.Create(new PostgresOptions
            {
                ConnectionString = builder.ConnectionString,
                HandleEncryptionKey = HandleEncryptionKey,
            });
            await new PostgresMigrator(dataSource).MigrateAsync(CancellationToken.None);
            return new Fixture(rootConnectionString, schema, dataSource);
        }

        public TransactionalDataMigration CreateMigration() => new(
            DataSource,
            Operations,
            ApplicationStore,
            ProviderStore,
            ObjectKeys,
            "test-application-bucket",
            "test-provider-bucket");

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task SetOnlyProviderDigestAsync(string digest)
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("update provider_credentials set digest = @digest", connection);
            command.Parameters.AddWithValue("digest", digest);
            await command.ExecuteNonQueryAsync();
        }

        public async Task RevokeOnlyProviderBindingAsync()
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("update provider_credentials set revoked_at = now()", connection);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await using var connection = new NpgsqlConnection(rootConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"drop schema if exists \"{Schema}\" cascade", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUTUALGPU_TEST_POSTGRES")))
                Skip = "Set MUTUALGPU_TEST_POSTGRES to run PostgreSQL integration tests.";
        }
    }
}
