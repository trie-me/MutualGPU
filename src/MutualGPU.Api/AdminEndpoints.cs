using MutualGPU.Application;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;

namespace MutualGPU.Api;

public static class AdminEndpoints
{
    public static void MapAdmin(this WebApplication app)
    {
        app.MapGet("/admin", () => Results.Redirect("/admin/index.html", permanent: false));
        app.MapPost("/admin/api/login", Login);
        app.MapPost("/admin/api/logout", Logout);
        app.MapGet("/admin/api/overview", Overview);
        app.MapGet("/admin/api/partner-resources/pending", PendingPartnerResources);
        app.MapPost("/admin/api/partner-resources/{id:guid}/approve", ApprovePartnerResource);
    }

    private static IResult Login(HttpContext context, AdminLoginRequest request, AdminAccessService access)
    {
        NoStore(context.Response);
        var clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = access.Login(request.Password, clientKey);
        if (result.Status is AdminLoginStatus.Granted)
        {
            context.Response.Cookies.Append(AdminAccessService.CookieName, result.Token!, Cookie(result.ExpiresAt));
            return Results.Ok(new { authenticated = true, expiresAt = result.ExpiresAt });
        }
        return result.Status switch
        {
            AdminLoginStatus.RateLimited => Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many login attempts."),
            AdminLoginStatus.Disabled => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Administrator access is not configured."),
            _ => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid administrator credentials."),
        };
    }

    private static IResult Logout(HttpContext context, AdminAccessService access)
    {
        NoStore(context.Response);
        access.Logout(context.Request.Cookies[AdminAccessService.CookieName]);
        context.Response.Cookies.Delete(AdminAccessService.CookieName, Cookie(DateTimeOffset.UnixEpoch));
        return Results.NoContent();
    }

    private static async Task<IResult> Overview(
        HttpContext context,
        AdminAccessService access,
        IAdminTaskReader tasks,
        ProviderConnectionRegistry connections,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        NoStore(context.Response);
        if (!access.IsAuthorized(context.Request.Cookies[AdminAccessService.CookieName])) return Results.Unauthorized();

        var taskRequests = await tasks.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = connections.Snapshot();
        var transactions = taskRequests.Select(static task => new AdminTransactionDto(
            task.Id.Value,
            task.RequestorId.Value,
            task.Capability.Name,
            task.CreatedAt,
            task.Status.ToString().ToLowerInvariant(),
            task.Resources.ComputeTier.ToString(),
            task.Resources.MemoryGiB,
            task.AssignmentCount,
            task.Result is not null)).ToArray();
        var assignments = taskRequests.SelectMany(task => task.Attempts.Select(attempt => new AdminAssignmentDto(
            task.Id.Value,
            attempt.Id.Value,
            diagnostics.AssignmentSessions.GetValueOrDefault(attempt.Id.Value),
            attempt.ExecutionUnitId.Value,
            task.Capability.Name,
            attempt.State.ToString().ToLowerInvariant(),
            attempt.AssignedAt,
            attempt.AcceptedAt,
            attempt.DisconnectedAt,
            SafeText(attempt.FailureStep),
            SafeText(attempt.FailureReason)))).OrderByDescending(static attempt => attempt.AssignedAt).ToArray();
        var now = timeProvider.GetUtcNow();
        return Results.Ok(new AdminOverviewDto(
            now,
            new AdminOverviewSummaryDto(
                diagnostics.Sessions.Count(static session => session.Status == "connected"),
                diagnostics.Sessions.Count,
                transactions.Length,
                transactions.Count(static transaction => transaction.Status == "running"),
                assignments.Length,
                assignments.Count(static assignment => assignment.State is "failed" or "rejected" or "revoked")),
            diagnostics.Sessions,
            transactions,
            assignments));
    }

    private static async Task<IResult> PendingPartnerResources(
        HttpContext context,
        AdminAccessService access,
        IPartnerResourceRegistry registry,
        CancellationToken cancellationToken)
    {
        NoStore(context.Response);
        if (!access.IsAuthorized(context.Request.Cookies[AdminAccessService.CookieName])) return Results.Unauthorized();
        var requests = await registry.ListPendingAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(requests.Select(static request => PartnerResourceRequestDto.From(request)).ToArray());
    }

    private static async Task<IResult> ApprovePartnerResource(
        HttpContext context,
        Guid id,
        AdminAccessService access,
        IPartnerResourceRegistry registry,
        CancellationToken cancellationToken)
    {
        NoStore(context.Response);
        if (!access.IsAuthorized(context.Request.Cookies[AdminAccessService.CookieName])) return Results.Unauthorized();
        var approved = await registry.ApproveAsync(id, cancellationToken).ConfigureAwait(false);
        return approved is null ? Results.NotFound() : Results.Ok(PartnerResourceRequestDto.From(approved));
    }

    private static CookieOptions Cookie(DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/admin",
        Expires = expiresAt,
        IsEssential = true,
    };

    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, private";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static string? SafeText(string? value)
    {
        if (String.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private sealed record AdminLoginRequest(string? Password);
}

public sealed record AdminOverviewDto(
    DateTimeOffset GeneratedAt,
    AdminOverviewSummaryDto Summary,
    IReadOnlyList<AdminSessionSnapshot> Sessions,
    IReadOnlyList<AdminTransactionDto> Transactions,
    IReadOnlyList<AdminAssignmentDto> Assignments);

public sealed record AdminOverviewSummaryDto(
    int ConnectedSessions,
    int RetainedSessions,
    int Transactions,
    int RunningTransactions,
    int Assignments,
    int UnsuccessfulAssignments);

public sealed record AdminTransactionDto(
    Guid TaskId,
    Guid RequestorId,
    string Capability,
    DateTimeOffset CreatedAt,
    string Status,
    string ComputeTier,
    int MemoryGiB,
    int AttemptCount,
    bool HasResult);

public sealed record AdminAssignmentDto(
    Guid TaskId,
    Guid AttemptId,
    Guid? SessionId,
    Guid ExecutionUnitId,
    string Capability,
    string State,
    DateTimeOffset AssignedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? DisconnectedAt,
    string? FailureStep,
    string? FailureReason);
