using MutualGPU.Application;
using MutualGPU.Contracts;
using MutualGPU.Domain;
using System.Security.Cryptography;
using System.Text.Json;

namespace MutualGPU.Api;

public static class MutualGpuEndpoints
{
    public static async Task<IResult> ListCapabilities(CapabilityCatalogueApplication catalogue, CancellationToken cancellationToken)
    {
        var available = await catalogue.List().RunAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(available.Select(item => new CapabilityAvailabilityDto(
            item.Capability.Id.Value,
            item.Capability.Name,
            item.Capability.ContractHash,
            item.Machines.Select(availability => new MachineAvailabilityDto(
                availability.Specifications.ComputeTier, availability.Specifications.MemoryGiB, availability.ConnectedCount, availability.IdleCount)).ToArray(),
                item.Capability.Inputs.Select(input => new CapabilityInputDto(input.Key, input.Type.ToString(), input.Required, input.Label, input.Description, input.Default, input.Minimum, input.Maximum, input.AllowedValues, input.ContentTypes)).ToArray())).ToArray());
    }

    public static async Task<IResult> SubmitTask(HttpContext context, TaskSubmissionApplication submission, ICapabilityReader capabilities, IObjectStore store, MutualGPU.Infrastructure.MutualGpuObjectKeys keys, MutualGpuTelemetry telemetry, CancellationToken cancellationToken)
    {
        using var activity = telemetry.Activities.StartActivity("mutualgpu.requestor.submit");
        if (!RequestorIdentity.TryGet(context, out var requestorId)) return Problem("requestor_identity_missing", StatusCodes.Status400BadRequest);
        SubmitTaskRequestDto? request;
        ArtifactId? image = null;
        var taskId = MutualGPU.Domain.TaskId.New();
        PendingImage? pendingImage = null;
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            try { request = JsonSerializer.Deserialize<SubmitTaskRequestDto>(form["submission"].ToString(), JsonOptions); }
            catch (JsonException) { return Problem("submission_invalid", StatusCodes.Status400BadRequest); }
            if (request is null) return Problem("submission_invalid", StatusCodes.Status400BadRequest);
            if (form.Files.Count > 1) return Problem("maximum_one_image", StatusCodes.Status400BadRequest);
            if (form.Files.Count == 1)
            {
                var file = form.Files[0];
                var capability = await capabilities.GetAsync(new CapabilityId(request.CapabilityId), cancellationToken).ConfigureAwait(false);
                var imageInput = capability?.Inputs.SingleOrDefault(static input => input.Type is CapabilityInputType.Image);
                if (imageInput is null) return Problem("image_not_declared", StatusCodes.Status400BadRequest);
                if (imageInput.ContentTypes is { Count: > 0 } && !imageInput.ContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase)) return Problem("image_content_type_invalid", StatusCodes.Status400BadRequest);
                await using var input = file.OpenReadStream(); await using var content = new MemoryStream(); await input.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
                var bytes = content.ToArray();
                if (!TryValidateImage(file.ContentType, bytes, out var extension)) return Problem("image_invalid", StatusCodes.Status400BadRequest);
                image = ArtifactId.New();
                pendingImage = new PendingImage(
                    image.Value,
                    file.ContentType,
                    extension,
                    bytes,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            }
        }
        else request = await JsonSerializer.DeserializeAsync<SubmitTaskRequestDto>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (request is null) return Problem("submission_invalid", StatusCodes.Status400BadRequest);
        if (pendingImage is not null)
        {
            await using var objectContent = new MemoryStream(pendingImage.Bytes, writable: false);
            await store.PutAsync(
                keys.TaskInput(requestorId, taskId, pendingImage.ArtifactId, pendingImage.Extension),
                objectContent,
                ObjectWriteConditions.IfNotExists,
                cancellationToken).ConfigureAwait(false);
        }

        SubmitTaskResult result;
        try
        {
            result = await submission.Submit(new SubmitTaskCommand(
                requestorId,
                new CapabilityId(request.CapabilityId),
                request.ContractHash,
                request.Scalars ?? new Dictionary<string, string>(StringComparer.Ordinal),
                image,
                request.Resources,
                DateTimeOffset.UtcNow,
                request.IdempotencyKey,
                taskId,
                pendingImage?.ContentType,
                pendingImage?.Extension,
                pendingImage?.Bytes.LongLength,
                pendingImage?.Sha256)).RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (pendingImage is not null)
            {
                await store.DeleteAsync(keys.TaskInput(requestorId, taskId, pendingImage.ArtifactId, pendingImage.Extension), CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }

        // An idempotent replay uses a new staging key, never a new task. Remove that
        // unreferenced object immediately; a crash here is the documented recoverable
        // staged-object boundary, not a second durable task.
        if (pendingImage is not null && result is not SubmitTaskResult.Created { CreatedNow: true })
        {
            await store.DeleteAsync(keys.TaskInput(requestorId, taskId, pendingImage.ArtifactId, pendingImage.Extension), cancellationToken).ConfigureAwait(false);
        }
        return result switch
        {
            SubmitTaskResult.Created created => Created(created, telemetry),
            SubmitTaskResult.Unavailable => Problem("capability_unavailable", StatusCodes.Status409Conflict),
            SubmitTaskResult.Conflict conflict => Problem(conflict.Code, StatusCodes.Status409Conflict),
            SubmitTaskResult.Invalid invalid => TypedResults.ValidationProblem(invalid.Errors),
            _ => Problem("submission_failed", StatusCodes.Status500InternalServerError),
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record PendingImage(ArtifactId ArtifactId, string ContentType, string Extension, byte[] Bytes, string Sha256);

    internal static bool TryValidateImage(string contentType, byte[] bytes, out string extension)
    {
        extension = contentType switch { "image/png" => "png", "image/jpeg" => "jpg", "image/webp" => "webp", _ => "" };
        if (bytes.Length < 24 || extension.Length == 0) return false;
        int width; int height;
        if (extension is "png" && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) { width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)); height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)); }
        else if (extension is "webp" && TryWebpDimensions(bytes, out width, out height)) { }
        else if (extension is "jpg" && TryJpegDimensions(bytes, out width, out height)) { }
        else return false;
        return width is > 0 and <= 1024 && height is > 0 and <= 1024;
    }

    private static bool TryJpegDimensions(byte[] bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;
        for (var index = 2; index + 9 < bytes.Length;)
        {
            if (bytes[index++] != 0xff) continue;
            while (index < bytes.Length && bytes[index] == 0xff) index++;
            if (index >= bytes.Length) return false;
            var marker = bytes[index++];
            if (marker is 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7) continue;
            if (index + 1 >= bytes.Length) return false;
            var length = (bytes[index] << 8) | bytes[index + 1];
            if (length < 2 || index + length > bytes.Length) return false;
            if (marker is >= 0xc0 and <= 0xc3 || marker is >= 0xc5 and <= 0xc7 || marker is >= 0xc9 and <= 0xcb || marker is >= 0xcd and <= 0xcf)
            { height = (bytes[index + 3] << 8) | bytes[index + 4]; width = (bytes[index + 5] << 8) | bytes[index + 6]; return true; }
            index += length;
        }
        return false;
    }

    private static bool TryWebpDimensions(byte[] bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 16 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return false;

        if (bytes.AsSpan(12, 4).SequenceEqual("VP8X"u8) && bytes.Length >= 30)
        {
            width = 1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16);
            height = 1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16);
            return true;
        }

        // Lossy VP8 frame: three-byte frame tag, then the 0x9d012a start code
        // and 14-bit little-endian dimensions.
        if (bytes.AsSpan(12, 4).SequenceEqual("VP8 "u8) && bytes.Length >= 30 && bytes.AsSpan(23, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
        {
            width = (bytes[26] | (bytes[27] << 8)) & 0x3fff;
            height = (bytes[28] | (bytes[29] << 8)) & 0x3fff;
            return true;
        }

        // Lossless VP8L image: signature byte then a packed 14-bit width and height.
        if (bytes.AsSpan(12, 4).SequenceEqual("VP8L"u8) && bytes.Length >= 25 && bytes[20] == 0x2f)
        {
            var packed = (uint)(bytes[21] | (bytes[22] << 8) | (bytes[23] << 16) | (bytes[24] << 24));
            width = (int)(packed & 0x3fff) + 1;
            height = (int)((packed >> 14) & 0x3fff) + 1;
            return true;
        }

        return false;
    }

    public static async Task<IResult> ListTasks(HttpContext context, ITaskSummaryReader summaries, IProviderProgress progress, CancellationToken cancellationToken)
    {
        if (!RequestorIdentity.TryGet(context, out var requestorId)) return Problem("requestor_identity_missing", StatusCodes.Status400BadRequest);
        var tasks = await summaries.ListSummariesAsync(requestorId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(tasks.Select(task => ToDto(task, progress.Get(task.TaskId))).ToArray());
    }

    public static async Task<IResult> GetTask(HttpContext context, Guid taskId, ITaskRepository tasks, IProviderProgress progress, CancellationToken cancellationToken)
    {
        if (!RequestorIdentity.TryGet(context, out var requestorId)) return Problem("requestor_identity_missing", StatusCodes.Status400BadRequest);
        var task = await tasks.GetAsync(requestorId, new TaskId(taskId), cancellationToken).ConfigureAwait(false);
        return task is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(task, progress.Get(task.Id)));
    }

    public static async Task<IResult> ReevaluateTask(HttpContext context, Guid taskId, ITaskRepository tasks, IApplicationEventSink events, CancellationToken cancellationToken)
    {
        if (!RequestorIdentity.TryGet(context, out var requestorId)) return Problem("requestor_identity_missing", StatusCodes.Status400BadRequest);
        var task = await tasks.GetAsync(requestorId, new TaskId(taskId), cancellationToken).ConfigureAwait(false);
        if (task is null) return TypedResults.NotFound();
        if (task.Status is not MutualGPU.Domain.TaskStatus.Running) return Problem("task_not_running", StatusCodes.Status409Conflict);
        events.TriggerScheduler();
        return TypedResults.Accepted($"/api/tasks/{taskId:D}");
    }

    public static async Task<IResult> GetTaskResult(HttpContext context, Guid taskId, ITaskRepository tasks, IObjectStore store, MutualGPU.Infrastructure.MutualGpuObjectKeys keys, CancellationToken cancellationToken)
    {
        if (!RequestorIdentity.TryGet(context, out var requestorId)) return Problem("requestor_identity_missing", StatusCodes.Status400BadRequest);
        var task = await tasks.GetAsync(requestorId, new TaskId(taskId), cancellationToken).ConfigureAwait(false);
        if (task is null) return TypedResults.NotFound();
        if (task.Status is not MutualGPU.Domain.TaskStatus.Completed || task.Result is null) return Problem("result_not_available", StatusCodes.Status409Conflict);
        var attempt = task.Attempts.LastOrDefault(static attempt => attempt.State is AttemptState.Completed);
        if (attempt is null) return Problem("result_not_available", StatusCodes.Status409Conflict);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        var artifacts = new List<ResultArtifactDto>();
        async Task AddArtifact(string name, ResultArtifact? artifact, ObjectKey key)
        {
            if (artifact is null) return;
            var url = await store.CreateDownloadUrlAsync(key, TimeSpan.FromMinutes(15), cancellationToken).ConfigureAwait(false);
            artifacts.Add(new ResultArtifactDto(name, artifact.ContentType, artifact.Length, artifact.Sha256, expiresAt, url));
        }

        await AddArtifact("result", task.Result.Zip, keys.ResultZip(task.RequestorId, task.Id, attempt.Id)).ConfigureAwait(false);
        await AddArtifact("metadata", task.Result.Metadata, keys.ResultMetadata(task.RequestorId, task.Id, attempt.Id)).ConfigureAwait(false);
        await AddArtifact("thumbnail", task.Result.Thumbnail, keys.ResultThumbnail(task.RequestorId, task.Id, attempt.Id, ExtensionFor(task.Result.Thumbnail))).ConfigureAwait(false);
        await AddArtifact("preview", task.Result.Preview, keys.ResultPreview(task.RequestorId, task.Id, attempt.Id, ExtensionFor(task.Result.Preview))).ConfigureAwait(false);
        await AddArtifact("logs", task.Result.Logs, keys.ResultLogs(task.RequestorId, task.Id, attempt.Id)).ConfigureAwait(false);
        return TypedResults.Ok(new TaskResultDto(task.Id.Value, artifacts));
    }

    private static TaskDto ToDto(TaskSummary task, TaskProgress? progress = null) => new(
        task.TaskId.Value, task.CapabilityName, task.CreatedAt, task.Resources,
        task.Status, task.AttemptCount, task.FailureStep, task.Status is MutualGPU.Domain.TaskStatus.Running, task.Status is MutualGPU.Domain.TaskStatus.Completed, ToDto(progress));

    private static TaskDto ToDto(TaskRequest task, TaskProgress? progress = null) => new(
        task.Id.Value, task.Capability.Name, task.CreatedAt, task.Resources,
        task.Status, task.AssignmentCount,
        task.Attempts.LastOrDefault(static attempt => attempt.State is AttemptState.Failed or AttemptState.Rejected or AttemptState.Revoked)?.FailureStep,
        task.Status is MutualGPU.Domain.TaskStatus.Running, task.Status is MutualGPU.Domain.TaskStatus.Completed, ToDto(progress));

    private static TaskProgressDto? ToDto(TaskProgress? progress) => progress is null ? null : new TaskProgressDto(progress.SequenceNumber, progress.ObservedAt, progress.Phase, progress.Percent, progress.Message);

    private static string ExtensionFor(ResultArtifact? artifact) => artifact?.ContentType switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/webp" => "webp",
        _ => "bin",
    };

    private static IResult Problem(string code, int status) => TypedResults.Problem(statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult Created(SubmitTaskResult.Created created, MutualGpuTelemetry telemetry)
    {
        if (created.CreatedNow) telemetry.TaskSubmitted();
        return TypedResults.Created($"/api/tasks/{created.Task.Id.Value:D}", ToDto(created.Task));
    }
}

public static class RequestorIdentity
{
    public const string CookieName = "mutualgpu-requestor";

    public static bool TryGet(HttpContext context, out RequestorId requestorId)
    {
        if (Guid.TryParse(context.Request.Cookies[CookieName], out var parsed) && parsed != Guid.Empty)
        {
            requestorId = new RequestorId(parsed);
            return true;
        }

        requestorId = default;
        return false;
    }
}
