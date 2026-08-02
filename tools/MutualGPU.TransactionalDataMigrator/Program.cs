using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MutualGPU.Application;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;
using Npgsql;
using NpgsqlTypes;

var mode = args switch
{
    ["import"] => MigrationMode.Import,
    ["verify"] => MigrationMode.Verify,
    _ => throw new ArgumentException("Usage: MutualGPU.TransactionalDataMigrator import | verify"),
};

if (!StringComparer.Ordinal.Equals(Environment.GetEnvironmentVariable("MUTUALGPU_TARGET_TASK_MODE"), "true"))
{
    throw new InvalidOperationException("The transactional migrator may run only in the reviewed target ECS task mode.");
}
var applicationBucket = RequiredEnvironmentVariable("MUTUALGPU_MIGRATION_APPLICATION_BUCKET");
var providerBucket = Environment.GetEnvironmentVariable("MUTUALGPU_MIGRATION_PROVIDER_BUCKET");
if (String.IsNullOrWhiteSpace(providerBucket)) providerBucket = applicationBucket;
var region = RequiredEnvironmentVariable("AWS_REGION");
if (!StringComparer.Ordinal.Equals(region, "us-east-1"))
{
    throw new InvalidOperationException("The target-only migration utility supports us-east-1 only.");
}
if (!String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_PROFILE")) ||
    !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_DEFAULT_PROFILE")))
{
    throw new InvalidOperationException("Target-task mode requires the ECS task-role credential chain; AWS profile overrides are forbidden.");
}
var storageTarget = new ArtifactStorageTargetSelection(
    RequiredEnvironmentVariable("MUTUALGPU_MIGRATION_WRITE_STORAGE_TARGET_ID"));
var postgres = PostgresEnvironment();
var targetAttestation = TargetMigrationAttestationExpectation.FromEnvironment(
    applicationBucket,
    region,
    postgres);
await AwsTargetMigrationAttestor.AttestAsync(targetAttestation, CancellationToken.None);
if (mode is MigrationMode.Import)
{
    // Imports may contain recoverable task handles. They are encrypted before
    // reaching PostgreSQL and require the same envelope key as the service.
    postgres.Validate(production: postgres.RequireTls);
}

await using var dataSource = PostgresDataSourceFactory.Create(postgres);
var migrator = new PostgresMigrator(dataSource);
if (mode is MigrationMode.Import)
{
    await migrator.MigrateAsync(CancellationToken.None);
}
else if (!await migrator.IsCompatibleAsync(CancellationToken.None))
{
    throw new InvalidOperationException("The PostgreSQL schema is not compatible with this migration utility.");
}

using var applicationStore = new AwsS3ObjectStore(
    new AwsS3ObjectStoreOptions(applicationBucket, region));
using var providerStore = StringComparer.Ordinal.Equals(providerBucket, applicationBucket)
    ? null
    : new AwsS3ObjectStore(new AwsS3ObjectStoreOptions(providerBucket, region));
var providerSource = providerStore ?? applicationStore;
var objectKeys = new MutualGpuObjectKeys();
var runner = new TransactionalDataMigration(
    dataSource,
    mode is MigrationMode.Import
        ? new PostgresOperationUnitOfWork(dataSource, new HandleCipher(postgres), objectKeys, storageTarget)
        : null,
    applicationStore,
    providerSource,
    objectKeys,
    applicationBucket,
    providerBucket);

var report = mode is MigrationMode.Import
    ? await runner.ImportAsync(CancellationToken.None)
    : await runner.VerifyAsync(CancellationToken.None);
Console.WriteLine(JsonSerializer.Serialize(report, MigrationJson.Options));
if (report.HasUnexplainedDiscrepancies)
{
    Environment.ExitCode = 1;
}

static PostgresOptions PostgresEnvironment() => new()
{
    ConnectionString = Environment.GetEnvironmentVariable("MutualGPU__Postgres__ConnectionString"),
    Host = Environment.GetEnvironmentVariable("MutualGPU__Postgres__Host"),
    Port = Int32.TryParse(Environment.GetEnvironmentVariable("MutualGPU__Postgres__Port"), out var port) ? port : 5432,
    Database = Environment.GetEnvironmentVariable("MutualGPU__Postgres__Database") ?? "mutualgpu",
    Username = Environment.GetEnvironmentVariable("MutualGPU__Postgres__Username") ?? "mutualgpu",
    Password = Environment.GetEnvironmentVariable("MutualGPU__Postgres__Password"),
    RequireTls = Boolean.TryParse(Environment.GetEnvironmentVariable("MutualGPU__Postgres__RequireTls"), out var tls) && tls,
    HandleEncryptionKey = Environment.GetEnvironmentVariable("MutualGPU__Postgres__HandleEncryptionKey"),
};

static string RequiredEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name) switch
{
    { Length: > 0 } value => value,
    _ => throw new InvalidOperationException($"The {name} environment variable is required."),
};

internal enum MigrationMode
{
    Import,
    Verify,
}

internal static class MigrationJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static NpgsqlParameter Parameter(string name, object value) =>
        new(name, NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(value, Options),
        };
}

internal sealed class TransactionalDataMigration(
    NpgsqlDataSource dataSource,
    IOperationUnitOfWork? operations,
    IObjectStore applicationStore,
    IObjectStore providerStore,
    MutualGpuObjectKeys objectKeys,
    string applicationBucket,
    string providerBucket)
{
    private const string Root = "mutualgpu/v3";
    private readonly MigrationReport report = new();
    private readonly string applicationSource = $"aws-s3:{applicationBucket}";
    private readonly string providerSource = $"aws-s3:{providerBucket}";
    private readonly Dictionary<Guid, ProviderBindingSource> providerBindings = [];
    private readonly List<ObjectEntry> requestorEntries = [];
    private bool providerBindingsLoaded;

    public async Task<MigrationReport> ImportAsync(CancellationToken cancellationToken)
    {
        if (operations is null) throw new InvalidOperationException("Import operations are unavailable.");
        await LoadProviderBindingsAsync(cancellationToken).ConfigureAwait(false);
        await ImportProviderBindingsAsync(cancellationToken).ConfigureAwait(false);
        await ImportCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        await ImportExecutionUnitsAsync(cancellationToken).ConfigureAwait(false);
        await ImportTasksAsync(cancellationToken).ConfigureAwait(false);
        await ImportAttemptEventsAsync(cancellationToken).ConfigureAwait(false);
        await ImportPartnerResourcesAsync(cancellationToken).ConfigureAwait(false);
        await CrossCheckCommitMarkersAsync(cancellationToken).ConfigureAwait(false);
        await PopulateDatabaseSummaryAsync(cancellationToken).ConfigureAwait(false);
        await VerifyProviderBindingParityAsync(cancellationToken).ConfigureAwait(false);
        await VerifyArtifactDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        return report;
    }

    public async Task<MigrationReport> VerifyAsync(CancellationToken cancellationToken)
    {
        await InventorySourceAsync(cancellationToken).ConfigureAwait(false);
        await PopulateDatabaseSummaryAsync(cancellationToken).ConfigureAwait(false);
        await VerifyLedgerAsync(cancellationToken).ConfigureAwait(false);
        await VerifyProviderBindingParityAsync(cancellationToken).ConfigureAwait(false);
        await VerifyQueueParityAsync(cancellationToken).ConfigureAwait(false);
        await VerifyArtifactDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        return report;
    }

    private async Task ImportProviderBindingsAsync(CancellationToken cancellationToken)
    {
        foreach (var source in providerBindings.Values.OrderBy(static source => source.Digest, StringComparer.Ordinal))
        {
            if (await AlreadyImportedAsync(providerSource, source.Entry, cancellationToken).ConfigureAwait(false))
            {
                if (!await ProviderBindingExistsAsync(source, cancellationToken).ConfigureAwait(false))
                    report.Discrepancy("provider_binding_missing_after_resume", source.ExecutionUnitId);
                continue;
            }

            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var insert = new NpgsqlCommand(
                """
                insert into provider_credentials(
                    digest, execution_unit_id, created_at, version)
                values (@digest, @execution_unit_id, @created_at, 1)
                on conflict do nothing
                """,
                connection,
                transaction))
            {
                insert.Parameters.Add("digest", NpgsqlDbType.Char).Value = source.Digest;
                insert.Parameters.AddWithValue("execution_unit_id", source.ExecutionUnitId);
                insert.Parameters.AddWithValue("created_at", source.Entry.LastModified.UtcDateTime);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var matches = false;
            await using (var check = new NpgsqlCommand(
                """
                select exists(
                    select 1 from provider_credentials
                    where digest = @digest
                      and execution_unit_id = @execution_unit_id)
                """,
                connection,
                transaction))
            {
                check.Parameters.Add("digest", NpgsqlDbType.Char).Value = source.Digest;
                check.Parameters.AddWithValue("execution_unit_id", source.ExecutionUnitId);
                matches = (bool)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
            }
            var discrepancy = matches ? null : "provider_binding_conflict";
            await RecordLedgerAsync(
                connection,
                transaction,
                providerSource,
                source.Entry,
                "provider_binding",
                source.ExecutionUnitId,
                discrepancy,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            report.Imported("provider_binding");
            if (discrepancy is not null) report.Discrepancy(discrepancy, source.ExecutionUnitId);
        }
    }

    private async Task ImportCapabilitiesAsync(CancellationToken cancellationToken)
    {
        await foreach (var entry in applicationStore.ListAsync(objectKeys.Capabilities(), cancellationToken))
        {
            report.Source("capability");
            if (!entry.Key.Value.EndsWith("/definition.json", StringComparison.Ordinal)) continue;
            if (await AlreadyImportedAsync(applicationSource, entry, cancellationToken).ConfigureAwait(false)) continue;
            var capability = await ReadAsync<CapabilityDefinition>(applicationStore, entry.Key, cancellationToken).ConfigureAwait(false);
            if (capability is null)
            {
                await RecordDiscrepancyAsync(
                    applicationSource,
                    entry,
                    "capability",
                    null,
                    "capability_invalid",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            string? discrepancy = null;
            try
            {
                await EnsureCapabilityAsync(capability, cancellationToken).ConfigureAwait(false);
            }
            catch (CapabilityNameConflictException)
            {
                discrepancy = "capability_name_conflict";
            }
            await RecordSourceAsync(
                applicationSource,
                entry,
                "capability",
                capability.Id.Value,
                discrepancy,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ImportExecutionUnitsAsync(CancellationToken cancellationToken)
    {
        var identities = new List<(ObjectEntry Entry, string Digest)>();
        await foreach (var entry in applicationStore.ListAsync(
            new ObjectPrefix($"{Root}/nodes"),
            cancellationToken))
        {
            if (!entry.Key.Value.EndsWith("/identity.json", StringComparison.Ordinal)) continue;
            var segments = entry.Key.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var nodeIndex = Array.IndexOf(segments, "nodes");
            var digest = nodeIndex >= 0 && nodeIndex + 1 < segments.Length
                ? segments[nodeIndex + 1]
                : "";
            if (!Regex.IsMatch(digest, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            {
                await RecordDiscrepancyAsync(
                    applicationSource,
                    entry,
                    "execution_unit",
                    null,
                    "execution_unit_digest_invalid",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            identities.Add((entry, digest));
        }

        foreach (var (identityEntry, digest) in identities)
        {
            report.Source("execution_unit");
            var identity = await ReadAsync<ExecutionUnitSnapshot>(
                applicationStore,
                identityEntry.Key,
                cancellationToken).ConfigureAwait(false);
            if (identity is null || identity.Id.Value == Guid.Empty)
            {
                await RecordDiscrepancyAsync(
                    applicationSource,
                    identityEntry,
                    "execution_unit",
                    identity?.Id.Value,
                    "execution_unit_identity_invalid",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            var executionUnitId = identity.Id.Value;
            if (!providerBindings.TryGetValue(executionUnitId, out var binding))
            {
                report.Discrepancy("execution_unit_provider_binding_missing", executionUnitId);
            }
            else if (!StringComparer.Ordinal.Equals(binding.Digest, digest))
            {
                report.Discrepancy("execution_unit_provider_digest_mismatch", executionUnitId);
            }

            var enrollmentEntries = new List<(ObjectEntry Entry, EnrollmentEvent Event)>();
            await foreach (var entry in applicationStore.ListAsync(
                objectKeys.EnrollmentsForProviderDigest(digest),
                cancellationToken))
            {
                report.Source("enrollment_event");
                var enrollment = await ReadAsync<EnrollmentEvent>(
                    applicationStore,
                    entry.Key,
                    cancellationToken).ConfigureAwait(false);
                if (enrollment is null || enrollment.Snapshot.Id.Value != executionUnitId)
                {
                    await RecordDiscrepancyAsync(
                        applicationSource,
                        entry,
                        "enrollment_event",
                        executionUnitId,
                        "enrollment_event_invalid",
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }
                enrollmentEntries.Add((entry, enrollment));
            }
            var latest = enrollmentEntries
                .OrderBy(static item => item.Event.Snapshot.Version.Value)
                .ThenBy(static item => item.Entry.Key.Value, StringComparer.Ordinal)
                .LastOrDefault();
            var current = latest.Event is not null &&
                latest.Event.Snapshot.Version.Value > identity.Version.Value
                    ? latest.Event.Snapshot
                    : identity;
            var identityDiscrepancy = current != identity ? "identity_behind_enrollment" : null;

            foreach (var capability in current.Enrollment.Capabilities)
            {
                await EnsureCapabilityAsync(capability, cancellationToken).ConfigureAwait(false);
            }
            await operations!.ExecuteAsync(
                async (operation, token) =>
                {
                    var existing = await operation.ExecutionUnits
                        .GetAsync(current.Id, token)
                        .ConfigureAwait(false);
                    if (existing is null)
                    {
                        operation.ExecutionUnits.Add(ExecutionUnit.Hydrate(current));
                    }
                    else if (existing.Version.Value < current.Version.Value)
                    {
                        operation.ExecutionUnits.Update(ExecutionUnit.Hydrate(current));
                    }
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            await RecordSourceAsync(
                applicationSource,
                identityEntry,
                "execution_unit",
                executionUnitId,
                identityDiscrepancy,
                cancellationToken).ConfigureAwait(false);

            foreach (var (entry, enrollment) in enrollmentEntries)
            {
                await ImportEnrollmentEventAsync(entry, enrollment, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ImportEnrollmentEventAsync(
        ObjectEntry entry,
        EnrollmentEvent enrollment,
        CancellationToken cancellationToken)
    {
        if (await AlreadyImportedAsync(applicationSource, entry, cancellationToken).ConfigureAwait(false)) return;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(
            """
            insert into enrollment_events(
                id, execution_unit_id, enrollment_version, occurred_at, snapshot)
            values (@id, @execution_unit_id, @enrollment_version, @occurred_at, @snapshot)
            on conflict (execution_unit_id, enrollment_version) do nothing
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", enrollment.Id.Value);
            command.Parameters.AddWithValue("execution_unit_id", enrollment.Snapshot.Id.Value);
            command.Parameters.AddWithValue("enrollment_version", (long)enrollment.Snapshot.Version.Value);
            command.Parameters.AddWithValue("occurred_at", enrollment.OccurredAt.UtcDateTime);
            command.Parameters.Add(MigrationJson.Parameter("snapshot", enrollment.Snapshot));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await RecordLedgerAsync(
            connection,
            transaction,
            applicationSource,
            entry,
            "enrollment_event",
            enrollment.Id.Value,
            null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        report.Imported("enrollment_event");
    }

    private async Task ImportTasksAsync(CancellationToken cancellationToken)
    {
        await LoadRequestorEntriesAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = requestorEntries
            .Where(static entry =>
                entry.Key.Value.EndsWith("/manifest.json", StringComparison.Ordinal) ||
                entry.Key.Value.Contains("/facts/", StringComparison.Ordinal) &&
                entry.Key.Value.EndsWith(".json", StringComparison.Ordinal))
            .Select(entry => (Entry: entry, Identity: ParseTaskIdentity(entry.Key.Value)))
            .Where(static item => item.Identity is not null)
            .GroupBy(static item => item.Identity!.Value);

        foreach (var group in snapshots)
        {
            foreach (var _ in group) report.Source("task_snapshot");
            var facts = group
                .Where(static item => item.Entry.Key.Value.Contains("/facts/", StringComparison.Ordinal))
                .OrderBy(static item => item.Entry.Key.Value, StringComparer.Ordinal)
                .ToArray();
            var selected = facts.LastOrDefault();
            if (selected.Entry is null)
            {
                selected = group.Single(static item =>
                    item.Entry.Key.Value.EndsWith("/manifest.json", StringComparison.Ordinal));
            }
            var snapshot = await ReadAsync<TaskRequestSnapshot>(
                applicationStore,
                selected.Entry.Key,
                cancellationToken).ConfigureAwait(false);
            if (snapshot is null ||
                snapshot.Id.Value != group.Key.TaskId ||
                snapshot.RequestorId.Value != group.Key.RequestorId)
            {
                await RecordDiscrepancyAsync(
                    applicationSource,
                    selected.Entry,
                    "task",
                    group.Key.TaskId,
                    "task_snapshot_invalid",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            string? discrepancy = null;
            if (!await ReferencedExecutionUnitsExistAsync(snapshot, cancellationToken).ConfigureAwait(false))
            {
                discrepancy = "task_execution_unit_missing";
            }
            else
            {
                try
                {
                    await EnsureCapabilityAsync(snapshot.Capability, cancellationToken).ConfigureAwait(false);
                    await operations!.ExecuteAsync(
                        async (operation, token) =>
                        {
                            var existing = await operation.Tasks
                                .GetAsync(snapshot.RequestorId, snapshot.Id, token)
                                .ConfigureAwait(false);
                            if (existing is null)
                            {
                                operation.Tasks.Add(TaskRequest.Hydrate(snapshot));
                            }
                            else if (!Equivalent(existing.ToSnapshot(), snapshot))
                            {
                                discrepancy = "task_destination_conflict";
                            }
                            return true;
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (TaskIdempotencyConflictException)
                {
                    discrepancy = "task_idempotency_conflict";
                }
            }

            foreach (var item in group)
            {
                var classification = item.Entry.Key == selected.Entry.Key
                    ? discrepancy
                    : "superseded_task_snapshot";
                await RecordSourceAsync(
                    applicationSource,
                    item.Entry,
                    "task",
                    snapshot.Id.Value,
                    classification,
                    cancellationToken,
                    countExpectedClassification: item.Entry.Key == selected.Entry.Key)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task ImportAttemptEventsAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in requestorEntries.Where(static entry =>
            entry.Key.Value.Contains("/attempts/", StringComparison.Ordinal) &&
            entry.Key.Value.Contains("/events/", StringComparison.Ordinal) &&
            entry.Key.Value.EndsWith(".json", StringComparison.Ordinal)))
        {
            report.Source("attempt_event");
            if (await AlreadyImportedAsync(applicationSource, entry, cancellationToken).ConfigureAwait(false)) continue;
            var identity = ParseAttemptEventIdentity(entry.Key.Value);
            var stateEvent = await ReadAsync<AttemptStateEvent>(
                applicationStore,
                entry.Key,
                cancellationToken).ConfigureAwait(false);
            if (identity is null || stateEvent is null || stateEvent.AttemptId.Value != identity.Value.AttemptId)
            {
                await RecordDiscrepancyAsync(
                    applicationSource,
                    entry,
                    "attempt_event",
                    identity?.AttemptId,
                    "attempt_event_invalid",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var exists = false;
            await using (var check = new NpgsqlCommand(
                """
                select exists(
                    select 1 from task_attempts
                    where id = @attempt_id and task_id = @task_id)
                """,
                connection,
                transaction))
            {
                check.Parameters.AddWithValue("attempt_id", identity.Value.AttemptId);
                check.Parameters.AddWithValue("task_id", identity.Value.TaskId);
                exists = (bool)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
            }
            var discrepancy = exists ? null : "attempt_event_destination_missing";
            if (exists)
            {
                await using var insert = new NpgsqlCommand(
                    """
                    insert into task_events(
                        id, task_id, task_version, attempt_id, event_type,
                        occurred_at, payload)
                    values (
                        @id, @task_id, @task_version, @attempt_id, @event_type,
                        @occurred_at, @payload)
                    on conflict do nothing
                    """,
                    connection,
                    transaction);
                insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
                insert.Parameters.AddWithValue("task_id", identity.Value.TaskId);
                insert.Parameters.AddWithValue("task_version", (long)identity.Value.Sequence + 1);
                insert.Parameters.AddWithValue("attempt_id", identity.Value.AttemptId);
                insert.Parameters.AddWithValue(
                    "event_type",
                    $"legacy_attempt_{stateEvent.State.ToString().ToLowerInvariant()}");
                insert.Parameters.AddWithValue("occurred_at", stateEvent.OccurredAt.UtcDateTime);
                insert.Parameters.Add(MigrationJson.Parameter("payload", new
                {
                    state = stateEvent.State,
                    failureStep = Bound(stateEvent.FailureStep, 128),
                }));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await RecordLedgerAsync(
                connection,
                transaction,
                applicationSource,
                entry,
                "attempt_event",
                identity.Value.AttemptId,
                discrepancy,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            report.Imported("attempt_event");
            if (discrepancy is not null) report.Discrepancy(discrepancy, identity.Value.AttemptId);
        }
    }

    private async Task ImportPartnerResourcesAsync(CancellationToken cancellationToken)
    {
        await foreach (var entry in applicationStore.ListAsync(
            objectKeys.PartnerResourceRequests(),
            cancellationToken))
        {
            report.Source("partner_resource");
            if (await AlreadyImportedAsync(applicationSource, entry, cancellationToken).ConfigureAwait(false)) continue;
            var request = await ReadAsync<PartnerResourceRequest>(
                applicationStore,
                entry.Key,
                cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                await RecordDiscrepancyAsync(
                    applicationSource,
                    entry,
                    "partner_resource",
                    null,
                    "partner_resource_invalid",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            string? discrepancy = null;
            await operations!.ExecuteAsync(
                async (operation, token) =>
                {
                    var existing = await operation.PartnerResources
                        .GetAsync(request.Id, token)
                        .ConfigureAwait(false);
                    if (existing is null) operation.PartnerResources.Add(request);
                    else if (existing != request) discrepancy = "partner_resource_destination_conflict";
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            await RecordSourceAsync(
                applicationSource,
                entry,
                "partner_resource",
                request.Id,
                discrepancy,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CrossCheckCommitMarkersAsync(CancellationToken cancellationToken)
    {
        await foreach (var entry in applicationStore.ListAsync(
            new ObjectPrefix($"{Root}/commits"),
            cancellationToken))
        {
            report.Source("commit_marker");
            if (await AlreadyImportedAsync(applicationSource, entry, cancellationToken).ConfigureAwait(false)) continue;
            var commit = await ReadAsync<CommitMarker>(
                applicationStore,
                entry.Key,
                cancellationToken).ConfigureAwait(false);
            var discrepancy = commit is null
                ? "commit_marker_invalid"
                : await CommitHasMissingMembersAsync(commit, cancellationToken).ConfigureAwait(false)
                    ? "commit_member_unaccounted"
                    : null;
            await RecordSourceAsync(
                applicationSource,
                entry,
                "derived_commit_marker",
                commit?.OperationId,
                discrepancy,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InventorySourceAsync(CancellationToken cancellationToken)
    {
        await LoadProviderBindingsAsync(cancellationToken).ConfigureAwait(false);
        await InventoryPrefixAsync(applicationStore, objectKeys.Capabilities(), "capability", cancellationToken)
            .ConfigureAwait(false);
        await InventoryPrefixAsync(applicationStore, new ObjectPrefix($"{Root}/nodes"), "node_record", cancellationToken)
            .ConfigureAwait(false);
        await InventoryPrefixAsync(applicationStore, objectKeys.PartnerResourceRequests(), "partner_resource", cancellationToken)
            .ConfigureAwait(false);
        await LoadRequestorEntriesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in requestorEntries)
        {
            if (entry.Key.Value.Contains("/inputs/", StringComparison.Ordinal)) report.Source("input_object");
            else if (entry.Key.Value.Contains("/results/", StringComparison.Ordinal)) report.Source("output_object");
            else if (entry.Key.Value.Contains("/facts/", StringComparison.Ordinal) ||
                     entry.Key.Value.EndsWith("/manifest.json", StringComparison.Ordinal)) report.Source("task_snapshot");
            else if (entry.Key.Value.Contains("/attempts/", StringComparison.Ordinal)) report.Source("attempt_event");
            else if (entry.Key.Value.Contains("/projections/", StringComparison.Ordinal)) report.Source("derived_projection");
        }
        await InventoryPrefixAsync(
            applicationStore,
            new ObjectPrefix($"{Root}/queue"),
            "derived_queue_marker",
            cancellationToken).ConfigureAwait(false);
        await InventoryPrefixAsync(
            applicationStore,
            new ObjectPrefix($"{Root}/commits"),
            "commit_marker",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyLedgerAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select destination_type,
                   count(*),
                   count(*) filter (where discrepancy_code is not null)
            from migration_ledger
            group by destination_type
            order by destination_type
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            report.Ledger[reader.GetString(0)] = new LedgerSummary(
                reader.GetInt64(1),
                reader.GetInt64(2));
        }
        await reader.DisposeAsync().ConfigureAwait(false);

        await using var discrepancies = new NpgsqlCommand(
            """
            select discrepancy_code, count(*)
            from migration_ledger
            where discrepancy_code is not null
              and discrepancy_code <> 'superseded_task_snapshot'
            group by discrepancy_code
            order by discrepancy_code
            """,
            connection);
        await using var discrepancyReader = await discrepancies
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await discrepancyReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            report.Discrepancy(
                discrepancyReader.GetString(0),
                null,
                discrepancyReader.GetInt64(1));
        }
    }

    /// <summary>
    /// Verifies that the immutable provider-key digest addressing in S3 and the
    /// PostgreSQL authentication index describe exactly the same bindings. The
    /// credential itself is never read, stored, or emitted.
    /// </summary>
    private async Task VerifyProviderBindingParityAsync(CancellationToken cancellationToken)
    {
        await LoadProviderBindingsAsync(cancellationToken).ConfigureAwait(false);
        var databaseBindings = new Dictionary<Guid, DatabaseProviderBinding>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(
            "select digest, execution_unit_id, revoked_at is not null from provider_credentials order by execution_unit_id",
            connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var executionUnitId = reader.GetGuid(1);
                var digest = reader.GetString(0).Trim();
                if (!databaseBindings.TryAdd(executionUnitId, new DatabaseProviderBinding(digest, reader.GetBoolean(2))))
                {
                    report.Discrepancy("database_provider_binding_execution_unit_duplicate", executionUnitId);
                }
            }
        }

        foreach (var (executionUnitId, source) in providerBindings)
        {
            if (!databaseBindings.TryGetValue(executionUnitId, out var database))
            {
                report.Discrepancy("provider_binding_missing_in_database", executionUnitId);
            }
            else if (!StringComparer.Ordinal.Equals(source.Digest, database.Digest))
            {
                report.Discrepancy("provider_binding_digest_mismatch", executionUnitId);
            }
            else if (database.Revoked)
            {
                report.Discrepancy("provider_binding_revoked_in_database", executionUnitId);
            }
        }
        foreach (var executionUnitId in databaseBindings.Keys.Except(providerBindings.Keys))
        {
            report.Discrepancy("database_provider_binding_not_in_source", executionUnitId);
        }

        var ledgerBindings = new Dictionary<string, ProviderBindingLedger>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
            """
            select source_key, source_etag, destination_id
            from migration_ledger
            where source_store = @source_store and destination_type = 'provider_binding'
            order by source_key
            """,
            connection))
        {
            command.Parameters.AddWithValue("source_store", providerSource);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sourceKey = reader.GetString(0);
                if (!ledgerBindings.TryAdd(
                    sourceKey,
                    new ProviderBindingLedger(
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetGuid(2))))
                {
                    report.Discrepancy("provider_binding_ledger_duplicate", null);
                }
            }
        }

        foreach (var source in providerBindings.Values)
        {
            if (!ledgerBindings.TryGetValue(source.Entry.Key.Value, out var ledger))
            {
                report.Discrepancy("provider_binding_ledger_missing", source.ExecutionUnitId);
                continue;
            }
            if (ledger.DestinationId != source.ExecutionUnitId)
            {
                report.Discrepancy("provider_binding_ledger_destination_mismatch", source.ExecutionUnitId);
            }
            if (ledger.ETag is null || source.Entry.ETag is null)
            {
                report.Discrepancy("provider_binding_source_etag_unavailable", source.ExecutionUnitId);
            }
            else if (!StringComparer.Ordinal.Equals(ledger.ETag, source.Entry.ETag))
            {
                report.Discrepancy("provider_binding_source_changed_after_import", source.ExecutionUnitId);
            }
        }
        foreach (var sourceKey in ledgerBindings.Keys.Except(
                     providerBindings.Values.Select(static source => source.Entry.Key.Value),
                     StringComparer.Ordinal))
        {
            report.Discrepancy("provider_binding_ledger_not_in_source", null);
        }
    }

    private async Task VerifyQueueParityAsync(CancellationToken cancellationToken)
    {
        var sourceQueued = new HashSet<Guid>();
        await foreach (var entry in applicationStore.ListAsync(
            new ObjectPrefix($"{Root}/queue"),
            cancellationToken))
        {
            var marker = await ReadAsync<QueueMarker>(applicationStore, entry.Key, cancellationToken)
                .ConfigureAwait(false);
            if (marker is not null) sourceQueued.Add(marker.TaskId.Value);
        }
        var databaseQueued = new HashSet<Guid>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "select id from tasks where status = 'queued' order by created_at, id",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            databaseQueued.Add(reader.GetGuid(0));
        }
        foreach (var missing in sourceQueued.Except(databaseQueued))
        {
            report.Discrepancy("queued_task_missing_in_database", missing);
        }
        foreach (var extra in databaseQueued.Except(sourceQueued))
        {
            report.Discrepancy("database_queued_task_not_in_source", extra);
        }
    }

    private async Task VerifyArtifactDescriptorsAsync(CancellationToken cancellationToken)
    {
        await LoadRequestorEntriesAsync(cancellationToken).ConfigureAwait(false);
        var objects = requestorEntries
            .Where(static entry =>
                entry.Key.Value.Contains("/inputs/", StringComparison.Ordinal) ||
                entry.Key.Value.Contains("/results/", StringComparison.Ordinal))
            .ToDictionary(static entry => entry.Key.Value, StringComparer.Ordinal);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select a.id, a.s3_object_key, a.length, a.sha256, a.state::text,
                   count(l.artifact_id), min(l.storage_target_id),
                   min(l.object_key), min(l.state::text)
            from artifacts a
            left join artifact_locations l on l.artifact_id = a.id
            group by a.id, a.s3_object_key, a.length, a.sha256, a.state
            order by a.s3_object_key
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            var key = reader.GetString(1);
            var expectedLength = reader.GetInt64(2);
            var expectedSha256 = reader.GetString(3).Trim();
            var logicalState = reader.GetString(4);
            var locationCount = reader.GetInt64(5);
            if (locationCount != 1)
            {
                report.Discrepancy("artifact_location_count_mismatch", id);
                continue;
            }

            var storageTargetId = reader.GetString(6);
            var locationKey = reader.GetString(7);
            var locationState = reader.GetString(8);
            if (!StringComparer.Ordinal.Equals(storageTargetId, ArtifactStorageTargetIds.AwsPrimary))
            {
                report.Discrepancy("artifact_location_target_not_aws_primary", id);
            }
            if (!StringComparer.Ordinal.Equals(locationKey, key))
            {
                report.Discrepancy("artifact_location_key_mismatch", id);
            }
            if (!StringComparer.Ordinal.Equals(locationState, logicalState))
            {
                report.Discrepancy("artifact_location_state_mismatch", id);
            }
            if (logicalState is not ("staged" or "available"))
            {
                continue;
            }

            if (!objects.TryGetValue(key, out var entry))
            {
                report.Discrepancy("artifact_object_missing", id);
            }
            else if (entry.Length != expectedLength)
            {
                report.Discrepancy("artifact_length_mismatch", id);
            }
            else
            {
                var read = await applicationStore.GetAsync(entry.Key, cancellationToken).ConfigureAwait(false);
                if (read is null)
                {
                    report.Discrepancy("artifact_object_missing", id);
                    continue;
                }

                await using (read)
                {
                    var sha256 = Convert.ToHexString(
                        await SHA256.HashDataAsync(read.Content, cancellationToken).ConfigureAwait(false))
                        .ToLowerInvariant();
                    if (!StringComparer.Ordinal.Equals(sha256, expectedSha256))
                    {
                        report.Discrepancy("artifact_sha256_mismatch", id);
                    }
                }
            }
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await using var uploads = new NpgsqlCommand(
            """
            select id
            from result_upload_operations
            where write_storage_target_id <> @storage_target_id
            order by id
            """,
            connection);
        uploads.Parameters.AddWithValue("storage_target_id", ArtifactStorageTargetIds.AwsPrimary);
        await using var uploadReader = await uploads.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await uploadReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            report.Discrepancy("result_upload_target_not_aws_primary", uploadReader.GetGuid(0));
        }
    }

    private async Task PopulateDatabaseSummaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in new[]
        {
            "capabilities",
            "provider_credentials",
            "execution_units",
            "enrollment_events",
            "tasks",
            "task_attempts",
            "task_events",
            "artifacts",
            "result_upload_operations",
            "partner_resource_requests",
            "operation_outbox",
            "migration_ledger",
        })
        {
            await using var command = new NpgsqlCommand($"select count(*) from {table}", connection);
            report.DatabaseRows[table] = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        await using var statuses = new NpgsqlCommand(
            "select status::text, count(*) from tasks group by status order by status",
            connection);
        await using var reader = await statuses.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            report.TaskStatuses[reader.GetString(0)] = reader.GetInt64(1);
        }
    }

    private async Task EnsureCapabilityAsync(
        CapabilityDefinition capability,
        CancellationToken cancellationToken)
    {
        await operations!.ExecuteAsync(
            async (operation, token) =>
            {
                var existing = await operation.Capabilities.GetAsync(capability.Id, token).ConfigureAwait(false);
                if (existing is null) operation.Capabilities.Add(capability);
                else if (!Equivalent(existing, capability))
                    throw new InvalidOperationException($"Capability '{capability.Id.Value:D}' conflicts with the migration source.");
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ReferencedExecutionUnitsExistAsync(
        TaskRequestSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Attempts.Count == 0) return true;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var executionUnitId in snapshot.Attempts.Select(static item => item.ExecutionUnitId.Value).Distinct())
        {
            await using var command = new NpgsqlCommand(
                "select exists(select 1 from execution_units where id = @id)",
                connection);
            command.Parameters.AddWithValue("id", executionUnitId);
            if (!(bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
                return false;
        }
        return true;
    }

    private async Task LoadProviderBindingsAsync(CancellationToken cancellationToken)
    {
        if (providerBindingsLoaded) return;
        var prefix = objectKeys.ProviderKeys().Value;
        var seenDigests = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in providerStore.ListAsync(objectKeys.ProviderKeys(), cancellationToken))
        {
            report.Source("provider_binding");
            var digest = ProviderDigestFromKey(entry.Key.Value, prefix);
            if (digest is null)
            {
                report.Discrepancy("provider_binding_key_invalid", null);
                continue;
            }
            if (!seenDigests.Add(digest))
            {
                report.Discrepancy("provider_binding_digest_duplicate", null);
                continue;
            }
            var binding = await ReadAsync<ProviderKeyBinding>(providerStore, entry.Key, cancellationToken).ConfigureAwait(false);
            if (binding is null || binding.ExecutionUnitId.Value == Guid.Empty)
            {
                report.Discrepancy("provider_binding_invalid", null);
                continue;
            }
            var source = new ProviderBindingSource(entry, digest, binding.ExecutionUnitId.Value);
            if (!providerBindings.TryAdd(source.ExecutionUnitId, source))
            {
                report.Discrepancy("provider_binding_execution_unit_duplicate", source.ExecutionUnitId);
            }
        }
        providerBindingsLoaded = true;
    }

    private async Task<bool> ProviderBindingExistsAsync(
        ProviderBindingSource source,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select exists(
                select 1 from provider_credentials
                where digest = @digest and execution_unit_id = @execution_unit_id)
            """,
            connection);
        command.Parameters.Add("digest", NpgsqlDbType.Char).Value = source.Digest;
        command.Parameters.AddWithValue("execution_unit_id", source.ExecutionUnitId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    private static string? ProviderDigestFromKey(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var suffix = key[prefix.Length..];
        return Regex.IsMatch(suffix, "^[0-9a-f]{64}\\.json$", RegexOptions.CultureInvariant)
            ? suffix[..64]
            : null;
    }

    private async Task LoadRequestorEntriesAsync(CancellationToken cancellationToken)
    {
        if (requestorEntries.Count > 0) return;
        await foreach (var entry in applicationStore.ListAsync(
            new ObjectPrefix($"{Root}/requestors"),
            cancellationToken))
        {
            requestorEntries.Add(entry);
        }
    }

    private async Task<bool> CommitHasMissingMembersAsync(
        CommitMarker commit,
        CancellationToken cancellationToken)
    {
        foreach (var key in commit.Keys)
        {
            if (key.Contains("/queue/", StringComparison.Ordinal) ||
                key.Contains("/projections/", StringComparison.Ordinal))
            {
                continue;
            }
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                select exists(
                    select 1 from migration_ledger
                    where source_store = @source_store and source_key = @source_key)
                """,
                connection);
            command.Parameters.AddWithValue("source_store", applicationSource);
            command.Parameters.AddWithValue("source_key", key);
            if (!(bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
                return true;
        }
        return false;
    }

    private async Task<bool> AlreadyImportedAsync(
        string source,
        ObjectEntry entry,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select source_etag
            from migration_ledger
            where source_store = @source_store and source_key = @source_key
            """,
            connection);
        command.Parameters.AddWithValue("source_store", source);
        command.Parameters.AddWithValue("source_key", entry.Key.Value);
        var recorded = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (recorded is null) return false;
        report.Skipped++;
        if (recorded is string etag &&
            entry.ETag is not null &&
            !StringComparer.Ordinal.Equals(etag, entry.ETag))
        {
            report.Discrepancy("source_changed_after_import", null);
        }
        return true;
    }

    private async Task RecordSourceAsync(
        string source,
        ObjectEntry entry,
        string destinationType,
        Guid? destinationId,
        string? discrepancy,
        CancellationToken cancellationToken,
        bool countExpectedClassification = true)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RecordLedgerAsync(
            connection,
            transaction,
            source,
            entry,
            destinationType,
            destinationId,
            discrepancy,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        report.Imported(destinationType);
        if (discrepancy is not null && countExpectedClassification)
            report.Discrepancy(discrepancy, destinationId);
    }

    private async Task RecordDiscrepancyAsync(
        string source,
        ObjectEntry entry,
        string destinationType,
        Guid? destinationId,
        string discrepancy,
        CancellationToken cancellationToken)
    {
        await RecordSourceAsync(
            source,
            entry,
            destinationType,
            destinationId,
            discrepancy,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecordLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string source,
        ObjectEntry entry,
        string destinationType,
        Guid? destinationId,
        string? discrepancy,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into migration_ledger(
                source_store, source_key, source_etag, source_last_modified,
                destination_type, destination_id, imported_at, discrepancy_code)
            values (
                @source_store, @source_key, @source_etag, @source_last_modified,
                @destination_type, @destination_id, now(), @discrepancy_code)
            on conflict (source_store, source_key) do nothing
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("source_store", source);
        command.Parameters.AddWithValue("source_key", entry.Key.Value);
        command.Parameters.Add("source_etag", NpgsqlDbType.Text).Value = (object?)entry.ETag ?? DBNull.Value;
        command.Parameters.AddWithValue("source_last_modified", entry.LastModified.UtcDateTime);
        command.Parameters.AddWithValue("destination_type", destinationType);
        command.Parameters.Add("destination_id", NpgsqlDbType.Uuid).Value =
            destinationId is null ? DBNull.Value : destinationId.Value;
        command.Parameters.Add("discrepancy_code", NpgsqlDbType.Text).Value =
            (object?)discrepancy ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InventoryPrefixAsync(
        IObjectStore store,
        ObjectPrefix prefix,
        string kind,
        CancellationToken cancellationToken)
    {
        await foreach (var _ in store.ListAsync(prefix, cancellationToken)) report.Source(kind);
    }

    private static async Task<T?> ReadAsync<T>(
        IObjectStore store,
        ObjectKey key,
        CancellationToken cancellationToken)
    {
        await using var read = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (read is null) return default;
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                read.Content,
                MigrationJson.Options,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static (Guid RequestorId, Guid TaskId)? ParseTaskIdentity(string key)
    {
        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var requestor = Array.IndexOf(segments, "requestors");
        var task = Array.IndexOf(segments, "tasks");
        return requestor >= 0 && requestor + 1 < segments.Length &&
               task >= 0 && task + 1 < segments.Length &&
               Guid.TryParseExact(segments[requestor + 1], "N", out var requestorId) &&
               Guid.TryParseExact(segments[task + 1], "N", out var taskId)
            ? (requestorId, taskId)
            : null;
    }

    private static (Guid TaskId, Guid AttemptId, int Sequence)? ParseAttemptEventIdentity(string key)
    {
        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var task = Array.IndexOf(segments, "tasks");
        var attempt = Array.IndexOf(segments, "attempts");
        var events = Array.IndexOf(segments, "events");
        var sequenceText = events >= 0 && events + 1 < segments.Length
            ? segments[events + 1].Split('-', 2)[0]
            : null;
        return task >= 0 && task + 1 < segments.Length &&
               attempt >= 0 && attempt + 1 < segments.Length &&
               Guid.TryParseExact(segments[task + 1], "N", out var taskId) &&
               Guid.TryParseExact(segments[attempt + 1], "N", out var attemptId) &&
               Int32.TryParse(sequenceText, CultureInfo.InvariantCulture, out var sequence)
            ? (taskId, attemptId, sequence)
            : null;
    }

    private static string? Bound(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

    private static bool Equivalent<T>(T left, T right) =>
        StringComparer.Ordinal.Equals(
            JsonSerializer.Serialize(left, MigrationJson.Options),
            JsonSerializer.Serialize(right, MigrationJson.Options));

    private sealed record ProviderKeyBinding(ExecutionUnitId ExecutionUnitId);
    private sealed record EnrollmentEvent(
        EnrollmentEventId Id,
        DateTimeOffset OccurredAt,
        ExecutionUnitSnapshot Snapshot);
    private sealed record AttemptStateEvent(
        AttemptId AttemptId,
        AttemptState State,
        DateTimeOffset OccurredAt,
        string? FailureStep,
        string? FailureReason = null);
    private sealed record CommitMarker(
        Guid OperationId,
        DateTimeOffset CommittedAt,
        IReadOnlyList<string> Keys);
    private sealed record QueueMarker(
        TaskId TaskId,
        RequestorId RequestorId,
        CapabilityId CapabilityId,
        MachineSpecifications Resources,
        DateTimeOffset CreatedAt);
    private sealed record ProviderBindingSource(ObjectEntry Entry, string Digest, Guid ExecutionUnitId);
    private sealed record DatabaseProviderBinding(string Digest, bool Revoked);
    private sealed record ProviderBindingLedger(string? ETag, Guid? DestinationId);
}

internal sealed class MigrationReport
{
    public int ReportVersion => 1;
    public SortedDictionary<string, long> SourceRecords { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> ImportedRecords { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> DatabaseRows { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> TaskStatuses { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, LedgerSummary> Ledger { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, DiscrepancySummary> Discrepancies { get; } = new(StringComparer.Ordinal);
    public long Skipped { get; set; }
    public bool HasUnexplainedDiscrepancies => Discrepancies.Count > 0;

    public void Source(string type) => Increment(SourceRecords, type);
    public void Imported(string type) => Increment(ImportedRecords, type);

    public void Discrepancy(string code, Guid? _, long count = 1)
    {
        if (!Discrepancies.TryGetValue(code, out var existing))
            existing = new DiscrepancySummary(0);
        // IDs, object keys, handles, and provider-key digests must stay out of
        // migration output. The classified aggregate is sufficient to fail the
        // release gate and direct an operator to the restricted execution log.
        Discrepancies[code] = new DiscrepancySummary(existing.Count + count);
    }

    private static void Increment(IDictionary<string, long> values, string key)
    {
        values.TryGetValue(key, out var current);
        values[key] = current + 1;
    }
}

internal sealed record LedgerSummary(long Count, long ClassifiedCount);
internal sealed record DiscrepancySummary(long Count);
