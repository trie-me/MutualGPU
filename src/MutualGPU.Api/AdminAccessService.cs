using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MutualGPU.Api;

/// <summary>Exchanges the server-only master password for a short-lived opaque browser session.
/// Neither the password nor a reusable derivative is ever sent back to the browser.</summary>
public sealed class AdminAccessService
{
    public const string CookieName = "mutualgpu_admin";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);
    private const int MaximumFailuresPerWindow = 5;
    private readonly byte[]? expectedPasswordHash;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<string, DateTimeOffset> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FailedAttempts> failedAttempts = new(StringComparer.Ordinal);

    public AdminAccessService(string? masterPassword, TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        if (String.IsNullOrWhiteSpace(masterPassword)) return;
        if (Encoding.UTF8.GetByteCount(masterPassword) < 24)
            throw new InvalidOperationException("MutualGPU:Admin:MasterPassword must contain at least 24 UTF-8 bytes.");
        expectedPasswordHash = SHA256.HashData(Encoding.UTF8.GetBytes(masterPassword));
    }

    public bool IsConfigured => expectedPasswordHash is not null;

    public AdminLoginResult Login(string? password, string clientKey)
    {
        if (!IsConfigured) return AdminLoginResult.Disabled;
        var now = timeProvider.GetUtcNow();
        RemoveExpired(now);
        if (failedAttempts.TryGetValue(clientKey, out var failures) && failures.WindowStartedAt + AttemptWindow > now && failures.Count >= MaximumFailuresPerWindow)
            return AdminLoginResult.RateLimited;

        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(password ?? String.Empty));
        if (!CryptographicOperations.FixedTimeEquals(candidateHash, expectedPasswordHash!))
        {
            failedAttempts.AddOrUpdate(clientKey, _ => new FailedAttempts(now, 1), (_, current) =>
                current.WindowStartedAt + AttemptWindow <= now ? new FailedAttempts(now, 1) : current with { Count = current.Count + 1 });
            return AdminLoginResult.Denied;
        }

        failedAttempts.TryRemove(clientKey, out _);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        sessions[TokenKey(token)] = now + SessionLifetime;
        return new AdminLoginResult(AdminLoginStatus.Granted, token, now + SessionLifetime);
    }

    public bool IsAuthorized(string? token)
    {
        if (String.IsNullOrWhiteSpace(token)) return false;
        var key = TokenKey(token);
        if (!sessions.TryGetValue(key, out var expiresAt)) return false;
        if (expiresAt > timeProvider.GetUtcNow()) return true;
        sessions.TryRemove(key, out _);
        return false;
    }

    public void Logout(string? token)
    {
        if (!String.IsNullOrWhiteSpace(token)) sessions.TryRemove(TokenKey(token), out _);
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var item in sessions.Where(item => item.Value <= now)) sessions.TryRemove(item.Key, out _);
        foreach (var item in failedAttempts.Where(item => item.Value.WindowStartedAt + AttemptWindow <= now)) failedAttempts.TryRemove(item.Key, out _);
    }

    private static string TokenKey(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed record FailedAttempts(DateTimeOffset WindowStartedAt, int Count);
}

public enum AdminLoginStatus { Granted, Denied, RateLimited, Disabled }

public sealed record AdminLoginResult(AdminLoginStatus Status, string? Token = null, DateTimeOffset? ExpiresAt = null)
{
    public static AdminLoginResult Denied { get; } = new(AdminLoginStatus.Denied);
    public static AdminLoginResult RateLimited { get; } = new(AdminLoginStatus.RateLimited);
    public static AdminLoginResult Disabled { get; } = new(AdminLoginStatus.Disabled);
}
