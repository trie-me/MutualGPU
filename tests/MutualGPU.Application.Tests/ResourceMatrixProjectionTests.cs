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
            new(ResourceTier.Small, ResourceTier.Large, 1, 1),
            new(ResourceTier.Large, ResourceTier.Small, 2, 0),
            new(ResourceTier.Large, ResourceTier.Large, 1, 1),
        ]);

        Assert.Equal([ResourceTier.Small, ResourceTier.Large], matrix.MemoryAxis);
        Assert.Equal([ResourceTier.Large, ResourceTier.Small], matrix.ComputeAxis.Select(static row => row.Compute));
        Assert.Equal(ResourceTier.Small, matrix.ComputeAxis[0].Cells[0].Memory);
        Assert.Equal(0, matrix.ComputeAxis[0].Cells[0].IdleCount);
    }
}
