using MutualGPU.Application;

namespace MutualGPU.Infrastructure;

public enum ObjectStorageProvider
{
    AwsS3,
    BackblazeB2,
}

/// <summary>
/// B2 application keys restricted to a bucket cannot manage bucket CORS. Make
/// the CORS authority explicit instead of widening the artifact data key.
/// </summary>
public enum BackblazeB2BrowserCorsMode
{
    ExternallyManaged,
    ManagedByApplication,
}

/// <summary>Explicit opt-in configuration for durable artifact targets.</summary>
public sealed class ObjectStorageOptions
{
    public string? WriteTarget { get; init; }

    public Dictionary<string, ObjectStorageTargetOptions> Targets { get; init; } = new(StringComparer.Ordinal);

    public void Validate()
    {
        if (String.IsNullOrWhiteSpace(WriteTarget))
        {
            throw new InvalidOperationException("MutualGPU:ObjectStorage:WriteTarget is required.");
        }
        if (Targets.Count == 0 || !Targets.ContainsKey(WriteTarget))
        {
            throw new InvalidOperationException("MutualGPU:ObjectStorage:WriteTarget must name an explicitly configured target.");
        }
        foreach (var (id, target) in Targets)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Object-storage target IDs must be non-empty.");
            target.Validate(id);
        }
    }
}

public sealed class ObjectStorageTargetOptions
{
    public ObjectStorageProvider? Provider { get; init; }

    public string? BucketName { get; init; }

    public string? Region { get; init; }

    public string? Profile { get; init; }

    public string? Endpoint { get; init; }

    public string? AccessKeyId { get; init; }

    public string? SecretAccessKey { get; init; }

    public BackblazeB2BrowserCorsMode? BrowserCorsMode { get; init; }

    public string? CorsAccessKeyId { get; init; }

    public string? CorsSecretAccessKey { get; init; }

    public bool ForcePathStyle { get; init; } = true;

    internal void Validate(string id)
    {
        if (Provider is null || String.IsNullOrWhiteSpace(BucketName) || String.IsNullOrWhiteSpace(Region))
        {
            throw new InvalidOperationException($"Object-storage target '{id}' requires BucketName and Region.");
        }
        if (Provider is ObjectStorageProvider.BackblazeB2 &&
            (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
             !StringComparer.OrdinalIgnoreCase.Equals(endpoint.Scheme, Uri.UriSchemeHttps) ||
             !String.IsNullOrEmpty(endpoint.Query) ||
             !String.IsNullOrEmpty(endpoint.Fragment) ||
             String.IsNullOrWhiteSpace(AccessKeyId) ||
             String.IsNullOrWhiteSpace(SecretAccessKey)))
        {
            throw new InvalidOperationException($"Backblaze target '{id}' requires an HTTPS endpoint and explicit application-key credentials.");
        }
        if (Provider is ObjectStorageProvider.BackblazeB2)
        {
            if (!String.IsNullOrWhiteSpace(Profile))
            {
                throw new InvalidOperationException($"Backblaze target '{id}' does not accept an AWS Profile.");
            }
            if (BrowserCorsMode is null)
            {
                throw new InvalidOperationException($"Backblaze target '{id}' requires an explicit BrowserCorsMode.");
            }
            if (BrowserCorsMode is BackblazeB2BrowserCorsMode.ManagedByApplication &&
                (String.IsNullOrWhiteSpace(CorsAccessKeyId) || String.IsNullOrWhiteSpace(CorsSecretAccessKey)))
            {
                throw new InvalidOperationException($"Backblaze target '{id}' requires separate CORS-management credentials when BrowserCorsMode is ManagedByApplication.");
            }
            if (BrowserCorsMode is BackblazeB2BrowserCorsMode.ManagedByApplication &&
                StringComparer.Ordinal.Equals(AccessKeyId, CorsAccessKeyId))
            {
                throw new InvalidOperationException($"Backblaze target '{id}' must use a distinct CORS-management key ID when BrowserCorsMode is ManagedByApplication.");
            }
            if (BrowserCorsMode is BackblazeB2BrowserCorsMode.ExternallyManaged &&
                (!String.IsNullOrWhiteSpace(CorsAccessKeyId) || !String.IsNullOrWhiteSpace(CorsSecretAccessKey)))
            {
                throw new InvalidOperationException($"Backblaze target '{id}' must not supply CORS-management credentials when BrowserCorsMode is ExternallyManaged.");
            }
            new BackblazeB2ObjectStoreOptions(
                BucketName!,
                Endpoint!,
                Region!,
                AccessKeyId!,
                SecretAccessKey!,
                ForcePathStyle).Validate();
        }
        if (Provider is ObjectStorageProvider.AwsS3 && !String.IsNullOrWhiteSpace(Endpoint))
        {
            throw new InvalidOperationException($"AWS target '{id}' does not accept an endpoint override.");
        }
        if (Provider is ObjectStorageProvider.AwsS3 &&
            (!String.IsNullOrWhiteSpace(AccessKeyId) || !String.IsNullOrWhiteSpace(SecretAccessKey) ||
             BrowserCorsMode is not null || !String.IsNullOrWhiteSpace(CorsAccessKeyId) || !String.IsNullOrWhiteSpace(CorsSecretAccessKey)))
        {
            throw new InvalidOperationException($"AWS target '{id}' must not include Backblaze credentials or CORS settings.");
        }
    }
}

public sealed class ObjectStoreRegistry : IObjectStoreRegistry, IDisposable
{
    private readonly IReadOnlyDictionary<string, Lazy<ObjectStoreTarget>> targets;
    private readonly bool ownsTargets;
    private int disposed;

    public ObjectStoreRegistry(string writeTargetId, IEnumerable<ObjectStoreTarget> targets, bool ownsTargets = true)
        : this(
            writeTargetId,
            targets?.Select(static target => new KeyValuePair<string, Func<ObjectStoreTarget>>(target.Id, () => target))
                ?? throw new ArgumentNullException(nameof(targets)),
            ownsTargets) { }

    private ObjectStoreRegistry(
        string writeTargetId,
        IEnumerable<KeyValuePair<string, Func<ObjectStoreTarget>>> targetFactories,
        bool ownsTargets)
    {
        if (String.IsNullOrWhiteSpace(writeTargetId)) throw new ArgumentException("A write target is required.", nameof(writeTargetId));
        this.targets = targetFactories?.ToDictionary(
            static target => target.Key,
            static target => new Lazy<ObjectStoreTarget>(target.Value, LazyThreadSafetyMode.ExecutionAndPublication),
            StringComparer.Ordinal) ?? throw new ArgumentNullException(nameof(targetFactories));
        if (!this.targets.ContainsKey(writeTargetId))
        {
            throw new InvalidOperationException("The selected object-storage write target is not configured.");
        }
        WriteTargetId = writeTargetId;
        this.ownsTargets = ownsTargets;
    }

    public static ObjectStoreRegistry CreateLazy(
        string writeTargetId,
        IEnumerable<KeyValuePair<string, Func<ObjectStoreTarget>>> targetFactories) =>
        new(writeTargetId, targetFactories, ownsTargets: true);

    public string WriteTargetId { get; }

    public ObjectStoreTarget GetRequired(string storageTargetId)
    {
        if (String.IsNullOrWhiteSpace(storageTargetId) || !targets.TryGetValue(storageTargetId, out var target))
        {
            throw new InvalidOperationException($"Object-storage target '{storageTargetId}' is not configured.");
        }
        return target.Value;
    }

    public bool IsConfigured(string storageTargetId) =>
        !String.IsNullOrWhiteSpace(storageTargetId) && targets.ContainsKey(storageTargetId);

    public void Dispose()
    {
        if (!ownsTargets || Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var disposable in targets.Values
                     .Where(static target => target.IsValueCreated)
                     .Select(static target => target.Value)
                     .SelectMany(static target => new object[] { target.Store, target.BrowserCors })
                     .Distinct(ReferenceEqualityComparer.Instance)
                     .OfType<IDisposable>())
        {
            disposable.Dispose();
        }
    }
}

/// <summary>
/// Non-owning façade for the configured write target. It keeps the registry as
/// the sole disposable owner of provider clients while preserving optional
/// native write receipts for API callers.
/// </summary>
public sealed class WriteTargetObjectStoreFacade(IObjectStoreRegistry stores)
    : IObjectStore, IObjectStoreHealth, IObjectStoreWriteReceipts
{
    private IObjectStore Store => stores.GetRequired(stores.WriteTargetId).Store;

    public Task<ObjectRead?> GetAsync(ObjectKey key, CancellationToken cancellationToken) =>
        Store.GetAsync(key, cancellationToken);

    public Task PutAsync(
        ObjectKey key,
        Stream content,
        ObjectWriteConditions conditions,
        CancellationToken cancellationToken) =>
        Store.PutAsync(key, content, conditions, cancellationToken);

    public async Task<ObjectWriteReceipt> PutWithReceiptAsync(
        ObjectKey key,
        Stream content,
        ObjectWriteConditions conditions,
        CancellationToken cancellationToken)
    {
        if (Store is IObjectStoreWriteReceipts receipts)
        {
            return await receipts.PutWithReceiptAsync(key, content, conditions, cancellationToken).ConfigureAwait(false);
        }
        await Store.PutAsync(key, content, conditions, cancellationToken).ConfigureAwait(false);
        return new ObjectWriteReceipt(null);
    }

    public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
        Store.DeleteAsync(key, cancellationToken);

    public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, CancellationToken cancellationToken) =>
        Store.ListAsync(prefix, cancellationToken);

    public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken) =>
        Store.CreateDownloadUrlAsync(key, lifetime, cancellationToken);

    public Task CheckHealthAsync(CancellationToken cancellationToken) =>
        stores.GetRequired(stores.WriteTargetId).Health.CheckHealthAsync(cancellationToken);
}

/// <summary>Synchronizes only the selected write target at startup. Other targets
/// are contacted only when an authorized artifact resolves to them.</summary>
public sealed class WriteTargetBrowserObjectCorsPolicy(IObjectStoreRegistry stores) : IBrowserObjectCorsPolicy
{
    public Task SynchronizeAsync(IReadOnlyCollection<string> allowedOrigins, CancellationToken cancellationToken) =>
        stores.GetRequired(stores.WriteTargetId).BrowserCors.SynchronizeAsync(allowedOrigins, cancellationToken);
}
