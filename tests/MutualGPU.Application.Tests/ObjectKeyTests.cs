using MutualGPU.Domain;
using MutualGPU.Infrastructure;

namespace MutualGPU.Application.Tests;

public sealed class ObjectKeyTests
{
    [Fact]
    public void Provider_object_paths_are_hmac_derived_and_queue_markers_include_requested_resources()
    {
        var keys = new MutualGpuObjectKeys([1, 2, 3]);
        var identity = keys.NodeIdentity("provider-secret");
        var queue = keys.QueueMarker(
            CapabilityId.New(),
            ResourceTier.Medium,
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
            TaskId.New());

        Assert.DoesNotContain("provider-secret", identity.Value, StringComparison.Ordinal);
        Assert.StartsWith("mutualgpu/v3/nodes/", identity.Value, StringComparison.Ordinal);
        Assert.Contains("/queue/", queue.Value, StringComparison.Ordinal);
        Assert.Contains("/03-00008/", queue.Value, StringComparison.Ordinal);
    }
}
