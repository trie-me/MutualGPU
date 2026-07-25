using System.Net;
using System.Security.Cryptography;
using System.Text;
using MutualGPU.Domain;

namespace MutualGPU.Api;

/// <summary>Emits requestor correlation fields without placing a client IP address in logs.</summary>
public sealed class RequestorDiagnostics
{
    private static readonly byte[] KeyDomain = "mutualgpu/requestor-ip-hash/v1\0"u8.ToArray();
    private readonly byte[] ipHashKey;
    private readonly ILogger<RequestorDiagnostics> logger;

    public RequestorDiagnostics(string? configuredKey, ILogger<RequestorDiagnostics> logger)
    {
        this.logger = logger;
        ipHashKey = String.IsNullOrWhiteSpace(configuredKey)
            ? RandomNumberGenerator.GetBytes(32)
            : SHA256.HashData([.. KeyDomain, .. Encoding.UTF8.GetBytes(configuredKey)]);
    }

    public void TaskOperation(HttpContext context, RequestorId requestorId, TaskId taskId, string operation)
    {
        logger.LogInformation(
            "requestor_task_operation {Operation} {RequestorId} {RequestorIpHash} {RequestorIpClassAB} {TaskId}",
            operation,
            requestorId.Value,
            HashIpAddress(context.Connection.RemoteIpAddress),
            ClassABSegment(context.Connection.RemoteIpAddress),
            taskId.Value);
    }

    internal string HashIpAddress(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var digest = HMACSHA256.HashData(ipHashKey, address.GetAddressBytes());
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    internal static string ClassABSegment(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            ? $"{bytes[0]}.{bytes[1]}.*.*"
            : $"{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:*";
    }
}
