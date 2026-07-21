using MutualGPU.Contracts;
using MutualGPU.Domain;

namespace MutualGPU.Application.Tests;

public sealed class ResourceMatrixProjectionTests
{
    [Fact]
    public void Capacity_view_places_memory_on_x_axis_and_compute_on_descending_y_axis()
    {
        var matrix = ResourceMatrixProjection.Create(
        [
            new(ResourceTier.Small, 32, 1, 1),
            new(ResourceTier.Large, 8, 2, 0),
            new(ResourceTier.Large, 32, 1, 1),
        ]);

        Assert.Equal([8, 32], matrix.MemoryGiBAxis);
        Assert.Equal([ResourceTier.Large, ResourceTier.Small], matrix.ComputeAxis.Select(static row => row.ComputeTier));
        Assert.Equal(8, matrix.ComputeAxis[0].Cells[0].MemoryGiB);
        Assert.Equal(0, matrix.ComputeAxis[0].Cells[0].IdleCount);
    }
}
