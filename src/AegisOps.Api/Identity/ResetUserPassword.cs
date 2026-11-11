using System.Security.Claims;
using System.Security.Cryptography;
using AegisOps.Application.Audit;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Identity;

public sealed record ResetUserPasswordResponse(Guid UserId, string TemporaryPassword, bool MustChangePassword);

/// <summary>
/// docs/09: with no mail server, an Admin sets a temporary password and the user must change it
/// (<see cref="User.MustChangePassword"/>). The password is random, returned once with
/// <c>Cache-Control: no-store</c>, and never logged or audited. Lockout is cleared and every active refresh
/// token of the user is revoked so existing sessions end.
/// </summary>
public static class ResetUserPassword {
    public static async Task<IResult> Handle(
        Guid id,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        IAuditWriter audit,
        TimeProvider time,
        HttpContext http
    ) {
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out _)) {
            return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var actor = await users.FindByIdAsync(subject);
        if (actor is null || !actor.IsActive) {
            return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) {
            return Results.Problem(title: "User was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        // 18 random bytes → 24 Base64Url characters: above the 12-character policy, no composition rules needed.
        var temporary = Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
        if (await users.HasPasswordAsync(user)) {
            ThrowIfFailed(await users.RemovePasswordAsync(user));
        }
        ThrowIfFailed(await users.AddPasswordAsync(user, temporary));
        user.RequirePasswordChange();
        ThrowIfFailed(await users.UpdateAsync(user));
        ThrowIfFailed(await users.SetLockoutEndDateAsync(user, null));
        ThrowIfFailed(await users.ResetAccessFailedCountAsync(user));

        var now = time.GetUtcNow();
        var cancellationToken = http.RequestAborted;
        var sessions = await db.RefreshTokens
            .Where(token => token.UserId == user.Id && token.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions) {
            session.Revoke(now, null);
        }

        audit.Write(now, actor.Id, actor.Email ?? actor.DisplayName, "user.password_reset", "User", user.Id);
        await db.SaveChangesAsync(cancellationToken);

        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new ResetUserPasswordResponse(user.Id, temporary, true));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void ThrowIfFailed(IdentityResult result) {
        if (!result.Succeeded) {
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(error => error.Code)));
        }
    }
}
