using System.Text.Json;
using Grpc.Core;
using MutualGPU.Application;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;
using MutualGPU.Protocol;
using NetCats.AspNetCore;
using NetCats.Core;
using NetCats.Runtime;

namespace MutualGPU.Api;

/// <summary>Thin gRPC adapter: authentication and envelope validation live here; enrollment and presence remain application services.</summary>
public sealed class ProviderControlService(
    IExecutionUnitAuthenticator authenticator,
    IExecutionUnitRepository units,
    EnrollmentApplication enrollment,
    ProviderConnectionRegistry connections,
    ProviderSessionApplication session,
    IProviderAssignments assignments,
    IResultUploadAuthorizations uploads,
    IObjectStore store,
    MutualGpuObjectKeys keys,
    DisconnectRecoveryService recovery,
    MutualGpuFiberOwner fibers,
    TaskAttemptFiberTracker taskFibers) : ProviderControl.ProviderControlBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public override async Task<EnrollResponse> Enroll(EnrollRequest request, ServerCallContext context)
    {
        var executionUnitId = await AuthenticateAsync(context.RequestHeaders.GetValue("authorization"), context.CancellationToken).ConfigureAwait(false);
        EnrollmentDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<EnrollmentDefinition>(request.Definition.Span, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Enrollment definition is not valid JSON."), exception.Message);
        }

        if (definition is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Enrollment definition is required."));
        try
        {
            var result = await enrollment.Enroll(new EnrollCommand(executionUnitId, definition.Machine, definition.Capabilities))
                .RunAsync(context.CancellationToken).ConfigureAwait(false);
            return result switch
            {
                EnrollResult.Enrolled enrolled => new EnrollResponse { ExecutionUnitId = enrolled.Unit.Id.Value.ToString("D") },
                EnrollResult.Conflict => throw new RpcException(new Status(StatusCode.AlreadyExists, "Capability contract conflicts with the shared catalogue.")),
                _ => throw new RpcException(new Status(StatusCode.Internal, "Unexpected enrollment result.")),
            };
        }
        catch (DomainRuleViolation violation)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, violation.Message));
        }
    }

    public override async Task Connect(IAsyncStreamReader<ProviderMessage> requestStream, IServerStreamWriter<ServerMessage> responseStream, ServerCallContext context)
    {
        var scope = fibers.ProviderSessions.CreateChild(new FiberScopeOptions("provider-session"));
        using var cancellation = context.CancellationToken.Register(static state => _ = ((FiberScope)state!).CloseAsync(), scope);
        var fiber = scope.Start(Latent<int>.DelayAsync(async _ =>
        {
            await ConnectCoreAsync(requestStream, responseStream, context, scope).ConfigureAwait(false);
            return 0;
        }), new FiberDescriptor("provider-session"));
        var outcome = await fiber.JoinAsync().ConfigureAwait(false);
        await scope.CloseAsync().ConfigureAwait(false);
        if (outcome is Outcome<int>.Faulted faulted) throw faulted.Error;
        if (outcome is Outcome<int>.Cancelled) throw new RpcException(new Status(StatusCode.Cancelled, "Provider session cancelled."));
    }

    private async Task ConnectCoreAsync(IAsyncStreamReader<ProviderMessage> requestStream, IServerStreamWriter<ServerMessage> responseStream, ServerCallContext context, FiberScope sessionScope)
    {
        var executionUnitId = await AuthenticateAsync(context.RequestHeaders.GetValue("authorization"), context.CancellationToken).ConfigureAwait(false);
        var unit = await units.GetAsync(executionUnitId, context.CancellationToken).ConfigureAwait(false)
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "The execution unit must enroll before connecting."));

        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false) || requestStream.Current.BodyCase is not ProviderMessage.BodyOneofCase.Connect)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The first provider message must be ConnectRequest."));
        if (requestStream.Current.Connect.ProtocolVersion != 1)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Unsupported provider protocol version."));

        var lease = connections.Connect(unit);
        using var responseGate = new SemaphoreSlim(1, 1);
        Task? sendAssignments = null;
        try
        {
            if (!String.IsNullOrWhiteSpace(requestStream.Current.Connect.ActiveTaskHandle) &&
                !await session.Rebind(executionUnitId, requestStream.Current.Connect.ActiveTaskHandle).RunAsync(context.CancellationToken).ConfigureAwait(false))
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "The reconnect task handle is not eligible for rebinding."));
            await WriteAsync(responseStream, responseGate, new ServerMessage { Connected = new Connected { ExecutionUnitId = executionUnitId.Value.ToString("D") } }, context.CancellationToken).ConfigureAwait(false);
            sendAssignments = WriteAssignmentsAsync(lease, responseStream, responseGate, context.CancellationToken);
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var message = requestStream.Current;
                await fibers.RunObservedAsync(
                    sessionScope,
                    "provider-message",
                    MessageName(message),
                    async token =>
                    {
                        await ApplyMessageAsync(executionUnitId, message, responseStream, responseGate, token).ConfigureAwait(false);
                        return 0;
                    },
                    context.CancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await taskFibers.CancelExecutionUnitAsync(executionUnitId).ConfigureAwait(false);
            if (connections.IsCurrent(lease))
            {
                if (await session.Disconnect(executionUnitId).RunAsync(CancellationToken.None).ConfigureAwait(false) > 0) recovery.Start(executionUnitId);
                connections.Disconnect(lease);
            }
            if (sendAssignments is not null)
            {
                try { await sendAssignments.ConfigureAwait(false); }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
            }
        }
    }

    private async Task ApplyMessageAsync(ExecutionUnitId executionUnitId, ProviderMessage message, IServerStreamWriter<ServerMessage> response, SemaphoreSlim responseGate, CancellationToken cancellationToken)
    {
        if (message.BodyCase is ProviderMessage.BodyOneofCase.Connect)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ConnectRequest may appear only once."));
        var accepted = message.BodyCase is ProviderMessage.BodyOneofCase.Accepted
            ? await session.Accept(executionUnitId, ParseTaskId(message.Accepted.TaskId), ParseAttemptId(message.Accepted.AttemptId), message.Accepted.TaskHandle, DateTimeOffset.UtcNow).RunAsync(cancellationToken).ConfigureAwait(false)
            : message.BodyCase is ProviderMessage.BodyOneofCase.Rejected
                ? await session.Reject(executionUnitId, ParseTaskId(message.Rejected.TaskId), ParseAttemptId(message.Rejected.AttemptId), message.Rejected.TaskHandle, message.Rejected.Reason).RunAsync(cancellationToken).ConfigureAwait(false)
                : message.BodyCase is ProviderMessage.BodyOneofCase.Failed
                    ? await session.Fail(executionUnitId, ParseTaskId(message.Failed.TaskId), ParseAttemptId(message.Failed.AttemptId), message.Failed.TaskHandle, message.Failed.Step).RunAsync(cancellationToken).ConfigureAwait(false)
                    : message.BodyCase is ProviderMessage.BodyOneofCase.Completed
                        ? await session.Complete(executionUnitId, ParseTaskId(message.Completed.TaskId), ParseAttemptId(message.Completed.AttemptId), message.Completed.TaskHandle, message.Completed.Receipt).RunAsync(cancellationToken).ConfigureAwait(false)
                        : message.BodyCase is ProviderMessage.BodyOneofCase.Progress
                            ? session.ReportProgress(executionUnitId, ParseTaskId(message.Progress.TaskId), ParseAttemptId(message.Progress.AttemptId), message.Progress.TaskHandle, new TaskProgress(message.Progress.SequenceNumber, DateTimeOffset.UtcNow, message.Progress.Phase, message.Progress.Percent, message.Progress.Message))
                            : message.BodyCase is ProviderMessage.BodyOneofCase.ResultUpload
                                ? await IssueUploadAuthorizationAsync(executionUnitId, message.ResultUpload, response, responseGate, cancellationToken).ConfigureAwait(false)
                                : message.BodyCase is ProviderMessage.BodyOneofCase.InputDownload
                                    ? await IssueInputDownloadAsync(executionUnitId, message.InputDownload, response, responseGate, cancellationToken).ConfigureAwait(false)
                                : true;
        if (!accepted) throw new RpcException(new Status(StatusCode.FailedPrecondition, "The task handle is unknown, revoked, or not owned by this provider."));
        if (message.BodyCase is ProviderMessage.BodyOneofCase.Completed)
        {
            await WriteAsync(response, responseGate, new ServerMessage { Completion = new CompletionAccepted { TaskId = message.Completed.TaskId } }, cancellationToken).ConfigureAwait(false);
        }
        if (message.BodyCase is ProviderMessage.BodyOneofCase.Accepted)
        {
            taskFibers.Start(executionUnitId, ParseTaskId(message.Accepted.TaskId), ParseAttemptId(message.Accepted.AttemptId));
            await taskFibers.OperationAsync(executionUnitId, ParseTaskId(message.Accepted.TaskId), ParseAttemptId(message.Accepted.AttemptId), "task-acceptance", "accept assignment", cancellationToken).ConfigureAwait(false);
        }
        else if (message.BodyCase is ProviderMessage.BodyOneofCase.Progress)
        {
            var phase = String.IsNullOrWhiteSpace(message.Progress.Phase) ? "provider work" : message.Progress.Phase;
            await taskFibers.OperationAsync(executionUnitId, ParseTaskId(message.Progress.TaskId), ParseAttemptId(message.Progress.AttemptId), "task-phase", $"{phase} {message.Progress.Percent:0}%", cancellationToken).ConfigureAwait(false);
        }
        else if (message.BodyCase is ProviderMessage.BodyOneofCase.ResultUpload)
        {
            await taskFibers.OperationAsync(executionUnitId, ParseTaskId(message.ResultUpload.TaskId), ParseAttemptId(message.ResultUpload.AttemptId), "task-result-upload", "authorize result upload", cancellationToken).ConfigureAwait(false);
        }
        else if (message.BodyCase is ProviderMessage.BodyOneofCase.InputDownload)
        {
            await taskFibers.OperationAsync(executionUnitId, ParseTaskId(message.InputDownload.TaskId), ParseAttemptId(message.InputDownload.AttemptId), "task-input-download", "authorize input download", cancellationToken).ConfigureAwait(false);
        }
        else if (message.BodyCase is ProviderMessage.BodyOneofCase.Completed)
        {
            await taskFibers.OperationAsync(executionUnitId, ParseTaskId(message.Completed.TaskId), ParseAttemptId(message.Completed.AttemptId), "task-completion", "record completion", cancellationToken).ConfigureAwait(false);
            await taskFibers.CompleteAsync(executionUnitId, ParseTaskId(message.Completed.TaskId), ParseAttemptId(message.Completed.AttemptId)).ConfigureAwait(false);
        }
        else if (message.BodyCase is ProviderMessage.BodyOneofCase.Failed)
        {
            await taskFibers.OperationAsync(executionUnitId, ParseTaskId(message.Failed.TaskId), ParseAttemptId(message.Failed.AttemptId), "task-failure", $"record failure: {message.Failed.Step}", cancellationToken).ConfigureAwait(false);
            await taskFibers.CompleteAsync(executionUnitId, ParseTaskId(message.Failed.TaskId), ParseAttemptId(message.Failed.AttemptId)).ConfigureAwait(false);
        }
    }

    private async Task WriteAssignmentsAsync(ProviderSessionLease lease, IServerStreamWriter<ServerMessage> response, SemaphoreSlim responseGate, CancellationToken cancellationToken)
    {
        await foreach (var assignment in lease.Assignments.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var wire = new TaskAssignment
            {
                TaskId = assignment.TaskId.Value.ToString("D"),
                AttemptId = assignment.AttemptId.Value.ToString("D"),
                TaskHandle = assignment.Handle,
            };
            foreach (var (key, value) in assignment.Scalars) wire.Scalars[key] = value;
            if (assignment.Input is { } input)
            {
                var url = await store.CreateDownloadUrlAsync(
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
            await WriteAsync(response, responseGate, new ServerMessage { Assignment = wire }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> IssueUploadAuthorizationAsync(ExecutionUnitId unitId, ResultUploadRequest request, IServerStreamWriter<ServerMessage> response, SemaphoreSlim responseGate, CancellationToken cancellationToken)
    {
        var taskId = ParseTaskId(request.TaskId);
        var attemptId = ParseAttemptId(request.AttemptId);
        if (!assignments.TryGet(unitId, taskId, attemptId, request.TaskHandle, out var task) ||
            task.Attempts.SingleOrDefault(attempt => attempt.Id == attemptId)?.State is not AttemptState.Accepted) return false;
        var authorization = uploads.Issue(unitId, taskId, attemptId, request.TaskHandle, DateTimeOffset.UtcNow);
        await WriteAsync(response, responseGate, new ServerMessage { ResultUpload = new MutualGPU.Protocol.ResultUploadAuthorization { UploadToken = authorization.Token } }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> IssueInputDownloadAsync(ExecutionUnitId unitId, InputDownloadRequest request, IServerStreamWriter<ServerMessage> response, SemaphoreSlim responseGate, CancellationToken cancellationToken)
    {
        var taskId = ParseTaskId(request.TaskId); var attemptId = ParseAttemptId(request.AttemptId);
        if (!assignments.TryGet(unitId, taskId, attemptId, request.TaskHandle, out var task) || task.Parameters.Image is null) return false;
        ObjectKey? input = task.Parameters.ImageExtension is { Length: > 0 } extension
            ? keys.TaskInput(task.RequestorId, task.Id, task.Parameters.Image.Value, extension)
            : null;
        // Snapshots written before image metadata was added remain readable during a
        // rolling demo upgrade. New tasks use the deterministic key above and make no
        // object-store listing call on this hot path.
        if (input is null)
        {
            await foreach (var entry in store.ListAsync(keys.TaskInputs(task.RequestorId, task.Id), cancellationToken).ConfigureAwait(false))
            {
                if (entry.Key.Value.Contains(task.Parameters.Image.Value.Value.ToString("N"), StringComparison.Ordinal)) { input = entry.Key; break; }
            }
        }
        if (input is null) return false;
        var url = await store.CreateDownloadUrlAsync(input.Value, TimeSpan.FromMinutes(15), cancellationToken).ConfigureAwait(false);
        await WriteAsync(response, responseGate, new ServerMessage { InputDownload = new InputDownloadAuthorization { Url = url.ToString() } }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task WriteAsync(IServerStreamWriter<ServerMessage> response, SemaphoreSlim gate, ServerMessage message, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await response.WriteAsync(message).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private static TaskId ParseTaskId(string value) => Guid.TryParse(value, out var id)
        ? new TaskId(id) : throw new RpcException(new Status(StatusCode.InvalidArgument, "Task ID is invalid."));

    private static AttemptId ParseAttemptId(string value) => Guid.TryParse(value, out var id)
        ? new AttemptId(id) : throw new RpcException(new Status(StatusCode.InvalidArgument, "Attempt ID is invalid."));

    private static string MessageName(ProviderMessage message) => message.BodyCase switch
    {
        ProviderMessage.BodyOneofCase.Accepted => "accept-assignment",
        ProviderMessage.BodyOneofCase.Rejected => "reject-assignment",
        ProviderMessage.BodyOneofCase.Progress => "report-progress",
        ProviderMessage.BodyOneofCase.ResultUpload => "authorize-result-upload",
        ProviderMessage.BodyOneofCase.InputDownload => "authorize-input-download",
        ProviderMessage.BodyOneofCase.Completed => "complete-assignment",
        ProviderMessage.BodyOneofCase.Failed => "fail-assignment",
        _ => "process-provider-message",
    };

    private async Task<ExecutionUnitId> AuthenticateAsync(string? authorization, CancellationToken cancellationToken)
    {
        var value = authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) is true ? authorization[7..] : authorization;
        var executionUnitId = await authenticator.AuthenticateAsync(value, cancellationToken).ConfigureAwait(false);
        return executionUnitId ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "Provider authentication failed."));
    }
}
