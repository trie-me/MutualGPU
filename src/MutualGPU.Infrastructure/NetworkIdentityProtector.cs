using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MutualGPU.Infrastructure;

/// <summary>
/// Produces stable administrative correlation values without persisting a raw IP address.
/// </summary>
public sealed class NetworkIdentityProtector
{
    // Preserve the v1 requestor hash domain so values already emitted to CloudWatch
    // continue to correlate after the protection logic is shared with assignments.
    private static readonly byte[] KeyDomain = "mutualgpu/requestor-ip-hash/v1\0"u8.ToArray();
    private readonly byte[] ipHashKey;

    public NetworkIdentityProtector(string? configuredKey)
    {
        ipHashKey = String.IsNullOrWhiteSpace(configuredKey)
            ? RandomNumberGenerator.GetBytes(32)
            : SHA256.HashData([.. KeyDomain, .. Encoding.UTF8.GetBytes(configuredKey)]);
    }

    public ProtectedNetworkIdentity Protect(IPAddress? address)
    {
        if (address is null) return ProtectedNetworkIdentity.Unknown;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var digest = HMACSHA256.HashData(ipHashKey, address.GetAddressBytes());
        return new ProtectedNetworkIdentity(
            Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant(),
            ClassABSegment(address));
    }

    public ProtectedNetworkIdentity Protect(string? address) =>
        IPAddress.TryParse(address, out var parsed) ? Protect(parsed) : ProtectedNetworkIdentity.Unknown;

    public static string ClassABSegment(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            ? $"{bytes[0]}.{bytes[1]}.*.*"
            : $"{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:*";
    }
}

public sealed record ProtectedNetworkIdentity(string IpHash, string IpClassAB)
{
    public static ProtectedNetworkIdentity Unknown { get; } = new("unknown", "unknown");
}
