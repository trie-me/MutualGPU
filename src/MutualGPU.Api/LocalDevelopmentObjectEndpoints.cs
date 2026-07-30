using MutualGPU.Application;
using MutualGPU.Infrastructure;

namespace MutualGPU.Api;

/// <summary>
/// Serves the local in-memory artifact store only when the development composition
/// explicitly configures a same-origin download base URI. This makes the browser
/// integration harness fully local while keeping production on S3 presigned URLs.
/// </summary>
public static class LocalDevelopmentObjectEndpoints
{
    public static async Task<IResult> Download(string key, InMemoryObjectStore store, CancellationToken cancellationToken)
    {
        ObjectKey objectKey;
        try { objectKey = new ObjectKey(key); }
        catch (ArgumentException) { return TypedResults.NotFound(); }

        await using var read = await store.GetAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (read is null) return TypedResults.NotFound();
        await using var buffer = new MemoryStream((int)Math.Min(read.Length, 64L * 1024 * 1024));
        await read.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return TypedResults.File(buffer.ToArray(), read.ContentType ?? "application/octet-stream");
    }
}
