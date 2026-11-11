using AegisOps.Infrastructure.Identity;

namespace AegisOps.Api.Identity;

/// <summary>
/// docs/09: a user signed in with a temporary password (seeded or reset by an Admin) may only read who they are,
/// change the password, refresh or sign out. Everything else returns 403 Problem Details with
/// <c>reason = PasswordChangeRequired</c> until a new token (issued after the change) no longer carries the claim.
/// </summary>
public static class PasswordChangeGate {
    public const string Reason = "PasswordChangeRequired";

    private static readonly string[] AllowedPaths = [
        "/api/v1/auth/me",
        "/api/v1/auth/change-password",
        "/api/v1/auth/logout",
        "/api/v1/auth/refresh",
        "/api/v1/auth/login",
    ];

    public static bool IsAllowed(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase) ||
        AllowedPaths.Any(allowed => path.Equals(allowed, StringComparison.OrdinalIgnoreCase));

    public static IApplicationBuilder UsePasswordChangeGate(this IApplicationBuilder app) => app.Use(async (context, next) => {
        if (context.User.HasClaim(JwtAccessTokenFactory.PasswordChangeClaim, "true") && !IsAllowed(context.Request.Path)) {
            await Results.Problem(
                title: "Password change required.",
                detail: "Change the temporary password before using AegisOps.",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["reason"] = Reason }).ExecuteAsync(context);
            return;
        }

        await next(context);
    });
}
