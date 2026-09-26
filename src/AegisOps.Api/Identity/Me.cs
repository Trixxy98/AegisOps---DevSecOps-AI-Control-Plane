using System.Security.Claims;
using AegisOps.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Identity;

public sealed record MeResponse(
    Guid Id,
    string Email,
    string DisplayName,
    bool MustChangePassword,
    IList<string> Roles
);

public static class CurrentUser {
    public static async Task<IResult> Handle(ClaimsPrincipal principal, UserManager<User> users) {
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out _)) {
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsActive || string.IsNullOrWhiteSpace(user.Email)) {
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var roles = await users.GetRolesAsync(user);
        return Results.Ok(new MeResponse(
            user.Id,
            user.Email,
            user.DisplayName,
            user.MustChangePassword,
            roles
        ));
    }
}