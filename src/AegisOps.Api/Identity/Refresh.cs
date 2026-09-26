using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AegisOps.Api.Identity;

public static class Refresh {
    public static async Task<IResult> Handle(
        HttpContext http,
        AegisOpsDbContext db,
        UserManager<User> users,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider time
    ) {
        if (!http.Request.Cookies.TryGetValue(RefreshCookie.Name, out var raw) || string.IsNullOrWhiteSpace(raw)) {
            return Unauthorized();
        }

        var now = time.GetUtcNow();
        var cancellationToken = http.RequestAborted;
        var current = await db.RefreshTokens.SingleOrDefaultAsync(
            token => token.TokenHash == RefreshTokenHasher.Hash(raw),
            cancellationToken
        );

        if (current is null) {
            RefreshCookie.Clear(http);
            return Unauthorized();
        }

        if (current.RevokedAt is not null) {
            await RevokeFamilyAsync(db, current.FamilyId, now, cancellationToken);
            RefreshCookie.Clear(http);
            return Unauthorized();
        }

        if (!current.IsActive(now)) {
            RefreshCookie.Clear(http);
            return Unauthorized();
        }

        var user = await users.FindByIdAsync(current.UserId.ToString());
        if (user is null || !user.IsActive) {
            current.Revoke(now, null);
            await db.SaveChangesAsync(cancellationToken);
            RefreshCookie.Clear(http);
            return Unauthorized();  
        }

        var options = jwtOptions.Value;
        if (options.RefreshTokenDays < 1) {
            throw new InvalidOperationException("Jwt:RefreshTokenDays must be at least 1.");
        }

        var nextRaw = RefreshTokenGenerator.Create();
        var next = RefreshToken.Create(
            user.Id,
            current.FamilyId,
            RefreshTokenHasher.Hash(nextRaw),
            now.AddDays(options.RefreshTokenDays),
            now,
            http.Connection.RemoteIpAddress?.ToString()
        );

        current.Revoke(now, next.Id);
        db.RefreshTokens.Add(next);
        await db.SaveChangesAsync(cancellationToken);
        RefreshCookie.Append(http, nextRaw, next.ExpiresAt);

        var accessToken = JwtAccessTokenFactory.Create(user, [], options, now);
        return Results.Ok(new LoginResponse(accessToken, now.AddMinutes(options.AccessTokenMinutes)));
    }

    private static IResult Unauthorized() {
        return Results.Problem(
            title: "Refresh token is invalid.",
            statusCode: StatusCodes.Status401Unauthorized
        );
    }

    private static async Task RevokeFamilyAsync(
        AegisOpsDbContext db,
        Guid familyId,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) {
        var active = await db.RefreshTokens
            .Where(token => token.FamilyId == familyId && token.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var token in active) {
            token.Revoke(now, null);
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
