using System.Security.Claims;
using System.Security.Cryptography;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public sealed record SetTeamWebhookRequest(string? Url, string? SecretBase64);
public sealed record TeamWebhookSettingsResponse(string? Url, bool SecretConfigured, DateTimeOffset? UpdatedAt);

public static class TeamWebhookSettingsEndpoints {
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
        return Results.Ok(new TeamWebhookSettingsResponse(settings?.Url, settings is not null, settings?.UpdatedAt));
    }

    public static async Task<IResult> Set(
        string slug,
        SetTeamWebhookRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
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
            db.TeamWebhookSettings.Add(TeamWebhookSettings.Create(access.Team!.Id, uri.AbsoluteUri, encryptedSecret, now));
        } else {
            settings.Update(uri.AbsoluteUri, encryptedSecret, now);
        }
        AuditLog.Write(db, now, access.User!.Id, access.User.Email ?? access.User.DisplayName,
            "team.webhook_updated", "TeamWebhook", access.Team!.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new TeamWebhookSettingsResponse(uri.AbsoluteUri, true, now));
    }

    public static async Task<IResult> Rewrap(
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
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
            AuditLog.Write(db, now, identity.User!.Id, identity.User.Email ?? identity.User.DisplayName,
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
