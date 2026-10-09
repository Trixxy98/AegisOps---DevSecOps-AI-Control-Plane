using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Identity;

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public static class ChangePassword {
    public static async Task<IResult> Handle(
        ChangePasswordRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time
    ) {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword)) {
            return Results.Problem(
                title: "Current password and new password are required.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out _)) {
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsActive) {
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var result = await users.ChangePasswordAsync(
            user,
            request.CurrentPassword,
            request.NewPassword
        );

        if (!result.Succeeded) {
            var errors = string.Join(", ", result.Errors.Select(error => error.Description));
            return Results.Problem(
                title: errors,
                statusCode: StatusCodes.Status400BadRequest
            );
        }

    user.MarkPasswordChanged();
    var update = await users.UpdateAsync(user);
    if (!update.Succeeded) {
        var errors = string.Join(", ", update.Errors.Select(error => error.Description));
        throw new InvalidOperationException(errors);
       }

    AuditLog.Write(db, time.GetUtcNow(), user.Id, user.Email ?? user.DisplayName, "auth.password_changed", "User", user.Id);
    await db.SaveChangesAsync();
    return Results.NoContent();
    }
}

