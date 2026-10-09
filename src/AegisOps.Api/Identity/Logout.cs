using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;

namespace AegisOps.Api.Identity;

public static class Logout {
    public static async Task<IResult> Handle(
        HttpContext http,
        ClaimsPrincipal principal,
        AegisOpsDbContext db,
        TimeProvider time
    ) {
        var cancellationToken = http.RequestAborted;
        if (!http.Request.Cookies.TryGetValue(RefreshCookie.Name, out var raw) || string.IsNullOrWhiteSpace(raw)) {
            RefreshCookie.Clear(http);
            return Results.NoContent();
        }

        var current = await db.RefreshTokens.SingleOrDefaultAsync(
            token => token.TokenHash == RefreshTokenHasher.Hash(raw),
            cancellationToken
        );

        if (current is null) {
            RefreshCookie.Clear(http);
            return Results.NoContent();
        }

        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out var userId) || userId != current.UserId) {
            RefreshCookie.Clear(http);
            return Results.Problem(
                title: "Refresh token is invalid.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var now = time.GetUtcNow();
        await Refresh.RevokeFamilyAsync(db, current.FamilyId, now, cancellationToken);
        AuditLog.Write(db, now, userId, principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value ?? subject, "auth.logout", "User", userId);
        await db.SaveChangesAsync(cancellationToken);
        RefreshCookie.Clear(http);
        return Results.NoContent();
    }
}