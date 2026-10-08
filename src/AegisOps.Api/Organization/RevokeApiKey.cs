using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public static class RevokeApiKey {
    public static async Task<IResult> Handle(
        string slug,
        Guid keyId,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        HttpContext http
    ) {
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out var userId)) {
            return Results.Problem(
                title: "Invalid credentials",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsActive) {
            return Results.Problem(
                title: "Invalid credentials",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var cancellationToken = http.RequestAborted;
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var project = await db.Projects.SingleOrDefaultAsync(
            item => item.Slug == normalizedSlug,
            cancellationToken
        );

        if (project is null) {
            return Results.Problem(
                title: "Project not found",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var isAdmin = await users.IsInRoleAsync(user, "Admin");
        var isOwner = await db.TeamMembers.AnyAsync(
            member =>
                member.TeamId == project.TeamId
                && member.UserId == userId
                && member.Role == TeamRole.Owner,
            cancellationToken
        );

        if (!isAdmin && !isOwner) {
            return Results.Problem(
                title: "Project not found",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var apiKey = await db.ApiKeys.SingleOrDefaultAsync(
            item => item.Id == keyId && item.ProjectId == project.Id,
            cancellationToken
        );

        if (apiKey is null) {
            return Results.Problem(
                title: "API key not found",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        try {
            apiKey.Revoke(time.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
        } catch (InvalidOperationException exception) {
            return Results.Problem(
                title: exception.Message,
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        return Results.NoContent();
    }
}