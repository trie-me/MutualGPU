using MutualGPU.Application;
using MutualGPU.Infrastructure;

var count = ParseCount(args);
var options = new AwsS3ProviderKeyRegistryOptions(
    RequiredEnvironmentVariable("MutualGPU__ProviderKeyS3__BucketName"),
    Environment.GetEnvironmentVariable("MutualGPU__ProviderKeyS3__Region") ?? "us-east-1");

var registry = new AwsS3ProviderKeyRegistry(options, new MutualGpuObjectKeys());
var issuer = new ProviderKeyIssuer(registry);

await issuer.IssueAsync(count, (issued, _) =>
{
    Console.WriteLine($"{issued.ExecutionUnitId.Value:D} {issued.PresharedKey}");
    return Task.CompletedTask;
}, CancellationToken.None);

static int ParseCount(string[] arguments)
{
    if (arguments.Length != 2 || !StringComparer.Ordinal.Equals(arguments[0], "--count") || !Int32.TryParse(arguments[1], out var count) || count <= 0)
    {
        throw new ArgumentException("Usage: MutualGPU.ProviderKeyProvisioner --count <positive-integer>");
    }

    return count;
}

static string RequiredEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name) switch
{
    { Length: > 0 } value => value,
    _ => throw new InvalidOperationException($"The {name} environment variable is required."),
};
