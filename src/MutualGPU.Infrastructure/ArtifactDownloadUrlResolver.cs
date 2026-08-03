using MutualGPU.Application;
using MutualGPU.Domain;

namespace MutualGPU.Infrastructure;

public sealed class ArtifactDownloadUrlResolver : IArtifactDownloadUrlResolver
{
    private readonly IOperationUnitOfWork? operations;
    private readonly IObjectStoreRegistry stores;
    private readonly IObjectStore fallbackStore;

    public ArtifactDownloadUrlResolver(IOperationUnitOfWork? operations, IObjectStoreRegistry stores)
        : this(operations, stores, stores?.GetRequired(stores.WriteTargetId).Store ?? throw new ArgumentNullException(nameof(stores))) { }

    public ArtifactDownloadUrlResolver(
        IOperationUnitOfWork? operations,
        IObjectStoreRegistry stores,
        IObjectStore fallbackStore)
    {
        this.operations = operations;
        this.stores = stores ?? throw new ArgumentNullException(nameof(stores));
        this.fallbackStore = fallbackStore ?? throw new ArgumentNullException(nameof(fallbackStore));
    }

    public async Task<Uri> CreateDownloadUrlAsync(
        TaskId taskId,
        ArtifactId artifactId,
        ObjectKey legacyKey,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (operations is null)
        {
            return await fallbackStore
                .CreateDownloadUrlAsync(legacyKey, lifetime, cancellationToken).ConfigureAwait(false);
        }

        return await operations.ExecuteAsync(
            async (context, token) =>
            {
                var artifact = (await context.Artifacts.GetForTaskAsync(taskId, token).ConfigureAwait(false))
                    .SingleOrDefault(item => item.Id == artifactId)
                    ?? throw new InvalidOperationException("The requested artifact is unavailable.");
                var locations = artifact.Locations?
                    .Where(item => item.State is ArtifactLocationState.Available)
                    .ToArray() ?? [];
                if (locations.Length != 1)
                {
                    throw new InvalidOperationException(locations.Length == 0
                        ? "The requested artifact has no available storage location."
                        : "The requested artifact has multiple available storage locations without a selection policy.");
                }
                var location = locations[0];
                return await stores.GetRequired(location.StorageTargetId).Store
                    .CreateDownloadUrlAsync(new ObjectKey(location.ObjectKey), lifetime, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
