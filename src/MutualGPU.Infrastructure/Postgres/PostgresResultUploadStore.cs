using System.Security.Cryptography;
using MutualGPU.Application;
using MutualGPU.Domain;
using Npgsql;
using NpgsqlTypes;

namespace MutualGPU.Infrastructure;

public sealed class PostgresResultUploadStore(
    NpgsqlDataSource dataSource,
    IOperationUnitOfWork operations,
    HandleCipher handles,
    MutualGpuObjectKeys objectKeys)
    : IResultUploadAuthorizations, IStagedResults
{
    public ResultUploadAuthorization Issue(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        DateTimeOffset now) =>
        throw new NotSupportedException("Use the asynchronous durable upload API.");

    public bool TryConsume(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        string token,
        DateTimeOffset now) =>
        throw new NotSupportedException("Use the asynchronous durable upload API.");

    public void Stage(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        StagedResult result) =>
        throw new NotSupportedException("Use the asynchronous durable upload API.");

    public bool TryTake(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        string receipt,
        out StagedResult result) =>
        throw new NotSupportedException("Use the asynchronous durable upload API.");

    public void MarkCompleted(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        string receipt) =>
        throw new NotSupportedException("Use the asynchronous durable upload API.");

    public bool IsCompleted(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        string receipt) =>
        throw new NotSupportedException("Use the asynchronous durable upload API.");

    public async ValueTask<ResultUploadAuthorization> IssueAsync(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiresAt = now.AddMinutes(15);
        var operation = new ResultUploadOperation(
            ResultUploadOperationId.New(),
            taskId,
            attemptId,
            unitId,
            handles.Digest(handle),
            Digest(token),
            null,
            ResultUploadState.Authorized,
            expiresAt,
            1,
            now);
        await operations.ExecuteAsync(
            (context, _) =>
            {
                context.ResultUploads.Add(operation);
                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);
        return new ResultUploadAuthorization(token, expiresAt);
    }

    public async ValueTask<bool> TryConsumeAsync(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        string token,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            update result_upload_operations
            set state = 'uploading',
                version = version + 1
            where token_digest = @token_digest
              and task_id = @task_id
              and attempt_id = @attempt_id
              and execution_unit_id = @execution_unit_id
              and handle_digest = @handle_digest
              and state = 'authorized'
              and expires_at >= @now
            """,
            connection);
        command.Parameters.Add("token_digest", NpgsqlDbType.Char).Value = Digest(token);
        command.Parameters.AddWithValue("task_id", taskId.Value);
        command.Parameters.AddWithValue("attempt_id", attemptId.Value);
        command.Parameters.AddWithValue("execution_unit_id", unitId.Value);
        command.Parameters.Add("handle_digest", NpgsqlDbType.Char).Value = handles.Digest(handle);
        command.Parameters.AddWithValue("now", PostgresPersistence.Utc(now));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask StageAsync(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        StagedResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var handleDigest = handles.Digest(handle);
        await operations.ExecuteAsync(
            async (context, token) =>
            {
                var upload = await context.ResultUploads
                    .GetUploadingAsync(taskId, attemptId, unitId, handleDigest, token)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("No consumed upload operation is available for staging.");
                var task = await context.Tasks.GetAsync(taskId, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The result task is unavailable.");
                context.ResultUploads.Update(upload with
                {
                    Receipt = result.Receipt,
                    State = ResultUploadState.Uploaded,
                    UploadedAt = DateTimeOffset.UtcNow,
                    Version = upload.Version + 1,
                });
                foreach (var (role, artifact, key) in ResultArtifacts(
                    task.RequestorId,
                    taskId,
                    attemptId,
                    result.Result))
                {
                    if (artifact is null) continue;
                    context.Artifacts.Add(new ArtifactDescriptor(
                        artifact.Id,
                        taskId,
                        attemptId,
                        ArtifactDirection.Output,
                        role,
                        key.Value,
                        artifact.ContentType,
                        artifact.Length,
                        artifact.Sha256,
                        ArtifactState.Staged,
                        DateTimeOffset.UtcNow,
                        upload.Id));
                }
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<StagedResult?> GetAsync(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        string receipt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select a.id, a.role, a.content_type, a.length, a.sha256
            from result_upload_operations u
            join artifacts a on a.result_upload_operation_id = u.id
            where u.task_id = @task_id
              and u.attempt_id = @attempt_id
              and u.execution_unit_id = @execution_unit_id
              and u.handle_digest = @handle_digest
              and u.receipt = @receipt
              and u.state in ('uploaded', 'completed')
            order by a.role
            """,
            connection);
        command.Parameters.AddWithValue("task_id", taskId.Value);
        command.Parameters.AddWithValue("attempt_id", attemptId.Value);
        command.Parameters.AddWithValue("execution_unit_id", unitId.Value);
        command.Parameters.Add("handle_digest", NpgsqlDbType.Char).Value = handles.Digest(handle);
        command.Parameters.AddWithValue("receipt", receipt);
        var artifacts = new Dictionary<string, ResultArtifact>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            artifacts.Add(
                reader.GetString(1),
                new ResultArtifact(
                    new ArtifactId(reader.GetGuid(0)),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetString(4).Trim()));
        }
        return artifacts.TryGetValue("result", out var zip)
            ? new StagedResult(
                receipt,
                new TaskResult(
                    zip,
                    artifacts.GetValueOrDefault("thumbnail"),
                    artifacts.GetValueOrDefault("preview"),
                    artifacts.GetValueOrDefault("logs"),
                    artifacts.GetValueOrDefault("metadata")))
            : null;
    }

    public async ValueTask MarkCompletedAsync(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        string receipt,
        CancellationToken cancellationToken)
    {
        var handleDigest = handles.Digest(handle);
        await operations.ExecuteAsync(
            async (context, token) =>
            {
                var upload = await context.ResultUploads
                    .GetByReceiptAsync(taskId, attemptId, receipt, token)
                    .ConfigureAwait(false);
                if (upload is null ||
                    upload.ExecutionUnitId != unitId ||
                    !StringComparer.Ordinal.Equals(upload.HandleDigest, handleDigest) ||
                    upload.State is not (ResultUploadState.Uploaded or ResultUploadState.Completed))
                {
                    throw new InvalidOperationException("The staged result is not available for completion.");
                }
                if (upload.State is ResultUploadState.Uploaded)
                {
                    context.ResultUploads.Update(upload with
                    {
                        State = ResultUploadState.Completed,
                        CompletedAt = DateTimeOffset.UtcNow,
                        Version = upload.Version + 1,
                    });
                    var artifacts = await context.Artifacts.GetForTaskAsync(taskId, token).ConfigureAwait(false);
                    foreach (var artifact in artifacts.Where(artifact =>
                        artifact.ResultUploadOperationId == upload.Id &&
                        artifact.State is ArtifactState.Staged))
                    {
                        context.Artifacts.Update(artifact with { State = ArtifactState.Available });
                    }
                }
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> IsCompletedAsync(
        ExecutionUnitId unitId,
        TaskId taskId,
        AttemptId attemptId,
        string handle,
        string receipt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select exists(
                select 1
                from result_upload_operations
                where task_id = @task_id
                  and attempt_id = @attempt_id
                  and execution_unit_id = @execution_unit_id
                  and handle_digest = @handle_digest
                  and receipt = @receipt
                  and state = 'completed')
            """,
            connection);
        command.Parameters.AddWithValue("task_id", taskId.Value);
        command.Parameters.AddWithValue("attempt_id", attemptId.Value);
        command.Parameters.AddWithValue("execution_unit_id", unitId.Value);
        command.Parameters.Add("handle_digest", NpgsqlDbType.Char).Value = handles.Digest(handle);
        command.Parameters.AddWithValue("receipt", receipt);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private IEnumerable<(string Role, ResultArtifact? Artifact, ObjectKey Key)> ResultArtifacts(
        RequestorId requestorId,
        TaskId taskId,
        AttemptId attemptId,
        TaskResult result)
    {
        yield return ("result", result.Zip, objectKeys.ResultZip(requestorId, taskId, attemptId));
        yield return ("thumbnail", result.Thumbnail, objectKeys.ResultThumbnail(requestorId, taskId, attemptId, ExtensionFor(result.Thumbnail)));
        yield return ("preview", result.Preview, objectKeys.ResultPreview(requestorId, taskId, attemptId, ExtensionFor(result.Preview)));
        yield return ("logs", result.Logs, objectKeys.ResultLogs(requestorId, taskId, attemptId));
        yield return ("metadata", result.Metadata, objectKeys.ResultMetadata(requestorId, taskId, attemptId));
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ExtensionFor(ResultArtifact? artifact) => artifact?.ContentType switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/webp" => "webp",
        _ => "bin",
    };
}
