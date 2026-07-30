using MutualGPU.Application;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;

var options = new PostgresOptions
{
    ConnectionString = Environment.GetEnvironmentVariable("MutualGPU__Postgres__ConnectionString"),
    Host = Environment.GetEnvironmentVariable("MutualGPU__Postgres__Host"),
    Port = Int32.TryParse(Environment.GetEnvironmentVariable("MutualGPU__Postgres__Port"), out var port) ? port : 5432,
    Database = Environment.GetEnvironmentVariable("MutualGPU__Postgres__Database") ?? "mutualgpu",
    Username = Environment.GetEnvironmentVariable("MutualGPU__Postgres__Username") ?? "mutualgpu",
    Password = Environment.GetEnvironmentVariable("MutualGPU__Postgres__Password"),
    RequireTls = Boolean.TryParse(Environment.GetEnvironmentVariable("MutualGPU__Postgres__RequireTls"), out var requireTls) && requireTls,
};

await using var dataSource = PostgresDataSourceFactory.Create(options);
await new PostgresMigrator(dataSource).MigrateAsync(CancellationToken.None);
var registry = new PostgresProviderCredentialRegistry(dataSource, new MutualGpuObjectKeys(), TimeProvider.System);
var issuer = new ProviderKeyIssuer(registry);

if (args is ["--count", var countValue] && Int32.TryParse(countValue, out var count) && count > 0)
{
    await issuer.IssueAsync(count, (issued, _) =>
    {
        // A raw key is returned once to the operator and is never retained by PostgreSQL.
        Console.WriteLine($"{issued.ExecutionUnitId.Value:D} {issued.PresharedKey}");
        return Task.CompletedTask;
    }, CancellationToken.None);
    return;
}

if (args is ["--bind-environment"])
{
    var executionUnitId = new ExecutionUnitId(Guid.Parse(RequiredEnvironmentVariable("MUTUALGPU_EXECUTION_UNIT_ID")));
    var presharedKey = RequiredEnvironmentVariable("MUTUALGPU_PROVIDER_KEY");
    await registry.ProvisionAsync(executionUnitId, presharedKey, CancellationToken.None);
    Console.WriteLine($"Bound execution unit {executionUnitId.Value:D}.");
    return;
}

throw new ArgumentException(
    "Usage: MutualGPU.ProviderKeyProvisioner --count <positive-integer> | --bind-environment");

static string RequiredEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name) switch
{
    { Length: > 0 } value => value,
    _ => throw new InvalidOperationException($"The {name} environment variable is required."),
};
