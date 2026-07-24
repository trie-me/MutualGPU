using MutualGPU.Domain;
using MutualGPU.Protocol;

namespace MutualGPU.Api;

internal static class ProviderMessageLogging
{
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
}
