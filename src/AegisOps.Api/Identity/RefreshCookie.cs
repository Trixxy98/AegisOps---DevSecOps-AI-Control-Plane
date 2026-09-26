using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Persistence;

namespace AegisOps.Api.Identity;

public static class RefreshCookie {
    public const string Name = "aegisops.refresh";
    public const string Path = "/api/v1/auth";

    public static async Task IssueAsync(
        HttpContext http,
        AegisOpsDbContext db,
        User user,
        JwtOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) {
        if (options.RefreshTokenDays < 1) {
            throw new InvalidOperationException("Jwt:RefreshTokenDays must be at least 1.");
        }

        var raw = RefreshTokenGenerator.Create();
        var token = RefreshToken.Create(
            user.Id,
            Guid.CreateVersion7(),
            RefreshTokenHasher.Hash(raw),
            now.AddDays(options.RefreshTokenDays),
            now,
            http.Connection.RemoteIpAddress?.ToString()
        );

        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);

        Append(http, raw, token.ExpiresAt);
    }

    public static void Append(HttpContext http, string raw, DateTimeOffset expiresAt) {
        http.Response.Cookies.Append(Name, raw, new CookieOptions {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = Path,
            Expires = expiresAt,
            IsEssential = true,
        });
    }

    public static void Clear(HttpContext http) {
        http.Response.Cookies.Delete(Name, new CookieOptions {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = Path,
        });
    }
}