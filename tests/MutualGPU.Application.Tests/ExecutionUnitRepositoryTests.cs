using MutualGPU.Domain;
using MutualGPU.Infrastructure;

namespace MutualGPU.Application.Tests;

public sealed class ExecutionUnitRepositoryTests
{
    [Fact]
    public async Task Concurrent_identical_enrollments_share_one_capability_definition()
    {
        var store = new InMemoryObjectStore();
        var keys = new MutualGpuObjectKeys([1, 2, 3]);
        var firstId = ExecutionUnitId.New();
        var secondId = ExecutionUnitId.New();
        var resolver = new ConfiguredPresharedKeyRegistry(new Dictionary<ExecutionUnitId, string>
        {
            [firstId] = "first-key",
            [secondId] = "second-key",
        });
        var locks = new RepositoryLockRegistry();
        var repository = new ObjectStoreExecutionUnitRepository(store, keys, resolver, locks);
        var application = new EnrollmentApplication(repository, new Events(), locks);
        var definition = new CapabilityDefinition(
            CapabilityId.New(),
            "splats",
            [new InputDefinition("seed", CapabilityInputType.Integer, true, "Seed", Minimum: 1, Maximum: 10)],
            new OutputDefinition(),
            "ignored-by-canonicalization");

        var results = await Task.WhenAll(
            application.Enroll(new EnrollCommand(firstId, Machine(ResourceTier.Medium, ResourceTier.Medium, 16), [definition])).RunAsync(),
            application.Enroll(new EnrollCommand(secondId, Machine(ResourceTier.Medium, ResourceTier.Medium, 16), [definition with { Id = CapabilityId.New() }])).RunAsync());

        var enrolled = results.Select(Assert.IsType<EnrollResult.Enrolled>).ToArray();
        Assert.Equal(enrolled[0].Unit.CurrentEnrollment.Capabilities.Single().Id, enrolled[1].Unit.CurrentEnrollment.Capabilities.Single().Id);
        Assert.Single(await repository.GetCapabilitiesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Enrollment_replacements_append_immutable_events_before_advancing_the_identity_projection()
    {
        var store = new InMemoryObjectStore();
        var keys = new MutualGpuObjectKeys([1, 2, 3]);
        var id = ExecutionUnitId.New();
        const string presharedKey = "provider-key";
        var resolver = new ConfiguredPresharedKeyRegistry(new Dictionary<ExecutionUnitId, string> { [id] = presharedKey });
        var repository = new ObjectStoreExecutionUnitRepository(store, keys, resolver, new RepositoryLockRegistry());
        var first = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "first");
        var unit = new ExecutionUnit(id, new EnrollmentDefinition(Machine(ResourceTier.Small, ResourceTier.Small, 8), [first]));

        await repository.SaveAsync(unit, CancellationToken.None);
        unit.ReplaceEnrollment(new EnrollmentDefinition(
            Machine(ResourceTier.Large, ResourceTier.Large, 32),
            [new CapabilityDefinition(CapabilityId.New(), "splats-v2", [], new OutputDefinition(), "second")]));
        await repository.SaveAsync(unit, CancellationToken.None);

        var events = new List<ObjectEntry>();
        await foreach (var entry in store.ListAsync(keys.Enrollments(presharedKey), CancellationToken.None)) events.Add(entry);
        var hydrated = await repository.GetAsync(id, CancellationToken.None);

        Assert.Equal(2, events.Count);
        Assert.EndsWith("0000000001", Path.GetFileNameWithoutExtension(events[0].Key.Value).Split('-')[0], StringComparison.Ordinal);
        Assert.EndsWith("0000000002", Path.GetFileNameWithoutExtension(events[1].Key.Value).Split('-')[0], StringComparison.Ordinal);
        Assert.NotNull(hydrated);
        Assert.Equal(new EnrollmentVersion(2), hydrated!.Version);
        Assert.Equal(ResourceTier.Large, hydrated.CurrentEnrollment.Machine.Tier);
        Assert.Equal(32, hydrated.CurrentEnrollment.Machine.Specifications.MemoryGiB);
        Assert.Equal("splats-v2", Assert.Single(hydrated.CurrentEnrollment.Capabilities).Name);
    }

    [Fact]
    public async Task Startup_recovery_rebuilds_a_missing_identity_projection_from_the_latest_enrollment_event()
    {
        var store = new InMemoryObjectStore();
        var keys = new MutualGpuObjectKeys([1, 2, 3]);
        var id = ExecutionUnitId.New();
        const string presharedKey = "provider-key";
        var resolver = new ConfiguredPresharedKeyRegistry(new Dictionary<ExecutionUnitId, string> { [id] = presharedKey });
        var repository = new ObjectStoreExecutionUnitRepository(store, keys, resolver, new RepositoryLockRegistry());
        var unit = new ExecutionUnit(id, new EnrollmentDefinition(Machine(ResourceTier.Small, ResourceTier.Small, 8), [new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "first")]));
        await repository.SaveAsync(unit, CancellationToken.None);
        unit.ReplaceEnrollment(new EnrollmentDefinition(Machine(ResourceTier.Large, ResourceTier.Large, 32), [new CapabilityDefinition(CapabilityId.New(), "splats-v2", [], new OutputDefinition(), "second")]));
        await repository.SaveAsync(unit, CancellationToken.None);
        await store.DeleteAsync(keys.NodeIdentity(presharedKey), CancellationToken.None);

        var recovered = await repository.RecoverAsync(CancellationToken.None);
        var hydrated = await repository.GetAsync(id, CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.NotNull(hydrated);
        Assert.Equal(new EnrollmentVersion(2), hydrated!.Version);
        Assert.Equal("splats-v2", Assert.Single(hydrated.CurrentEnrollment.Capabilities).Name);
    }

    [Fact]
    public void Replaced_connection_lease_cannot_remove_the_newer_session()
    {
        var registry = new ProviderConnectionRegistry();
        var capability = new CapabilityDefinition(CapabilityId.New(), "splats", [], new OutputDefinition(), "hash");
        var unit = new ExecutionUnit(ExecutionUnitId.New(), new EnrollmentDefinition(Machine(ResourceTier.Medium, ResourceTier.Medium, 16), [capability]));

        var first = registry.Connect(unit);
        var second = registry.Connect(unit);

        Assert.False(registry.Disconnect(first));
        Assert.Single(registry.GetConnectedCandidates(capability.Id));
        Assert.True(registry.Disconnect(second));
        Assert.Empty(registry.GetConnectedCandidates(capability.Id));
    }

    [Fact]
    public void Result_upload_tokens_are_single_use_and_expire_after_fifteen_minutes()
    {
        var authorizations = new ResultUploadAuthorizations();
        var unit = ExecutionUnitId.New();
        var task = TaskId.New();
        var attempt = AttemptId.New();
        var issuedAt = DateTimeOffset.UtcNow;
        var token = authorizations.Issue(unit, task, attempt, "handle", issuedAt);

        Assert.True(authorizations.TryConsume(unit, task, attempt, "handle", token.Token, issuedAt.AddMinutes(15)));
        Assert.False(authorizations.TryConsume(unit, task, attempt, "handle", token.Token, issuedAt.AddMinutes(15)));
        var expired = authorizations.Issue(unit, task, attempt, "handle", issuedAt);
        Assert.False(authorizations.TryConsume(unit, task, attempt, "handle", expired.Token, issuedAt.AddMinutes(15).AddTicks(1)));
    }

    private sealed class Events : IApplicationEventSink
    {
        public void TriggerScheduler() { }
    }

    private static MachineProfile Machine(ResourceTier tier, ResourceTier computeTier, int memoryGiB) =>
        new(tier, new MachineSpecifications(computeTier, memoryGiB));
}
