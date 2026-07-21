using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MutualGPU.Application;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;

namespace MutualGPU.Api;

public static class ProviderResultEndpoints
{
    private const int MaximumResultBytes = 50 * 1024 * 1024;
    private const int MaximumImageBytes = 5 * 1024 * 1024;
    private const int MaximumMetadataBytes = 64 * 1024;
    private const int MaximumLogBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async Task<IResult> IssueToken(Guid taskId, Guid attemptId, HttpContext context, IExecutionUnitAuthenticator authenticator, IProviderAssignments assignments, IResultUploadAuthorizations authorizations)
    {
        var task = new TaskId(taskId);
        var attempt = new AttemptId(attemptId);
        if ((await AuthenticateAsync(authenticator, context).ConfigureAwait(false)) is not { } unitId || !TryHandle(context, out var handle) ||
            !assignments.TryGet(unitId, task, attempt, handle, out var request) || !IsAccepted(request, attempt)) return TypedResults.Unauthorized();
        var token = authorizations.Issue(unitId, task, attempt, handle, DateTimeOffset.UtcNow);
        return TypedResults.Ok(token);
    }

    public static async Task<IResult> Upload(Guid taskId, Guid attemptId, HttpContext context, IExecutionUnitAuthenticator authenticator, IProviderAssignments assignments, IResultUploadAuthorizations authorizations, IStagedResults staged, IObjectStore store, MutualGpuObjectKeys keys, ProviderSessionApplication session, MutualGpuTelemetry telemetry, CancellationToken cancellationToken)
    {
        using var activity = telemetry.Activities.StartActivity("mutualgpu.provider.result_upload");
        var taskKey = new TaskId(taskId);
        var attemptKey = new AttemptId(attemptId);
        if ((await AuthenticateAsync(authenticator, context).ConfigureAwait(false)) is not { } unitId || !TryHandle(context, out var handle) || !context.Request.Headers.TryGetValue("X-MutualGPU-Upload-Token", out var token) ||
            !assignments.TryGet(unitId, taskKey, attemptKey, handle, out var task) || !IsAccepted(task, attemptKey)) return TypedResults.Unauthorized();
        if (!authorizations.TryConsume(unitId, taskKey, attemptKey, handle, token!, DateTimeOffset.UtcNow)) return TypedResults.Conflict(new { code = "upload_token_invalid" });
        if (!context.Request.HasFormContentType) return await ResultValidationFailedAsync(session, unitId, taskKey, attemptKey, handle, "multipart_required", cancellationToken).ConfigureAwait(false);
        IFormCollection form;
        try { form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException) { return await ResultValidationFailedAsync(session, unitId, taskKey, attemptKey, handle, "result_parts_invalid", cancellationToken).ConfigureAwait(false); }
        if (!TryGetParts(form, out var parts)) return await ResultValidationFailedAsync(session, unitId, taskKey, attemptKey, handle, "result_parts_invalid", cancellationToken).ConfigureAwait(false);
        if (!parts.TryGetValue("result", out var result) || result.Length < 4) return await ResultValidationFailedAsync(session, unitId, taskKey, attemptKey, handle, "result_zip_required", cancellationToken).ConfigureAwait(false);
        if (!TryGetMetadata(form, parts, out var metadataFile, out var metadataValue)) return await ResultValidationFailedAsync(session, unitId, taskKey, attemptKey, handle, "metadata_parts_invalid", cancellationToken).ConfigureAwait(false);

        var output = task.Capability.Output;
        if (parts.ContainsKey("thumbnail") && !output.HasThumbnail ||
            parts.ContainsKey("preview") && !output.HasPreview ||
            parts.ContainsKey("logs") && !output.HasLogs ||
            (metadataFile is not null || metadataValue is not null) && !output.HasMetadata)
            return await ResultValidationFailedAsync(session, unitId, taskKey, attemptKey, handle, "result_part_undeclared", cancellationToken).ConfigureAwait(false);

        byte[] resultBytes;
        try { resultBytes = await ReadBytesAsync(result, MaximumResultBytes, cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException) { return await ResultValidationFailedAsync(session, unitId, taskKey, attemptKey, handle, "result_zip_too_large", cancellationToken).ConfigureAwait(false); }
        if (!IsZip(resultBytes)) return await ResultValidationFailedAsync(session, unitId, taskKey, attemptKey, handle, "result_zip_invalid", cancellationToken).ConfigureAwait(false);
        var digest = Sha256(resultBytes);
        if (!context.Request.Headers.TryGetValue("X-MutualGPU-Sha256", out var supplied) || !StringComparer.OrdinalIgnoreCase.Equals(digest, supplied!)) return await ResultValidationFailedAsync(session, unitId, taskKey, attemptKey, handle, "result_checksum_invalid", cancellationToken).ConfigureAwait(false);

        ResultArtifact? thumbnail;
        ResultArtifact? preview;
        ResultArtifact? logs;
        ResultArtifact? metadata;
        try
        {
            thumbnail = await StoreImageAsync(parts.GetValueOrDefault("thumbnail"), store, keys.ResultThumbnail, task.RequestorId, task.Id, new AttemptId(attemptId), cancellationToken).ConfigureAwait(false);
            preview = await StorePreviewAsync(parts.GetValueOrDefault("preview"), output, store, keys, task.RequestorId, task.Id, new AttemptId(attemptId), cancellationToken).ConfigureAwait(false);
            logs = await StoreLogsAsync(parts.GetValueOrDefault("logs"), store, keys, task.RequestorId, task.Id, new AttemptId(attemptId), cancellationToken).ConfigureAwait(false);
            metadata = await StoreMetadataAsync(metadataFile, metadataValue, store, keys, task.RequestorId, task.Id, new AttemptId(attemptId), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            return await ResultValidationFailedAsync(session, unitId, taskKey, attemptKey, handle, exception.Message, cancellationToken).ConfigureAwait(false);
        }

        var zip = await StoreAsync(resultBytes, "application/zip", keys.ResultZip(task.RequestorId, task.Id, new AttemptId(attemptId)), store, cancellationToken).ConfigureAwait(false);
        var receipt = Guid.CreateVersion7().ToString("N");
        staged.Stage(unitId, task.Id, new AttemptId(attemptId), handle, new StagedResult(receipt, new TaskResult(zip, thumbnail, preview, logs, metadata)));
        telemetry.UploadCompleted(zip.Length + (thumbnail?.Length ?? 0) + (preview?.Length ?? 0) + (logs?.Length ?? 0) + (metadata?.Length ?? 0));
        return TypedResults.Ok(new { receipt, sha256 = digest });
    }

    /// <summary>
    /// A consumed upload token cannot be retried after a malformed result. End the
    /// active attempt so the logical task follows its ordinary retry budget instead
    /// of leaving an accepted attempt stranded forever.
    /// </summary>
    private static async Task<IResult> ResultValidationFailedAsync(ProviderSessionApplication session, ExecutionUnitId unitId, TaskId taskId, AttemptId attemptId, string handle, string code, CancellationToken cancellationToken)
    {
        await session.Fail(unitId, taskId, attemptId, handle, "result_validation", code).RunAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.BadRequest(new { code });
    }

    private static bool TryGetParts(IFormCollection form, out Dictionary<string, IFormFile> parts)
    {
        parts = [];
        foreach (var group in form.Files.GroupBy(static file => file.Name, StringComparer.Ordinal))
        {
            if (group.Key is not ("result" or "metadata" or "thumbnail" or "preview" or "logs") || group.Count() != 1) return false;
            parts.Add(group.Key, group.Single());
        }
        return true;
    }

    private static bool TryGetMetadata(IFormCollection form, IReadOnlyDictionary<string, IFormFile> parts, out IFormFile? file, out string? value)
    {
        file = parts.GetValueOrDefault("metadata");
        var values = form["metadata"];
        value = values.Count switch { 0 => null, 1 => values[0], _ => null };
        return values.Count <= 1 && !(file is not null && value is not null);
    }

    private static async Task<ResultArtifact?> StoreImageAsync(IFormFile? file, IObjectStore store, Func<RequestorId, TaskId, AttemptId, string, ObjectKey> key, RequestorId requestorId, TaskId taskId, AttemptId attemptId, CancellationToken cancellationToken)
    {
        if (file is null) return null;
        var bytes = await ReadBytesAsync(file, MaximumImageBytes, cancellationToken).ConfigureAwait(false);
        if (!MutualGpuEndpoints.TryValidateImage(file.ContentType, bytes, out var extension)) throw new InvalidDataException("result_image_invalid");
        return await StoreAsync(bytes, file.ContentType, key(requestorId, taskId, attemptId, extension), store, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ResultArtifact?> StorePreviewAsync(IFormFile? file, OutputDefinition output, IObjectStore store, MutualGpuObjectKeys keys, RequestorId requestorId, TaskId taskId, AttemptId attemptId, CancellationToken cancellationToken)
    {
        if (file is null) return null;
        if (output.PreviewContentTypes?.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase) is not true) throw new InvalidDataException("preview_content_type_invalid");
        return await StoreImageAsync(file, store, keys.ResultPreview, requestorId, taskId, attemptId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ResultArtifact?> StoreLogsAsync(IFormFile? file, IObjectStore store, MutualGpuObjectKeys keys, RequestorId requestorId, TaskId taskId, AttemptId attemptId, CancellationToken cancellationToken)
    {
        if (file is null) return null;
        if (!StringComparer.OrdinalIgnoreCase.Equals(file.ContentType, "text/plain")) throw new InvalidDataException("logs_content_type_invalid");
        var bytes = await ReadBytesAsync(file, MaximumLogBytes, cancellationToken).ConfigureAwait(false);
        try { _ = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new InvalidDataException("logs_utf8_invalid"); }
        return await StoreAsync(bytes, "text/plain", keys.ResultLogs(requestorId, taskId, attemptId), store, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ResultArtifact?> StoreMetadataAsync(IFormFile? file, string? value, IObjectStore store, MutualGpuObjectKeys keys, RequestorId requestorId, TaskId taskId, AttemptId attemptId, CancellationToken cancellationToken)
    {
        if (file is null && value is null) return null;
        if (file is not null && !StringComparer.OrdinalIgnoreCase.Equals(file.ContentType, "application/json")) throw new InvalidDataException("metadata_content_type_invalid");
        var bytes = file is null ? StrictUtf8.GetBytes(value!) : await ReadBytesAsync(file, MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaximumMetadataBytes) throw new InvalidDataException("metadata_too_large");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind is not JsonValueKind.Object) throw new InvalidDataException("metadata_shape_invalid");
        }
        catch (JsonException) { throw new InvalidDataException("metadata_json_invalid"); }
        return await StoreAsync(bytes, "application/json", keys.ResultMetadata(requestorId, taskId, attemptId), store, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ResultArtifact> StoreAsync(byte[] bytes, string contentType, ObjectKey key, IObjectStore store, CancellationToken cancellationToken)
    {
        await using var content = new MemoryStream(bytes, writable: false);
        await store.PutAsync(key, content, ObjectWriteConditions.None, cancellationToken).ConfigureAwait(false);
        return new ResultArtifact(ArtifactId.New(), contentType, bytes.LongLength, Sha256(bytes));
    }

    private static async Task<byte[]> ReadBytesAsync(IFormFile file, int maximumBytes, CancellationToken cancellationToken)
    {
        if (file.Length > maximumBytes) throw new InvalidDataException("result_part_too_large");
        await using var input = file.OpenReadStream();
        await using var content = new MemoryStream((int)Math.Min(file.Length, maximumBytes));
        await input.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
        if (content.Length > maximumBytes) throw new InvalidDataException("result_part_too_large");
        return content.ToArray();
    }

    private static bool IsZip(byte[] bytes)
    {
        try
        {
            using var content = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: false);
            // Force central-directory parsing. It rejects truncated local-header
            // prefixes that merely resemble a ZIP but cannot be consumed later.
            _ = archive.Entries.Count;
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static async Task<IResult> Complete(Guid taskId, Guid attemptId, string receipt, HttpContext context, IExecutionUnitAuthenticator authenticator, ProviderSessionApplication session, CancellationToken cancellationToken)
    {
        if ((await AuthenticateAsync(authenticator, context).ConfigureAwait(false)) is not { } unitId || !TryHandle(context, out var handle)) return TypedResults.Unauthorized();
        return await session.Complete(unitId, new TaskId(taskId), new AttemptId(attemptId), handle, receipt).RunAsync(cancellationToken).ConfigureAwait(false) ? TypedResults.Ok() : TypedResults.Conflict(new { code = "completion_invalid" });
    }

    private static bool TryHandle(HttpContext context, out string handle) => (handle = context.Request.Headers["X-MutualGPU-Task-Handle"].ToString()).Length > 0;
    private static Task<ExecutionUnitId?> AuthenticateAsync(IExecutionUnitAuthenticator authenticator, HttpContext context)
    {
        var value = context.Request.Headers.Authorization.ToString();
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            value = value[7..];
        }

        return authenticator.AuthenticateAsync(value, context.RequestAborted);
    }
    private static bool IsAccepted(TaskRequest task, AttemptId attemptId) => task.Attempts.SingleOrDefault(attempt => attempt.Id == attemptId)?.State is AttemptState.Accepted;
}
