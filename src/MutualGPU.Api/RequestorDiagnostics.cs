using System.Net;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;

namespace MutualGPU.Api;

/// <summary>Emits requestor correlation fields without placing a client IP address in logs.</summary>
public sealed class RequestorDiagnostics
{
    private readonly NetworkIdentityProtector networkIdentities;
    private readonly ILogger<RequestorDiagnostics> logger;

    public RequestorDiagnostics(string? configuredKey, ILogger<RequestorDiagnostics> logger)
        : this(new NetworkIdentityProtector(configuredKey), logger)
    {
    }

    public RequestorDiagnostics(NetworkIdentityProtector networkIdentities, ILogger<RequestorDiagnostics> logger)
    {
        this.networkIdentities = networkIdentities;
        this.logger = logger;
    }

    public ProtectedNetworkIdentity Describe(HttpContext context) =>
        networkIdentities.Protect(context.Connection.RemoteIpAddress);

    public void TaskOperation(
        HttpContext context,
        RequestorId requestorId,
        TaskId taskId,
        string operation,
        ProtectedNetworkIdentity? identity = null)
    {
        identity ??= Describe(context);
        logger.LogInformation(
            "requestor_task_operation {Operation} {RequestorId} {RequestorIpHash} {RequestorIpClassAB} {TaskId}",
            operation,
            requestorId.Value,
            identity.IpHash,
            identity.IpClassAB,
            taskId.Value);
    }

    internal string HashIpAddress(IPAddress? address) => networkIdentities.Protect(address).IpHash;

    internal static string ClassABSegment(IPAddress? address) =>
        NetworkIdentityProtector.ClassABSegment(address);
}
