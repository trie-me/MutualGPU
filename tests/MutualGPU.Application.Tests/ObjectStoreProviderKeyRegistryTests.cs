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

    [Fact]
    public async Task Authentication_caches_the_digest_needed_to_load_the_execution_unit()
    {
        var inner = new InMemoryObjectStore();
        var keys = new MutualGpuObjectKeys();
        var provisioner = new ObjectStoreProviderKeyRegistry(inner, keys);
        var executionUnitId = MutualGPU.Domain.ExecutionUnitId.New();
        const string providerKey = "provider-key";
        await provisioner.ProvisionAsync(executionUnitId, providerKey, CancellationToken.None);
        var store = new CountingObjectStore(inner);
        var registry = new ObjectStoreProviderKeyRegistry(store, keys);

        Assert.Equal(executionUnitId, await registry.AuthenticateAsync(providerKey, CancellationToken.None));
        Assert.Equal(keys.ProviderDigest(providerKey), await registry.GetProviderKeyDigestAsync(executionUnitId, CancellationToken.None));
        Assert.Equal(0, store.ListCount);
    }

    private sealed class CountingObjectStore(IObjectStore inner) : IObjectStore
    {
        public int ListCount { get; private set; }

        public Task<ObjectRead?> GetAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetAsync(key, cancellationToken);

        public Task PutAsync(ObjectKey key, Stream content, ObjectWriteConditions conditions, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, conditions, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, cancellationToken);

        public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, CancellationToken cancellationToken)
        {
            ListCount++;
            return inner.ListAsync(prefix, cancellationToken);
        }

        public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            inner.CreateDownloadUrlAsync(key, lifetime, cancellationToken);
    }
}
