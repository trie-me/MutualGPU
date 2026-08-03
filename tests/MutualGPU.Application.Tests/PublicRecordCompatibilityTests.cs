using MutualGPU.Application;
using MutualGPU.Domain;

namespace MutualGPU.Application.Tests;

public sealed class PublicRecordCompatibilityTests
{
    [Fact]
    public void Provider_etag_metadata_does_not_expand_existing_positional_contracts()
    {
        var parameters = new TaskParameters(new Dictionary<string, string>(), null)
        {
            ImageProviderETag = "input-etag",
        };
        parameters.Deconstruct(
            out _, out _, out _, out _, out _, out _, out _, out _, out _);

        var artifact = new ResultArtifact(ArtifactId.New(), "application/zip", 1, "digest")
        {
            ProviderETag = "result-etag",
        };
        artifact.Deconstruct(out _, out _, out _, out _);

        var command = new SubmitTaskCommand(
            RequestorId.New(),
            CapabilityId.New(),
            "contract",
            new Dictionary<string, string>(),
            null,
            new MachineSpecifications(ResourceTier.Small, 8),
            DateTimeOffset.UtcNow)
        {
            ImageProviderETag = "input-etag",
        };
        command.Deconstruct(
            out _, out _, out _, out _, out _,
            out _, out _, out _, out _, out _,
            out _, out _, out _, out _, out _);
    }
}
