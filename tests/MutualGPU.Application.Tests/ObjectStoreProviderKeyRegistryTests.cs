using MutualGPU.Application;
using MutualGPU.Infrastructure;

namespace MutualGPU.Application.Tests;

public sealed class ObjectStoreProviderKeyRegistryTests
{
    [Fact]
    public async Task Issued_key_is_authenticated_from_its_sha256_addressed_object_record()
    {
        var store = new InMemoryObjectStore();
        var registry = new ObjectStoreProviderKeyRegistry(store, new MutualGpuObjectKeys());
        var issued = Assert.Single(await new ProviderKeyIssuer(registry).IssueAsync(1, CancellationToken.None));

        var authenticated = await registry.AuthenticateAsync(issued.PresharedKey, CancellationToken.None);
        var unknown = await registry.AuthenticateAsync("not-a-provider-key", CancellationToken.None);
        var provisioned = new List<MutualGPU.Domain.ExecutionUnitId>();
        var objects = new List<ObjectEntry>();
        await foreach (var id in registry.ListExecutionUnitIdsAsync(CancellationToken.None)) provisioned.Add(id);
        await foreach (var entry in store.ListAsync(new ObjectPrefix("mutualgpu/v3/provider-keys"), CancellationToken.None)) objects.Add(entry);

        Assert.Equal(issued.ExecutionUnitId, authenticated);
        Assert.Null(unknown);
        Assert.Equal([issued.ExecutionUnitId], provisioned);
        Assert.DoesNotContain(issued.PresharedKey, String.Join('\n', objects.Select(static entry => entry.Key.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_callback_observes_each_key_after_its_record_is_durable()
    {
        var store = new InMemoryObjectStore();
        var registry = new ObjectStoreProviderKeyRegistry(store, new MutualGpuObjectKeys());
        var observed = new List<IssuedProviderKey>();

        await new ProviderKeyIssuer(registry).IssueAsync(2, async (issued, cancellationToken) =>
        {
            observed.Add(issued);
            Assert.Equal(issued.ExecutionUnitId, await registry.AuthenticateAsync(issued.PresharedKey, cancellationToken));
        }, CancellationToken.None);

        Assert.Equal(2, observed.Count);
    }
}
