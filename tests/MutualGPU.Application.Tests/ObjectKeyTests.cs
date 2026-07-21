using MutualGPU.Domain;
using MutualGPU.Infrastructure;

namespace MutualGPU.Application.Tests;

public sealed class ObjectKeyTests
{
    [Fact]
    public void Provider_object_paths_are_sha256_derived_and_queue_markers_include_requested_resources()
    {
        var keys = new MutualGpuObjectKeys();
        var identity = keys.NodeIdentity("provider-secret");
        var queue = keys.QueueMarker(
            CapabilityId.New(),
            ResourceTier.Medium,
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
            TaskId.New());

        Assert.DoesNotContain("provider-secret", identity.Value, StringComparison.Ordinal);
        Assert.Equal(
            "mutualgpu/v3/nodes/e65f74547fff782068bf662c47916a35446bb720776a3fc22b07482b82c95552/identity.json",
            identity.Value);
        Assert.StartsWith("mutualgpu/v3/nodes/", identity.Value, StringComparison.Ordinal);
        Assert.Contains("/queue/", queue.Value, StringComparison.Ordinal);
        Assert.Contains("/03-00008/", queue.Value, StringComparison.Ordinal);
    }
}
