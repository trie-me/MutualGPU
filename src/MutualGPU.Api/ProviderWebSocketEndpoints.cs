using System.Net.WebSockets;
using System.Text.Json;
using Google.Protobuf;
using MutualGPU.Application;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;
using MutualGPU.Protocol;
using NetCats.AspNetCore;
using NetCats.Core;
using NetCats.Runtime;

namespace MutualGPU.Api;

/// <summary>Chrome transport adapter. Each binary WebSocket frame is exactly one canonical protobuf envelope.</summary>
public static class ProviderWebSocketEndpoints
{
    private const int MaximumMessageBytes = 64 * 1024;
    public const string CapabilityIdentityConflictCode = "capability_identity_conflict";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task Enroll(
        HttpContext context,
        IExecutionUnitAuthenticator authenticator,
        EnrollmentApplication enrollment,
        CancellationToken cancellationToken)
    {
        if ((await AuthenticateAsync(authenticator, context.Request.Headers.Authorization, cancellationToken).ConfigureAwait(false)) is not { } unitId)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        EnrollRequest request;
        try { request = await ReadMessageAsync(context.Request.Body, EnrollRequest.Parser, cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        EnrollmentDefinition? definition;
        try { definition = JsonSerializer.Deserialize<EnrollmentDefinition>(request.Definition.Span, JsonOptions); }
        catch (JsonException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        if (definition is null) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        EnrollResult result;
        try
        {
            result = await enrollment.Enroll(new EnrollCommand(unitId, definition.Machine, definition.Capabilities)).RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DomainRuleViolation)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (result is EnrollResult.Conflict conflict)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.ContentType = "application/problem+json";
            await JsonSerializer.SerializeAsync(context.Response.Body, new EnrollmentConflictProblem(
                Type: "https://mutualgpu.com/problems/capability-identity-conflict",
                Title: "Capability identity conflict",
                Status: StatusCodes.Status409Conflict,
                Code: CapabilityIdentityConflictCode,
                Detail: "A capability with the same name is already enrolled with a different execution contract.",
                Conflicts: conflict.Conflicts.Select(static item => new EnrollmentConflictDetail(item.CapabilityName, item.Paths)).ToArray()), JsonOptions, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (result is not EnrollResult.Enrolled enrolled) { context.Response.StatusCode = StatusCodes.Status500InternalServerError; return; }
        context.Response.ContentType = "application/x-protobuf";
        await context.Response.Body.WriteAsync(new EnrollResponse { ExecutionUnitId = enrolled.Unit.Id.Value.ToString("D") }.ToByteArray(), cancellationToken).ConfigureAwait(false);
    }

    private sealed record EnrollmentConflictProblem(
        string Type,
        string Title,
        int Status,
        string Code,
        string Detail,
        IReadOnlyList<EnrollmentConflictDetail> Conflicts);

    private sealed record EnrollmentConflictDetail(string CapabilityName, IReadOnlyList<string> Paths);

    public static async Task Connect(
        HttpContext context,
        IExecutionUnitAuthenticator authenticator,
        IExecutionUnitRepository units,
        ProviderConnectionRegistry connections,
        ProviderSessionApplication sessions,
        IProviderAssignments assignments,
        IResultUploadAuthorizations uploads,
        IArtifactDownloadUrlResolver downloads,
        MutualGpuObjectKeys keys,
        DisconnectRecoveryService recovery,
        TaskAttemptFiberTracker taskFibers,
        MutualGpuFiberOwner fibers,
        MutualGpuTelemetry telemetry,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        var sourceIp = context.Connection.RemoteIpAddress?.ToString();
        var logger = loggerFactory.CreateLogger("MutualGPU.ProviderWebSocket");
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var scope = fibers.ProviderSessions.CreateChild(new FiberScopeOptions("provider-websocket-session"));
        using var cancellation = cancellationToken.Register(static state => _ = ((FiberScope)state!).CloseAsync(), scope);
        Outcome<int> outcome;
        try
        {
            var fiber = scope.Start(Latent<int>.DelayAsync(async fiberCancellationToken =>
            {
                await ConnectCoreAsync(socket, sourceIp, authenticator, units, connections, sessions, assignments, uploads, downloads, keys, recovery, taskFibers, telemetry, logger, fiberCancellationToken).ConfigureAwait(false);
                return 0;
            }), new FiberDescriptor("provider-websocket-session"));
            outcome = await fiber.JoinAsync().ConfigureAwait(false);
        }
        finally
        {
            await scope.CloseAsync().ConfigureAwait(false);
        }
        if (outcome is Outcome<int>.Faulted faulted) throw faulted.Error;
    }

    private static async Task ConnectCoreAsync(
        WebSocket socket,
        string? sourceIp,
        IExecutionUnitAuthenticator authenticator,
        IExecutionUnitRepository units,
        ProviderConnectionRegistry connections,
        ProviderSessionApplication sessions,
        IProviderAssignments assignments,
        IResultUploadAuthorizations uploads,
        IArtifactDownloadUrlResolver downloads,
        MutualGpuObjectKeys keys,
        DisconnectRecoveryService recovery,
        TaskAttemptFiberTracker taskFibers,
        MutualGpuTelemetry telemetry,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ProviderMessage first;
        try { first = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException) { await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid protobuf message.", cancellationToken).ConfigureAwait(false); return; }
        if (first.BodyCase is not ProviderMessage.BodyOneofCase.Connect || first.Connect.ProtocolVersion != 1 ||
            (await AuthenticateAsync(authenticator, first.Connect.Authorization, cancellationToken).ConfigureAwait(false)) is not { } unitId)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Authentication or protocol negotiation failed.", cancellationToken).ConfigureAwait(false);
            return;
        }
        var unit = await units.GetAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null) { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Enrollment is required.", cancellationToken).ConfigureAwait(false); return; }
        var hasActiveTask = !String.IsNullOrWhiteSpace(first.Connect.ActiveTaskHandle);
        // A reconnecting execution unit still owns its previous computation. It
        // must never become a scheduler candidate in the gap before rebind.
        var providerKey = ProviderKey(first.Connect.Authorization);
        var lease = connections.Connect(unit, "wss", isIdle: !hasActiveTask, sourceIp: sourceIp, providerName: ProviderDisplayNames.Create(providerKey));
        Task? writes = null;
        using var sendGate = new SemaphoreSlim(1, 1);
        try
        {
            if (hasActiveTask &&
                !await sessions.Rebind(unitId, first.Connect.ActiveTaskHandle).RunAsync(cancellationToken).ConfigureAwait(false))
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "The reconnect task handle is not eligible for rebinding.", cancellationToken).ConfigureAwait(false);
                return;
            }
            await SendAsync(socket, sendGate, new ServerMessage { Connected = new Connected { ExecutionUnitId = unitId.Value.ToString("D") } }, cancellationToken).ConfigureAwait(false);
            writes = WriteAssignmentsAsync(socket, lease, downloads, keys, sendGate, cancellationToken);
            while (socket.State is WebSocketState.Open)
            {
                var message = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
                var valid = await ApplyAsync(sessions, assignments, uploads, downloads, keys, socket, sendGate, taskFibers, telemetry, unitId, message, logger, cancellationToken).ConfigureAwait(false);
                if (!valid) { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid task handle.", cancellationToken).ConfigureAwait(false); break; }
                if (message.BodyCase is ProviderMessage.BodyOneofCase.Completed)
                    await SendAsync(socket, sendGate, new ServerMessage { Completion = new CompletionAccepted { TaskId = message.Completed.TaskId } }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (WebSocketException) { }
        finally
        {
            await taskFibers.CancelExecutionUnitAsync(unitId).ConfigureAwait(false);
            if (connections.IsCurrent(lease))
            {
                if (await sessions.Disconnect(unitId).RunAsync(CancellationToken.None).ConfigureAwait(false) > 0) recovery.Start(unitId);
                connections.Disconnect(lease);
            }
            if (writes is not null)
            {
                try { await writes.ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (WebSocketException) { }
            }
        }
    }

    private static async Task<bool> ApplyAsync(ProviderSessionApplication sessions, IProviderAssignments assignments, IResultUploadAuthorizations uploads, IArtifactDownloadUrlResolver downloads, MutualGpuObjectKeys keys, WebSocket socket, SemaphoreSlim sendGate, TaskAttemptFiberTracker taskFibers, MutualGpuTelemetry telemetry, ExecutionUnitId unitId, ProviderMessage message, ILogger logger, CancellationToken cancellationToken)
    {
        if (message.BodyCase is ProviderMessage.BodyOneofCase.Progress)
        {
            var disposition = sessions.ReportProgress(
                unitId,
                ParseTaskId(message.Progress.TaskId),
                ParseAttemptId(message.Progress.AttemptId),
                message.Progress.TaskHandle,
                new TaskProgress(message.Progress.SequenceNumber, DateTimeOffset.UtcNow, message.Progress.Phase, message.Progress.Percent, message.Progress.Message));
            ProviderMessageLogging.Progress(logger, "wss", unitId, message.Progress.TaskId, message.Progress.AttemptId, message.Progress.SequenceNumber, disposition);
            telemetry.ProviderProgress("wss", disposition);
            if (disposition is ProviderProgressDisposition.DroppedStaleSequence or ProviderProgressDisposition.DroppedSuperseded) return true;
            if (disposition is not ProviderProgressDisposition.Accepted) return false;

            var phase = String.IsNullOrWhiteSpace(message.Progress.Phase) ? "provider work" : message.Progress.Phase;
            await taskFibers.OperationAsync(unitId, ParseTaskId(message.Progress.TaskId), ParseAttemptId(message.Progress.AttemptId), "task-phase", $"{phase} {message.Progress.Percent:0}%", cancellationToken).ConfigureAwait(false);
            return true;
        }

        var accepted = message.BodyCase switch
        {
            ProviderMessage.BodyOneofCase.Accepted => await sessions.Accept(unitId, ParseTaskId(message.Accepted.TaskId), ParseAttemptId(message.Accepted.AttemptId), message.Accepted.TaskHandle, DateTimeOffset.UtcNow).RunAsync(cancellationToken).ConfigureAwait(false),
            ProviderMessage.BodyOneofCase.Rejected => await sessions.Reject(unitId, ParseTaskId(message.Rejected.TaskId), ParseAttemptId(message.Rejected.AttemptId), message.Rejected.TaskHandle, message.Rejected.Reason).RunAsync(cancellationToken).ConfigureAwait(false),
            ProviderMessage.BodyOneofCase.Failed => await sessions.Fail(unitId, ParseTaskId(message.Failed.TaskId), ParseAttemptId(message.Failed.AttemptId), message.Failed.TaskHandle, message.Failed.Step, message.Failed.Reason).RunAsync(cancellationToken).ConfigureAwait(false),
            ProviderMessage.BodyOneofCase.Completed => await sessions.Complete(unitId, ParseTaskId(message.Completed.TaskId), ParseAttemptId(message.Completed.AttemptId), message.Completed.TaskHandle, message.Completed.Receipt).RunAsync(cancellationToken).ConfigureAwait(false),
            ProviderMessage.BodyOneofCase.ResultUpload => await IssueUploadAsync(assignments, uploads, socket, sendGate, unitId, message.ResultUpload, cancellationToken).ConfigureAwait(false),
            ProviderMessage.BodyOneofCase.InputDownload => await IssueInputAsync(assignments, downloads, keys, socket, sendGate, unitId, message.InputDownload, cancellationToken).ConfigureAwait(false),
            _ => false,
        };
        if (!accepted) return false;
        ProviderMessageLogging.Accepted(logger, unitId, message);
        if (message.BodyCase is ProviderMessage.BodyOneofCase.Failed)
            ProviderMessageLogging.Failed(logger, "wss", unitId, message.Failed);

        if (message.BodyCase is ProviderMessage.BodyOneofCase.Accepted)
        {
            taskFibers.Start(unitId, ParseTaskId(message.Accepted.TaskId), ParseAttemptId(message.Accepted.AttemptId));
            await taskFibers.OperationAsync(unitId, ParseTaskId(message.Accepted.TaskId), ParseAttemptId(message.Accepted.AttemptId), "task-acceptance", "accept assignment", cancellationToken).ConfigureAwait(false);
        }
        else if (message.BodyCase is ProviderMessage.BodyOneofCase.InputDownload)
        {
            await taskFibers.OperationAsync(unitId, ParseTaskId(message.InputDownload.TaskId), ParseAttemptId(message.InputDownload.AttemptId), "task-input-download", "authorize input download", cancellationToken).ConfigureAwait(false);
        }
        else if (message.BodyCase is ProviderMessage.BodyOneofCase.ResultUpload)
        {
            await taskFibers.OperationAsync(unitId, ParseTaskId(message.ResultUpload.TaskId), ParseAttemptId(message.ResultUpload.AttemptId), "task-result-upload", "authorize result upload", cancellationToken).ConfigureAwait(false);
        }
        else if (message.BodyCase is ProviderMessage.BodyOneofCase.Completed)
        {
            await taskFibers.OperationAsync(unitId, ParseTaskId(message.Completed.TaskId), ParseAttemptId(message.Completed.AttemptId), "task-completion", "record completion", cancellationToken).ConfigureAwait(false);
            await taskFibers.CompleteAsync(unitId, ParseTaskId(message.Completed.TaskId), ParseAttemptId(message.Completed.AttemptId)).ConfigureAwait(false);
        }
        else if (message.BodyCase is ProviderMessage.BodyOneofCase.Failed)
        {
            await taskFibers.OperationAsync(unitId, ParseTaskId(message.Failed.TaskId), ParseAttemptId(message.Failed.AttemptId), "task-failure", $"record failure: {message.Failed.Step}", cancellationToken).ConfigureAwait(false);
            await taskFibers.CompleteAsync(unitId, ParseTaskId(message.Failed.TaskId), ParseAttemptId(message.Failed.AttemptId)).ConfigureAwait(false);
        }
        return true;
    }

    private static async Task WriteAssignmentsAsync(WebSocket socket, ProviderSessionLease lease, IArtifactDownloadUrlResolver downloads, MutualGpuObjectKeys keys, SemaphoreSlim sendGate, CancellationToken cancellationToken)
    {
        await foreach (var message in lease.Assignments.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (message is ProviderCancellation cancelled)
            {
                await SendAsync(socket, sendGate, new ServerMessage
                {
                    Cancelled = new TaskCancelled
                    {
                        TaskId = cancelled.TaskId.Value.ToString("D"),
                        AttemptId = cancelled.AttemptId.Value.ToString("D"),
                        TaskHandle = cancelled.Handle,
                    },
                }, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var assignment = ((ProviderAssignmentMessage)message).Assignment;
            var wire = new TaskAssignment { TaskId = assignment.TaskId.Value.ToString("D"), AttemptId = assignment.AttemptId.Value.ToString("D"), TaskHandle = assignment.Handle };
            foreach (var (key, value) in assignment.Scalars) wire.Scalars[key] = value;
            if (assignment.Input is { } input)
            {
                var url = await downloads.CreateDownloadUrlAsync(
                    assignment.TaskId,
                    input.ArtifactId,
                    keys.TaskInput(input.RequestorId, assignment.TaskId, input.ArtifactId, input.Extension),
                    TimeSpan.FromMinutes(15),
                    cancellationToken).ConfigureAwait(false);
                wire.Input = new InputArtifactDescriptor
                {
                    Url = url.ToString(),
                    ContentType = input.ContentType,
                    Length = (ulong)input.Length,
                    Sha256 = input.Sha256,
                };
            }
            await SendAsync(socket, sendGate, new ServerMessage { Assignment = wire }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> IssueUploadAsync(IProviderAssignments assignments, IResultUploadAuthorizations uploads, WebSocket socket, SemaphoreSlim sendGate, ExecutionUnitId unitId, ResultUploadRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.TaskId, out var task) || !Guid.TryParse(request.AttemptId, out var attempt) ||
            !assignments.TryGet(unitId, new TaskId(task), new AttemptId(attempt), request.TaskHandle, out var requestTask) ||
            requestTask.Attempts.SingleOrDefault(candidate => candidate.Id == new AttemptId(attempt))?.State is not AttemptState.Accepted) return false;
        var authorization = await uploads
            .IssueAsync(unitId, new TaskId(task), new AttemptId(attempt), request.TaskHandle, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        await SendAsync(socket, sendGate, new ServerMessage { ResultUpload = new MutualGPU.Protocol.ResultUploadAuthorization { UploadToken = authorization.Token } }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> IssueInputAsync(IProviderAssignments assignments, IArtifactDownloadUrlResolver downloads, MutualGpuObjectKeys keys, WebSocket socket, SemaphoreSlim sendGate, ExecutionUnitId unitId, InputDownloadRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.TaskId, out var taskId) || !Guid.TryParse(request.AttemptId, out var attemptId) || !assignments.TryGet(unitId, new TaskId(taskId), new AttemptId(attemptId), request.TaskHandle, out var task) || task.Parameters.Image is null) return false;
        ObjectKey? input = task.Parameters.ImageExtension is { Length: > 0 } extension
            ? keys.TaskInput(task.RequestorId, task.Id, task.Parameters.Image.Value, extension)
            : null;
        if (input is null) return false;
        var url = await downloads.CreateDownloadUrlAsync(
            task.Id,
            task.Parameters.Image.Value,
            input.Value,
            TimeSpan.FromMinutes(15),
            cancellationToken).ConfigureAwait(false);
        await SendAsync(socket, sendGate, new ServerMessage { InputDownload = new InputDownloadAuthorization { Url = url.ToString() } }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task SendAsync(WebSocket socket, SemaphoreSlim gate, ServerMessage message, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await socket.SendAsync(message.ToByteArray(), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private static async Task<ProviderMessage> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new ArraySegment<byte>(new byte[MaximumMessageBytes]);
        var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result.MessageType is WebSocketMessageType.Close) throw new WebSocketException("The provider closed the session.");
        if (result.MessageType is not WebSocketMessageType.Binary || !result.EndOfMessage || result.Count > MaximumMessageBytes) throw new InvalidDataException("A provider frame must be one bounded binary protobuf message.");
        try { return ProviderMessage.Parser.ParseFrom(buffer.Array!, 0, result.Count); }
        catch (InvalidProtocolBufferException exception) { throw new InvalidDataException("Invalid protobuf message.", exception); }
    }

    private static async Task<T> ReadMessageAsync<T>(Stream stream, MessageParser<T> parser, CancellationToken cancellationToken) where T : IMessage<T>
    {
        await using var content = new MemoryStream();
        await stream.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
        if (content.Length is 0 or > MaximumMessageBytes) throw new InvalidDataException("Invalid protobuf body size.");
        return parser.ParseFrom(content.ToArray());
    }

    private static Task<ExecutionUnitId?> AuthenticateAsync(
        IExecutionUnitAuthenticator authenticator,
        string? authorization,
        CancellationToken cancellationToken)
    {
        return authenticator.AuthenticateAsync(ProviderKey(authorization), cancellationToken);
    }

    private static string ProviderKey(string? authorization) =>
        authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) is true ? authorization[7..] : authorization ?? String.Empty;

    private static TaskId ParseTaskId(string value) => Guid.TryParse(value, out var id) ? new TaskId(id) : throw new InvalidDataException("Task ID is invalid.");

    private static AttemptId ParseAttemptId(string value) => Guid.TryParse(value, out var id) ? new AttemptId(id) : throw new InvalidDataException("Attempt ID is invalid.");
}
