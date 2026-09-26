using AegisOps.Domain.Identity;
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
        HttpContext http
    ) {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password)) {
            return Results.Problem(
                title: "Email and password are required.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var user = await users.FindByEmailAsync(request.Email);
        var passwordValid = user is not null && await users.CheckPasswordAsync(user, request.Password);

        if (user is null || !user.IsActive || !passwordValid) {
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var options = jwtOptions.Value;
        var now = time.GetUtcNow();
        var token = JwtAccessTokenFactory.Create(user, [], options, now);

        await RefreshCookie.IssueAsync(http, db, user, options, now, http.RequestAborted);

        return Results.Ok(new LoginResponse(token, now.AddMinutes(options.AccessTokenMinutes)));
    }
}
