using MutualGPU.Application;

namespace MutualGPU.Application.Tests;

public sealed class TargetMigrationAttestationTests
{
    [Fact]
    public void Accepts_the_exact_target_task_role_stack_rds_and_current_secret_metadata()
    {
        TargetMigrationAttestationValidator.Validate(Expectation(), Snapshot());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("task_role")]
    [InlineData("bucket")]
    [InlineData("database_engine_version")]
    [InlineData("database_secret_version")]
    [InlineData("deployment_secret_version")]
    public void Fails_closed_with_a_safe_code_when_any_attested_target_value_drifts(string drift)
    {
        var snapshot = Snapshot() with
        {
            Account = drift == "account" ? "000000000000" : TargetMigrationAttestationExpectation.TargetAccount,
            CallerArn = drift == "task_role"
                ? "arn:aws:sts::428590861908:assumed-role/unreviewed/session"
                : "arn:aws:sts::428590861908:assumed-role/mutualgpu-postgres-migration-task/reviewed",
            FoundationOutputs = drift == "bucket"
                ? Outputs(applicationBucket: "unexpected-bucket")
                : Outputs(),
            FoundationParameters = drift == "database_engine_version"
                ? Parameters(engineVersion: "0.0")
                : Parameters(),
            CurrentDatabaseSecretVersionIds = drift == "database_secret_version"
                ? new HashSet<string>(StringComparer.Ordinal) { "other-database-version" }
                : new HashSet<string>(StringComparer.Ordinal) { "database-version" },
            CurrentDeploymentSecretVersionIds = drift == "deployment_secret_version"
                ? new HashSet<string>(StringComparer.Ordinal) { "other-deployment-version" }
                : new HashSet<string>(StringComparer.Ordinal) { "deployment-version" },
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            TargetMigrationAttestationValidator.Validate(Expectation(), snapshot));

        Assert.Equal($"Target migration attestation failed: {drift}.", error.Message);
        Assert.DoesNotContain("unexpected-bucket", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("version", error.Message.Replace(drift, String.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private static TargetMigrationAttestationExpectation Expectation() => new(
        "reviewed-bucket",
        "us-east-1",
        "mutualgpu-foundation",
        "mutualgpu-postgres",
        "arn:aws:secretsmanager:us-east-1:428590861908:secret:mutualgpu/deployment-review",
        "deployment-version",
        "database-version",
        "database.internal",
        5432,
        "mutualgpu");

    private static TargetMigrationAttestationSnapshot Snapshot() => new(
        TargetMigrationAttestationExpectation.TargetAccount,
        "arn:aws:sts::428590861908:assumed-role/mutualgpu-postgres-migration-task/reviewed",
        Outputs(),
        Parameters(),
        "mutualgpu-postgres",
        "postgres",
        "18.4",
        "database.internal",
        5432,
        "mutualgpu",
        "arn:aws:secretsmanager:us-east-1:428590861908:secret:mutualgpu/postgres-review",
        new HashSet<string>(StringComparer.Ordinal) { "database-version" },
        new HashSet<string>(StringComparer.Ordinal) { "deployment-version" });

    private static IReadOnlyDictionary<string, string> Outputs(string applicationBucket = "reviewed-bucket") =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ApplicationDataBucketName"] = applicationBucket,
            ["DatabaseEndpointAddress"] = "database.internal",
            ["DatabaseEndpointPort"] = "5432",
            ["DatabaseName"] = "mutualgpu",
            ["DatabaseSecretArn"] = "arn:aws:secretsmanager:us-east-1:428590861908:secret:mutualgpu/postgres-review",
        };

    private static IReadOnlyDictionary<string, string> Parameters(string engineVersion = "18.4") =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DatabaseEngineVersion"] = engineVersion,
        };
}
