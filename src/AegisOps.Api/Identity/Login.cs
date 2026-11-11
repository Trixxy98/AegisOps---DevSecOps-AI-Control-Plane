using AegisOps.Domain.Identity;
using AegisOps.Application.Audit;
using AegisOps.Domain.Audit;
using AegisOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using AegisOps.Infrastructure.Persistence;

namespace AegisOps.Api.Identity;

public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);

public static class Login {
    public static async Task<IResult> Handle(
        LoginRequest request,
        UserManager<User> users,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider time,
        AegisOpsDbContext db,
        IAuditWriter audit,
        HttpContext http
    ) {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password)) {
            return Results.Problem(
                title: "Email and password are required.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var user = await users.FindByEmailAsync(request.Email);
        // docs/09: 5 failures lock the account for 15 minutes. CheckPasswordAsync alone neither checks nor counts
        // lockout, so do both here. A locked account gets the same generic 401 (no account-state oracle), and its
        // password is not even checked while locked.
        var lockedOut = user is not null && await users.IsLockedOutAsync(user);
        var passwordValid = user is not null && !lockedOut && await users.CheckPasswordAsync(user, request.Password);
        if (user is not null && user.IsActive && !lockedOut && !passwordValid) {
            ThrowIfFailed(await users.AccessFailedAsync(user));
        }

        if (user is null || !user.IsActive || lockedOut || !passwordValid) {
            // docs/17 §6: failed logins are audited. The submitted email and password are never recorded;
            // the target user id is kept only when the account exists, to make guessing visible.
            audit.Write(AuditEvent.Record(time.GetUtcNow(), "Anonymous", null, "anonymous",
                "auth.login_failed", "User", user?.Id, "Denied", null));
            await db.SaveChangesAsync(http.RequestAborted);
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        if (await users.GetAccessFailedCountAsync(user) > 0) {
            ThrowIfFailed(await users.ResetAccessFailedCountAsync(user));
        }

        var options = jwtOptions.Value;
        var now = time.GetUtcNow();
        var roles = await users.GetRolesAsync(user);
        var token = JwtAccessTokenFactory.Create(user, roles, options, now);

        await RefreshCookie.IssueAsync(http, db, user, options, now, http.RequestAborted);
        audit.Write(now, user.Id, user.Email ?? user.DisplayName, "auth.login", "User", user.Id);
        await db.SaveChangesAsync(http.RequestAborted);

        return Results.Ok(new LoginResponse(token, now.AddMinutes(options.AccessTokenMinutes)));
    }

    private static void ThrowIfFailed(IdentityResult result) {
        if (!result.Succeeded) {
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(error => error.Code)));
        }
    }
}
