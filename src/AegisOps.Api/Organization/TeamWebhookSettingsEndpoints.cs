using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Application.Audit;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public sealed record SetTeamWebhookRequest(string? Url, string? SecretBase64, string[]? EventTypes = null);
public sealed record TeamWebhookSettingsResponse(string? Url, bool SecretConfigured, DateTimeOffset? UpdatedAt, string[] EventTypes);
public sealed record TeamWebhookAttemptResponse(int AttemptNumber, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string Status, string? Error);
public sealed record TeamWebhookDeliveryResponse(Guid EventId, string EventType, DateTimeOffset OccurredAt, string Status, int Attempts, int MaxAttempts, DateTimeOffset? CompletedAt, string? LastError, IReadOnlyList<TeamWebhookAttemptResponse>? AttemptHistory = null);

public static class TeamWebhookSettingsEndpoints {
    public static async Task<IResult> Deliveries(
        string slug, ClaimsPrincipal principal, UserManager<User> users, AegisOpsDbContext db, HttpContext http
    ) {
        var access = await FindAuthorizedTeam(slug, principal, users, db, http.RequestAborted);
        if (access.Error is not null) return access.Error;
        var eventQuery = from item in db.DeploymentEvents.AsNoTracking()
                         join deployment in db.Deployments.AsNoTracking() on item.DeploymentId equals deployment.Id
                         join project in db.Projects.AsNoTracking() on deployment.ProjectId equals project.Id
                         where project.TeamId == access.Team!.Id
                         select new { item.Id, item.Type, item.Timestamp };
        var events = db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true
            ? (await eventQuery.ToListAsync(http.RequestAborted)).OrderByDescending(item => item.Timestamp).Take(100).ToList()
            : await eventQuery.OrderByDescending(item => item.Timestamp).Take(100).ToListAsync(http.RequestAborted);
        if (events.Count == 0) return Results.Ok(Array.Empty<TeamWebhookDeliveryResponse>());
        var eventIds = events.Select(item => item.Id).ToHashSet();
        var jobsQuery = db.Jobs.AsNoTracking().Where(job => job.Type == AegisOps.Domain.Jobs.JobType.SendNotification)
            .Select(job => new { job.Id, job.Payload, job.Status, job.Attempts, job.MaxAttempts, job.CompletedAt, job.LastError, job.CreatedAt });
        var jobs = db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true
            ? await jobsQuery.Take(1000).ToListAsync(http.RequestAborted)
            : await jobsQuery.OrderByDescending(job => job.CreatedAt).Take(1000).ToListAsync(http.RequestAborted);
        var jobIdsByEvent = new Dictionary<Guid, Guid>();
        foreach (var job in jobs) {
            try {
                var eventId = JsonDocument.Parse(job.Payload).RootElement.GetProperty("eventId").GetGuid();
                if (eventIds.Contains(eventId) && !jobIdsByEvent.ContainsKey(eventId)) jobIdsByEvent[eventId] = job.Id;
            } catch (JsonException) { }
        }
        var attempts = await db.WebhookDeliveryAttempts.AsNoTracking()
            .Where(attempt => jobIdsByEvent.Values.Contains(attempt.JobId))
            .OrderByDescending(attempt => attempt.AttemptNumber)
            .Select(attempt => new { attempt.JobId, attempt.AttemptNumber, attempt.StartedAt, attempt.CompletedAt, attempt.Status, attempt.Error })
            .ToListAsync(http.RequestAborted);
        var attemptsByJob = attempts.GroupBy(attempt => attempt.JobId).ToDictionary(group => group.Key, group => group.ToArray());
        var jobsById = jobs.ToDictionary(job => job.Id);
        return Results.Ok(events.Select(item => {
            if (!jobIdsByEvent.TryGetValue(item.Id, out var jobId) || !jobsById.TryGetValue(jobId, out var job))
                return new TeamWebhookDeliveryResponse(item.Id, item.Type.ToString(), item.Timestamp, "NotQueued", 0, 0, null, null);
            var history = attemptsByJob.TryGetValue(jobId, out var records)
                ? records.OrderBy(record => record.AttemptNumber).Select(record => new TeamWebhookAttemptResponse(record.AttemptNumber, record.StartedAt, record.CompletedAt, record.Status, record.Error)).ToArray()
                : Array.Empty<TeamWebhookAttemptResponse>();
            return new TeamWebhookDeliveryResponse(item.Id, item.Type.ToString(), item.Timestamp,
                job.Status.ToString(), job.Attempts, job.MaxAttempts,
                job.CompletedAt, job.LastError, history);
        }));
    }

    public static async Task<IResult> Get(
        string slug,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http
    ) {
        var access = await FindAuthorizedTeam(slug, principal, users, db, http.RequestAborted);
        if (access.Error is not null) return access.Error;
        var settings = await db.TeamWebhookSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TeamId == access.Team!.Id, http.RequestAborted);
        return Results.Ok(new TeamWebhookSettingsResponse(settings?.Url, settings is not null, settings?.UpdatedAt, ParseEventTypes(settings?.EventTypes)));
    }

    public static async Task<IResult> Set(
        string slug,
        SetTeamWebhookRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        IAuditWriter audit,
        TeamWebhookSecretProtector protector,
        HttpContext http
    ) {
        var access = await FindAuthorizedTeam(slug, principal, users, db, http.RequestAborted);
        if (access.Error is not null) return access.Error;
        if (!protector.IsEnabled || !protector.IsConfigured) {
            return Results.Problem(title: "Webhook secret protection is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (!TeamWebhookSender.TryValidateUrl(request.Url, out var uri) || !string.IsNullOrEmpty(uri.Query)) {
            return Results.Problem(title: "A public HTTPS URL on port 443 is required.", statusCode: StatusCodes.Status400BadRequest);
        }
        var selectedEventTypes = request.EventTypes ?? Enum.GetNames<DeploymentEventType>();
        if (selectedEventTypes.Length == 0 || selectedEventTypes.Distinct(StringComparer.Ordinal).Count() != selectedEventTypes.Length ||
            selectedEventTypes.Any(value => !Enum.TryParse<DeploymentEventType>(value, false, out var parsed) || parsed.ToString() != value)) {
            return Results.Problem(title: "EventTypes must contain one or more valid deployment event names.", statusCode: StatusCodes.Status400BadRequest);
        }
        var eventTypes = string.Join(',', selectedEventTypes.Order(StringComparer.Ordinal));
        if (request.SecretBase64 is null || request.SecretBase64.Length > 1368) {
            return Results.Problem(title: "SecretBase64 must contain 32 to 1024 bytes.", statusCode: StatusCodes.Status400BadRequest);
        }
        byte[] secret;
        try {
            secret = Convert.FromBase64String(request.SecretBase64 ?? string.Empty);
        } catch (FormatException) {
            return Results.Problem(title: "SecretBase64 must be valid base64 containing at least 32 bytes.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (secret.Length is < 32 or > 1024) {
            CryptographicOperations.ZeroMemory(secret);
            return Results.Problem(title: "SecretBase64 must contain 32 to 1024 bytes.", statusCode: StatusCodes.Status400BadRequest);
        }

        string encryptedSecret;
        try {
            encryptedSecret = protector.Protect(access.Team!.Id, secret);
        } finally {
            CryptographicOperations.ZeroMemory(secret);
        }

        var now = TimeProvider.System.GetUtcNow();
        var settings = await db.TeamWebhookSettings.SingleOrDefaultAsync(item => item.TeamId == access.Team!.Id, http.RequestAborted);
        if (settings is null) {
            db.TeamWebhookSettings.Add(TeamWebhookSettings.Create(access.Team!.Id, uri.AbsoluteUri, encryptedSecret, now, eventTypes));
        } else {
            settings.Update(uri.AbsoluteUri, encryptedSecret, now, eventTypes);
        }
        audit.Write(now, access.User!.Id, access.User.Email ?? access.User.DisplayName,
            "team.webhook_updated", "TeamWebhook", access.Team!.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new TeamWebhookSettingsResponse(uri.AbsoluteUri, true, now, ParseEventTypes(eventTypes)));
    }

    private static string[] ParseEventTypes(string? eventTypes) => eventTypes is null || eventTypes == "*"
        ? Enum.GetNames<DeploymentEventType>()
        : eventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static async Task<IResult> Rewrap(
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        IAuditWriter audit,
        TeamWebhookSecretProtector protector,
        HttpContext http
    ) {
        var identity = await FindActiveUser(principal, users);
        if (identity.Error is not null) return identity.Error;
        if (!await users.IsInRoleAsync(identity.User!, "Admin")) {
            return Results.Forbid();
        }
        if (!protector.IsConfigured) {
            return Results.Problem(title: "Webhook secret protection is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var settings = await db.TeamWebhookSettings.ToListAsync(http.RequestAborted);
        (TeamWebhookSettings Settings, string Encrypted)[] rewrites;
        try {
            rewrites = settings.Select(item => (Settings: item, Encrypted: protector.Rewrap(item.TeamId, item.EncryptedSecret))).ToArray();
        } catch (CryptographicException) {
            return Results.Problem(title: "Stored webhook secrets could not be rewrapped; existing settings were unchanged.",
                statusCode: StatusCodes.Status409Conflict);
        }
        var now = TimeProvider.System.GetUtcNow();
        var changed = 0;
        foreach (var (item, encrypted) in rewrites) {
            if (item.RewrapSecret(encrypted, now)) changed++;
        }
        if (changed > 0) {
            audit.Write(now, identity.User!.Id, identity.User.Email ?? identity.User.DisplayName,
                "team.webhook_keys_rewrapped", "TeamWebhook", null);
            await db.SaveChangesAsync(http.RequestAborted);
        }
        return Results.Ok(new { Rewrapped = changed });
    }

    private static async Task<(User? User, Team? Team, IResult? Error)> FindAuthorizedTeam(
        string slug,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        CancellationToken cancellationToken
    ) {
        var identity = await FindActiveUser(principal, users);
        if (identity.Error is not null) return (null, null, identity.Error);
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var team = await db.Teams.SingleOrDefaultAsync(item => item.Slug == normalizedSlug, cancellationToken);
        if (team is null) return (null, null, Results.Problem(title: "Team was not found.", statusCode: StatusCodes.Status404NotFound));

        var isAdmin = await users.IsInRoleAsync(identity.User!, "Admin");
        var isOwner = await db.TeamMembers.AnyAsync(member =>
            member.TeamId == team.Id && member.UserId == identity.User!.Id && member.Role == TeamRole.Owner, cancellationToken);
        return isAdmin || isOwner
            ? (identity.User, team, null)
            : (null, null, Results.Problem(title: "Team was not found.", statusCode: StatusCodes.Status404NotFound));
    }

    private static async Task<(User? User, IResult? Error)> FindActiveUser(ClaimsPrincipal principal, UserManager<User> users) {
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out _) ) {
            return (null, Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized));
        }
        var user = await users.FindByIdAsync(subject!);
        return user is null || !user.IsActive
            ? (null, Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized))
            : (user, null);
    }
}
