using System.Text.RegularExpressions;
using Amazon;
using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Amazon.RDS;
using Amazon.RDS.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using MutualGPU.Infrastructure;

internal sealed record TargetMigrationAttestationExpectation(
    string ApplicationBucket,
    string Region,
    string FoundationStackName,
    string DatabaseIdentifier,
    string ExpectedDeploymentSecretArn,
    string ExpectedDeploymentSecretVersionId,
    string ExpectedDatabaseSecretVersionId,
    string DatabaseHost,
    int DatabasePort,
    string DatabaseName)
{
    internal const string TargetAccount = "428590861908";
    internal const string TargetMigrationRoleName = "mutualgpu-postgres-migration-task";

    internal static TargetMigrationAttestationExpectation FromEnvironment(
        string applicationBucket,
        string region,
        PostgresOptions postgres) => new(
            applicationBucket,
            region,
            Required("MUTUALGPU_MIGRATION_FOUNDATION_STACK_NAME"),
            Required("MUTUALGPU_MIGRATION_DATABASE_IDENTIFIER"),
            Required("MUTUALGPU_MIGRATION_DEPLOYMENT_SECRET_ARN"),
            Required("MUTUALGPU_MIGRATION_DEPLOYMENT_SECRET_VERSION_ID"),
            Required("MUTUALGPU_MIGRATION_DATABASE_SECRET_VERSION_ID"),
            RequiredPostgres(postgres.Host, "MutualGPU__Postgres__Host"),
            postgres.Port,
            RequiredPostgres(postgres.Database, "MutualGPU__Postgres__Database"));

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) switch
    {
        { Length: > 0 } value => value,
        _ => throw new System.InvalidOperationException($"The {name} environment variable is required for target attestation."),
    };

    private static string RequiredPostgres(string? value, string name) => value switch
    {
        { Length: > 0 } present => present,
        _ => throw new System.InvalidOperationException($"The {name} environment variable is required for target attestation."),
    };
}

internal sealed record TargetMigrationAttestationSnapshot(
    string Account,
    string CallerArn,
    IReadOnlyDictionary<string, string> FoundationOutputs,
    IReadOnlyDictionary<string, string> FoundationParameters,
    string DatabaseIdentifier,
    string DatabaseEngine,
    string DatabaseEngineVersion,
    string DatabaseHost,
    int DatabasePort,
    string DatabaseName,
    string DatabaseSecretArn,
    IReadOnlySet<string> CurrentDatabaseSecretVersionIds,
    IReadOnlySet<string> CurrentDeploymentSecretVersionIds);

internal static class AwsTargetMigrationAttestor
{
    internal static async Task AttestAsync(
        TargetMigrationAttestationExpectation expectation,
        CancellationToken cancellationToken)
    {
        if (!StringComparer.Ordinal.Equals(expectation.Region, "us-east-1")) throw Failed("region");

        using var sts = new AmazonSecurityTokenServiceClient(RegionEndpoint.USEast1);
        using var cloudFormation = new AmazonCloudFormationClient(RegionEndpoint.USEast1);
        using var rds = new AmazonRDSClient(RegionEndpoint.USEast1);
        using var s3 = new AmazonS3Client(RegionEndpoint.USEast1);
        using var secrets = new AmazonSecretsManagerClient(RegionEndpoint.USEast1);

        var identity = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken).ConfigureAwait(false);
        var foundation = await cloudFormation.DescribeStacksAsync(
            new DescribeStacksRequest { StackName = expectation.FoundationStackName },
            cancellationToken).ConfigureAwait(false);
        var stack = foundation.Stacks.SingleOrDefault();
        if (stack is null) throw Failed("foundation_stack");

        var database = await rds.DescribeDBInstancesAsync(
            new DescribeDBInstancesRequest { DBInstanceIdentifier = expectation.DatabaseIdentifier },
            cancellationToken).ConfigureAwait(false);
        var instance = database.DBInstances.SingleOrDefault();
        if (instance is null || instance.Endpoint is null) throw Failed("database_instance");

        var outputs = ToMap(stack.Outputs.Select(static output => (output.OutputKey, output.OutputValue)), "foundation_outputs");
        var parameters = ToMap(stack.Parameters.Select(static parameter => (parameter.ParameterKey, parameter.ParameterValue)), "foundation_parameters");
        var databaseSecretArn = RequiredMapValue(outputs, "DatabaseSecretArn", "foundation_database_secret");
        var databaseSecret = await secrets.DescribeSecretAsync(
            new DescribeSecretRequest { SecretId = databaseSecretArn },
            cancellationToken).ConfigureAwait(false);
        var deploymentSecret = await secrets.DescribeSecretAsync(
            new DescribeSecretRequest { SecretId = expectation.ExpectedDeploymentSecretArn },
            cancellationToken).ConfigureAwait(false);
        await s3.GetBucketAclAsync(
            new GetBucketAclRequest
            {
                BucketName = expectation.ApplicationBucket,
                ExpectedBucketOwner = TargetMigrationAttestationExpectation.TargetAccount,
            },
            cancellationToken).ConfigureAwait(false);

        TargetMigrationAttestationValidator.Validate(
            expectation,
            new TargetMigrationAttestationSnapshot(
                identity.Account ?? String.Empty,
                identity.Arn ?? String.Empty,
                outputs,
                parameters,
                instance.DBInstanceIdentifier ?? String.Empty,
                instance.Engine ?? String.Empty,
                instance.EngineVersion ?? String.Empty,
                instance.Endpoint.Address ?? String.Empty,
                instance.Endpoint.Port ?? 0,
                instance.DBName ?? String.Empty,
                databaseSecret.ARN ?? String.Empty,
                CurrentVersionIds(databaseSecret.VersionIdsToStages),
                CurrentVersionIds(deploymentSecret.VersionIdsToStages)));
    }

    private static Dictionary<string, string> ToMap(
        IEnumerable<(string Key, string Value)> values,
        string failureCode)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (String.IsNullOrWhiteSpace(key) || value is null || !result.TryAdd(key, value)) throw Failed(failureCode);
        }
        return result;
    }

    private static IReadOnlySet<string> CurrentVersionIds(Dictionary<string, List<string>>? versionIdsToStages) =>
        (versionIdsToStages ?? [])
        .Where(static pair => pair.Value.Contains("AWSCURRENT", StringComparer.Ordinal))
        .Select(static pair => pair.Key)
        .ToHashSet(StringComparer.Ordinal);

    private static string RequiredMapValue(IReadOnlyDictionary<string, string> values, string key, string failureCode) =>
        values.TryGetValue(key, out var value) && !String.IsNullOrWhiteSpace(value) ? value : throw Failed(failureCode);

    private static System.InvalidOperationException Failed(string code) =>
        new($"Target migration attestation failed: {code}.");
}

internal static class TargetMigrationAttestationValidator
{
    internal static void Validate(TargetMigrationAttestationExpectation expectation, TargetMigrationAttestationSnapshot actual)
    {
        Require(StringComparer.Ordinal.Equals(actual.Account, TargetMigrationAttestationExpectation.TargetAccount), "account");
        Require(Regex.IsMatch(
            actual.CallerArn,
            $"^arn:aws:sts::{TargetMigrationAttestationExpectation.TargetAccount}:assumed-role/{TargetMigrationAttestationExpectation.TargetMigrationRoleName}/[A-Za-z0-9+=,.@_-]+$",
            RegexOptions.CultureInvariant), "task_role");
        RequireMap(actual.FoundationOutputs, "ApplicationDataBucketName", expectation.ApplicationBucket, "bucket");
        RequireMap(actual.FoundationOutputs, "DatabaseEndpointAddress", expectation.DatabaseHost, "database_host");
        RequireMap(actual.FoundationOutputs, "DatabaseEndpointPort", expectation.DatabasePort.ToString(System.Globalization.CultureInfo.InvariantCulture), "database_port");
        RequireMap(actual.FoundationOutputs, "DatabaseName", expectation.DatabaseName, "database_name");
        RequireMap(actual.FoundationParameters, "DatabaseEngineVersion", actual.DatabaseEngineVersion, "database_engine_version");
        Require(StringComparer.Ordinal.Equals(actual.DatabaseIdentifier, expectation.DatabaseIdentifier), "database_identifier");
        Require(StringComparer.Ordinal.Equals(actual.DatabaseEngine, "postgres"), "database_engine");
        Require(StringComparer.Ordinal.Equals(actual.DatabaseHost, expectation.DatabaseHost), "database_host");
        Require(actual.DatabasePort == expectation.DatabasePort, "database_port");
        Require(StringComparer.Ordinal.Equals(actual.DatabaseName, expectation.DatabaseName), "database_name");
        Require(StringComparer.Ordinal.Equals(actual.DatabaseSecretArn, RequiredMapValue(actual.FoundationOutputs, "DatabaseSecretArn", "foundation_database_secret")), "database_secret_arn");
        Require(actual.CurrentDatabaseSecretVersionIds.SetEquals([expectation.ExpectedDatabaseSecretVersionId]), "database_secret_version");
        Require(actual.CurrentDeploymentSecretVersionIds.SetEquals([expectation.ExpectedDeploymentSecretVersionId]), "deployment_secret_version");
    }

    private static void RequireMap(IReadOnlyDictionary<string, string> values, string key, string expected, string code) =>
        Require(values.TryGetValue(key, out var actual) && StringComparer.Ordinal.Equals(actual, expected), code);

    private static string RequiredMapValue(IReadOnlyDictionary<string, string> values, string key, string code) =>
        values.TryGetValue(key, out var value) && !String.IsNullOrWhiteSpace(value) ? value : throw Failed(code);

    private static void Require(bool condition, string code)
    {
        if (!condition) throw Failed(code);
    }

    private static System.InvalidOperationException Failed(string code) => new($"Target migration attestation failed: {code}.");
}
