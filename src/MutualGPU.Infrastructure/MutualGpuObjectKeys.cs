using System.Security.Cryptography;
using System.Text;
using MutualGPU.Application;
using MutualGPU.Domain;

namespace MutualGPU.Infrastructure;

/// <summary>Centralizes the versioned Backblaze layout and prevents raw provider keys reaching object names.</summary>
public sealed class MutualGpuObjectKeys(byte[] providerKeyPepper)
{
    private const string Root = "mutualgpu/v3";
    private readonly byte[] providerKeyPepper = providerKeyPepper?.ToArray() ?? throw new ArgumentNullException(nameof(providerKeyPepper));

    public ObjectKey CapabilityDefinition(CapabilityId capabilityId) => new($"{Root}/capabilities/{capabilityId.Value:N}/definition.json");

    public ObjectPrefix Capabilities() => new($"{Root}/capabilities");

    public ObjectKey NodeIdentity(string presharedKey) => new($"{Root}/nodes/{ProviderDigest(presharedKey)}/identity.json");

    public ObjectKey Enrollment( string presharedKey, EnrollmentVersion version, EnrollmentEventId eventId) =>
        new($"{Root}/nodes/{ProviderDigest(presharedKey)}/enrollments/{version.Value:D10}-{eventId.Value:N}.json");

    public ObjectPrefix Enrollments(string presharedKey) =>
        new($"{Root}/nodes/{ProviderDigest(presharedKey)}/enrollments");

    public ObjectKey TaskManifest(RequestorId requestorId, TaskId taskId) =>
        new($"{Root}/requestors/{requestorId.Value:N}/tasks/{taskId.Value:N}/manifest.json");

    public ObjectPrefix TaskFacts(RequestorId requestorId, TaskId taskId) =>
        new($"{Root}/requestors/{requestorId.Value:N}/tasks/{taskId.Value:N}/facts");

    public ObjectKey TaskFact(RequestorId requestorId, TaskId taskId, DateTimeOffset committedAt, Guid operationId) =>
        new($"{Root}/requestors/{requestorId.Value:N}/tasks/{taskId.Value:N}/facts/{committedAt.UtcTicks:D19}-{operationId:N}.json");

    public ObjectPrefix RequestorTasks(RequestorId requestorId) => new($"{Root}/requestors/{requestorId.Value:N}/tasks");

    public ObjectKey TaskSummaryProjection(RequestorId requestorId) =>
        new($"{Root}/requestors/{requestorId.Value:N}/projections/task-summaries.json");

    public ObjectKey TaskInput(RequestorId requestorId, TaskId taskId, ArtifactId artifactId, string extension) =>
        new($"{Root}/requestors/{requestorId.Value:N}/tasks/{taskId.Value:N}/inputs/{artifactId.Value:N}.{SafeExtension(extension)}");

    public ObjectPrefix TaskInputs(RequestorId requestorId, TaskId taskId) =>
        new($"{Root}/requestors/{requestorId.Value:N}/tasks/{taskId.Value:N}/inputs");

    public ObjectKey AttemptEvent(RequestorId requestorId, TaskId taskId, AttemptId attemptId, int sequence, string name) =>
        new($"{Root}/requestors/{requestorId.Value:N}/tasks/{taskId.Value:N}/attempts/{attemptId.Value:N}/events/{sequence:D4}-{SafeName(name)}.json");

    public ObjectPrefix AttemptEvents(RequestorId requestorId, TaskId taskId, AttemptId attemptId) =>
        new($"{Root}/requestors/{requestorId.Value:N}/tasks/{taskId.Value:N}/attempts/{attemptId.Value:N}/events");

    public ObjectKey ResultZip(RequestorId requestorId, TaskId taskId, AttemptId attemptId) =>
        new($"{Root}/requestors/{requestorId.Value:N}/tasks/{taskId.Value:N}/results/{attemptId.Value:N}/result.zip");

    public ObjectKey ResultMetadata(RequestorId requestorId, TaskId taskId, AttemptId attemptId) =>
        new($"{Root}/requestors/{requestorId.Value:N}/tasks/{taskId.Value:N}/results/{attemptId.Value:N}/metadata.json");

    public ObjectKey ResultThumbnail(RequestorId requestorId, TaskId taskId, AttemptId attemptId, string extension) =>
        new($"{Root}/requestors/{requestorId.Value:N}/tasks/{taskId.Value:N}/results/{attemptId.Value:N}/thumbnail.{SafeExtension(extension)}");

    public ObjectKey ResultPreview(RequestorId requestorId, TaskId taskId, AttemptId attemptId, string extension) =>
        new($"{Root}/requestors/{requestorId.Value:N}/tasks/{taskId.Value:N}/results/{attemptId.Value:N}/preview.{SafeExtension(extension)}");

    public ObjectKey ResultLogs(RequestorId requestorId, TaskId taskId, AttemptId attemptId) =>
        new($"{Root}/requestors/{requestorId.Value:N}/tasks/{taskId.Value:N}/results/{attemptId.Value:N}/logs.txt");

    public ObjectKey QueueMarker(CapabilityId capabilityId, MachineSpecifications resources, DateTimeOffset createdAt, TaskId taskId) =>
        new($"{Root}/queue/{capabilityId.Value:N}/{(int)resources.ComputeTier:D2}-{resources.MemoryGiB:D5}/{createdAt.UtcTicks:D19}-{taskId.Value:N}.json");

    public ObjectKey QueueMarker(CapabilityId capabilityId, ResourceTier tier, DateTimeOffset createdAt, TaskId taskId) =>
        QueueMarker(capabilityId, new MachineSpecifications(tier is ResourceTier.Automatic ? ResourceTier.Small : tier, MachineSpecificationsPolicy.MinimumMemoryGiB), createdAt, taskId);

    public ObjectKey Commit(Guid operationId) => new($"{Root}/commits/{operationId:N}.json");

    public ObjectPrefix QueuePrefix(CapabilityId capabilityId) => new($"{Root}/queue/{capabilityId.Value:N}");

    private string ProviderDigest(string presharedKey)
    {
        if (String.IsNullOrWhiteSpace(presharedKey))
        {
            throw new ArgumentException("A preshared key is required.", nameof(presharedKey));
        }

        return Convert.ToHexString(HMACSHA256.HashData(providerKeyPepper, Encoding.UTF8.GetBytes(presharedKey))).ToLowerInvariant();
    }

    private static string SafeExtension(string extension) => SafeName(extension.TrimStart('.'));

    private static string SafeName(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Object-key name segments must be non-empty ASCII letters, digits, hyphens, or underscores.", nameof(value));
        }

        return value;
    }
}
