using MutualGPU.Application;
using MutualGPU.Domain;
using MutualGPU.Protocol;

namespace MutualGPU.Api;

internal static class ProviderMessageLogging
{
    public static void Progress(
        ILogger logger,
        string transport,
        ExecutionUnitId executionUnitId,
        string taskId,
        string attemptId,
        ulong sequence,
        ProviderProgressDisposition disposition)
    {
        logger.LogInformation(
            "provider_progress_disposition {Transport} {ExecutionUnitId} {TaskId} {AttemptId} {Disposition} {Sequence}",
            transport,
            executionUnitId.Value,
            taskId,
            attemptId,
            disposition,
            sequence);
    }

    public static void Accepted(ILogger logger, ExecutionUnitId executionUnitId, ProviderMessage message)
    {
        var identity = message.BodyCase switch
        {
            ProviderMessage.BodyOneofCase.Accepted => (message.Accepted.TaskId, message.Accepted.AttemptId),
            ProviderMessage.BodyOneofCase.Rejected => (message.Rejected.TaskId, message.Rejected.AttemptId),
            ProviderMessage.BodyOneofCase.Failed => (message.Failed.TaskId, message.Failed.AttemptId),
            ProviderMessage.BodyOneofCase.Completed => (message.Completed.TaskId, message.Completed.AttemptId),
            ProviderMessage.BodyOneofCase.ResultUpload => (message.ResultUpload.TaskId, message.ResultUpload.AttemptId),
            ProviderMessage.BodyOneofCase.InputDownload => (message.InputDownload.TaskId, message.InputDownload.AttemptId),
            _ => default,
        };
        if (String.IsNullOrWhiteSpace(identity.TaskId) || String.IsNullOrWhiteSpace(identity.AttemptId)) return;

        logger.LogInformation(
            "Provider operation {ProviderOperation} accepted for task {TaskId}, attempt {AttemptId}, execution unit {ExecutionUnitId}.",
            message.BodyCase,
            identity.TaskId,
            identity.AttemptId,
            executionUnitId.Value);
    }

    public static void Failed(ILogger logger, string transport, ExecutionUnitId executionUnitId, TaskFailed failed)
    {
        logger.LogWarning(
            "provider_assignment_failed {Transport} {ExecutionUnitId} {TaskId} {AttemptId} {Step} {Reason}",
            transport,
            executionUnitId.Value,
            failed.TaskId,
            failed.AttemptId,
            failed.Step,
            failed.Reason);
    }
}
