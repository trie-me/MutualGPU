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
        app.MapGet("/admin/api/partner-resources/approved", ApprovedPartnerResources);
        app.MapPost("/admin/api/partner-resources/{id:guid}/approve", ApprovePartnerResource);
        app.MapPost("/admin/api/partner-resources/{id:guid}/revoke", RevokePartnerResource);
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
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        NoStore(context.Response);
        if (!access.IsAuthorized(context.Request.Cookies[AdminAccessService.CookieName])) return Results.Unauthorized();

        PageResult<TaskRequest> taskPage;
        try
        {
            taskPage = await tasks.GetPageAsync(new PageRequest(limit ?? 100, cursor), cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { code = "pagination_cursor_invalid" });
        }
        if (taskPage.NextCursor is not null)
        {
            context.Response.Headers["X-MutualGPU-Next-Cursor"] = taskPage.NextCursor;
        }
        var taskRequests = taskPage.Items;
        var diagnostics = connections.Snapshot();
        var sessions = MergeSessions(diagnostics.Sessions, taskRequests);
        var transactions = taskRequests.Select(static task => new AdminTransactionDto(
            task.Id.Value,
            task.RequestorId.Value,
            task.Capability.Name,
            task.CreatedAt,
            task.Status.ToString().ToLowerInvariant(),
            task.Resources.ComputeTier.ToString(),
            task.Resources.MemoryGiB,
            task.AssignmentCount,
            task.Result is not null,
            task.Parameters.RequestorIpHash,
            task.Parameters.RequestorIpClassAB)).ToArray();
        var sessionsById = sessions.ToDictionary(static session => session.SessionId);
        var assignments = taskRequests.SelectMany(task => task.Attempts.Select(attempt =>
        {
            var sessionId = attempt.ProviderSessionId ??
                (diagnostics.AssignmentSessions.TryGetValue(attempt.Id.Value, out var retainedSessionId) ? retainedSessionId : null);
            sessionsById.TryGetValue(sessionId ?? Guid.Empty, out var retainedSession);
            return new AdminAssignmentDto(
                task.Id.Value,
                attempt.Id.Value,
                sessionId,
                attempt.ExecutionUnitId.Value,
                task.Capability.Name,
                attempt.State.ToString().ToLowerInvariant(),
                attempt.AssignedAt,
                attempt.AcceptedAt,
                attempt.DisconnectedAt,
                SafeText(attempt.FailureStep),
                SafeText(attempt.FailureReason),
                task.RequestorId.Value,
                task.Parameters.RequestorIpHash,
                task.Parameters.RequestorIpClassAB,
                attempt.ProviderIpHash ?? retainedSession?.IpHash,
                attempt.ProviderIpClassAB ?? retainedSession?.IpClassAB,
                attempt.ProviderName ?? retainedSession?.ProviderName,
                attempt.ProviderTransport ?? retainedSession?.Transport);
        })).OrderByDescending(static attempt => attempt.AssignedAt).ToArray();
        var now = timeProvider.GetUtcNow();
        return Results.Ok(new AdminOverviewDto(
            now,
            new AdminOverviewSummaryDto(
                diagnostics.Sessions.Count(static session => session.Status == "connected"),
                sessions.Count,
                transactions.Length,
                transactions.Count(static transaction => transaction.Status == "running"),
                assignments.Length,
                assignments.Count(static assignment => assignment.State is "failed" or "rejected" or "revoked")),
            sessions,
            transactions,
            assignments));
    }

    private static IReadOnlyList<AdminSessionSnapshot> MergeSessions(
        IReadOnlyList<AdminSessionSnapshot> liveSessions,
        IReadOnlyList<TaskRequest> tasks)
    {
        var retainedIds = liveSessions.Select(static session => session.SessionId).ToHashSet();
        var historical = tasks
            .SelectMany(task => task.Attempts.Select(attempt => (task, attempt)))
            .Where(item => item.attempt.ProviderSessionId is { } sessionId && !retainedIds.Contains(sessionId))
            .GroupBy(static item => item.attempt.ProviderSessionId!.Value)
            .Select(static group => HistoricalSession(group.Key, group.ToArray()));
        return liveSessions
            .Concat(historical)
            .OrderByDescending(static session => session.ConnectedAt)
            .ToArray();
    }

    private static AdminSessionSnapshot HistoricalSession(
        Guid sessionId,
        IReadOnlyList<(TaskRequest task, TaskAttempt attempt)> assignments)
    {
        var first = assignments.OrderBy(static item => item.attempt.AssignedAt).First();
        var events = assignments
            .SelectMany(static item => HistoricalEvents(item.task, item.attempt))
            .OrderBy(static item => item.OccurredAt)
            .ToArray();
        var closedAt = events.LastOrDefault()?.OccurredAt ?? first.attempt.AssignedAt;
        return new AdminSessionSnapshot(
            sessionId,
            first.attempt.ExecutionUnitId.Value,
            first.attempt.ProviderName,
            first.attempt.ProviderTransport ?? "unknown",
            null,
            first.attempt.ProviderIpHash ?? "unknown",
            first.attempt.ProviderIpClassAB ?? "unknown",
            first.attempt.AssignedAt,
            closedAt,
            "disconnected",
            "retained_from_assignment_facts",
            assignments.Count,
            new AdminSessionSummary(
                events.Length,
                assignments.Count,
                assignments.Count(static item => item.attempt.AcceptedAt is not null),
                assignments.Count(static item => item.attempt.State is AttemptState.Rejected),
                assignments.Count(static item => item.attempt.State is AttemptState.Failed),
                assignments.Count(static item => item.attempt.State is AttemptState.Completed),
                0),
            events);
    }

    private static IEnumerable<AdminSessionEvent> HistoricalEvents(TaskRequest task, TaskAttempt attempt)
    {
        yield return new AdminSessionEvent(
            attempt.Id.Value,
            attempt.AssignedAt,
            "assignment_created",
            "Task assigned to provider.",
            task.Id.Value,
            attempt.Id.Value);
        if (attempt.State is AttemptState.Assigned) yield break;
        var occurredAt = attempt.DisconnectedAt ?? attempt.AcceptedAt ?? attempt.AssignedAt;
        yield return new AdminSessionEvent(
            task.Id.Value,
            occurredAt,
            attempt.State.ToString().ToLowerInvariant(),
            HistoricalEventDetail(attempt),
            task.Id.Value,
            attempt.Id.Value);
    }

    private static string HistoricalEventDetail(TaskAttempt attempt) => attempt.State switch
    {
        AttemptState.Accepted => "Provider accepted the assignment.",
        AttemptState.Rejected => SafeText(attempt.FailureReason) ?? "Provider rejected the assignment.",
        AttemptState.Disconnected => "Provider disconnected while the assignment was active.",
        AttemptState.Revoked => SafeText(attempt.FailureReason) ?? "Assignment was revoked.",
        AttemptState.Cancelled => SafeText(attempt.FailureReason) ?? "Assignment was cancelled.",
        AttemptState.Completed => "Assignment completed.",
        AttemptState.Failed => SafeText(attempt.FailureReason) ?? "Provider reported an assignment failure.",
        _ => "Assignment state retained from durable task facts.",
    };

    private static async Task<IResult> PendingPartnerResources(
        HttpContext context,
        AdminAccessService access,
        IPartnerResourceRegistry registry,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        NoStore(context.Response);
        if (!access.IsAuthorized(context.Request.Cookies[AdminAccessService.CookieName])) return Results.Unauthorized();
        try
        {
            var requests = await registry
                .ListPendingPageAsync(new PageRequest(limit ?? 100, cursor), cancellationToken)
                .ConfigureAwait(false);
            if (requests.NextCursor is not null)
                context.Response.Headers["X-MutualGPU-Next-Cursor"] = requests.NextCursor;
            return Results.Ok(requests.Items.Select(static request => PartnerResourceRequestDto.From(request)).ToArray());
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { code = "pagination_cursor_invalid" });
        }
    }

    private static async Task<IResult> ApprovePartnerResource(
        HttpContext context,
        Guid id,
        AdminAccessService access,
        IPartnerResourceRegistry registry,
        BrowserObjectCorsSynchronizer browserObjectCors,
        CancellationToken cancellationToken)
    {
        NoStore(context.Response);
        if (!access.IsAuthorized(context.Request.Cookies[AdminAccessService.CookieName])) return Results.Unauthorized();
        var approved = await registry.ApproveAsync(id, cancellationToken).ConfigureAwait(false);
        if (approved is not null) await browserObjectCors.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return approved is null ? Results.NotFound() : Results.Ok(PartnerResourceRequestDto.From(approved));
    }

    private static async Task<IResult> ApprovedPartnerResources(
        HttpContext context,
        AdminAccessService access,
        IPartnerResourceRegistry registry,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        NoStore(context.Response);
        if (!access.IsAuthorized(context.Request.Cookies[AdminAccessService.CookieName])) return Results.Unauthorized();
        try
        {
            var requests = await registry
                .ListApprovedPageAsync(new PageRequest(limit ?? 100, cursor), cancellationToken)
                .ConfigureAwait(false);
            if (requests.NextCursor is not null)
                context.Response.Headers["X-MutualGPU-Next-Cursor"] = requests.NextCursor;
            return Results.Ok(requests.Items.Select(static request => PartnerResourceRequestDto.From(request)).ToArray());
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { code = "pagination_cursor_invalid" });
        }
    }

    private static async Task<IResult> RevokePartnerResource(
        HttpContext context,
        Guid id,
        AdminAccessService access,
        IPartnerResourceRegistry registry,
        BrowserObjectCorsSynchronizer browserObjectCors,
        CancellationToken cancellationToken)
    {
        NoStore(context.Response);
        if (!access.IsAuthorized(context.Request.Cookies[AdminAccessService.CookieName])) return Results.Unauthorized();
        var revoked = await registry.RevokeAsync(id, cancellationToken).ConfigureAwait(false);
        if (revoked is not null) await browserObjectCors.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return revoked is null ? Results.NotFound() : Results.Ok(PartnerResourceRequestDto.From(revoked));
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
    bool HasResult,
    string? RequestorIpHash,
    string? RequestorIpClassAB);

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
    string? FailureReason,
    Guid RequestorId,
    string? RequestorIpHash,
    string? RequestorIpClassAB,
    string? ProviderIpHash,
    string? ProviderIpClassAB,
    string? ProviderName,
    string? ProviderTransport);
