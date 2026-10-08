using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public static class ListApiKeys {
    public static async Task<IResult> Handle(
        string slug,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
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
        var normalizedSlug = slug.ToLowerInvariant();
        var project = await db.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Slug == normalizedSlug, cancellationToken);

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
                && member.Role == AegisOps.Domain.Organization.TeamRole.Owner,
            cancellationToken
        );

        if (!isAdmin && !isOwner) {
            return Results.Problem(
                title: "Project not found",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var keys = await db.ApiKeys
            .AsNoTracking()
            .Where(item => item.ProjectId == project.Id)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new ApiKeyListItem(
                item.Id,
                item.Name,
                item.KeyPrefix,
                item.Scopes,
                item.ExpiresAt,
                item.LastUsedAt,
                item.RevokedAt,
                item.CreatedAt
            ))
            .ToListAsync(cancellationToken);

        return Results.Ok(keys);
    }
}