using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public static class GetProject {
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

        var cancellationToken = http.RequestAborted;
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var project = await db.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Slug == normalizedSlug, cancellationToken);

        if (project is null) {
            return Results.Problem(
                title: "Project was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var isAdmin = await users.IsInRoleAsync(user, "Admin");
        if (!isAdmin) {
            var isMember = await db.TeamMembers.AnyAsync(
                member => member.TeamId == project.TeamId && member.UserId == userId,
                cancellationToken
            );
            if (!isMember) {
                return Results.Problem(
                    title: "Project was not found.",
                    statusCode: StatusCodes.Status404NotFound
                );
            }
        }

        var environments = await db.Environments
            .AsNoTracking()
            .Where(environment => environment.ProjectId == project.Id)
            .OrderBy(environment => environment.Order)
            .Select(environment => new EnvironmentListItem(
                environment.Id,
                environment.Name,
                environment.Tier.ToString(),
                environment.Order
            ))
            .ToListAsync(cancellationToken);

        var repository = await db.Repositories
            .AsNoTracking()
            .Where(item => item.ProjectId == project.Id)
            .Select(item => new RepositoryResponse(
                item.Id,
                item.ProjectId,
                item.Provider.ToString(),
                item.FullName,
                item.DefaultBranch,
                item.HtmlUrl
            ))
            .SingleOrDefaultAsync(cancellationToken);

        return Results.Ok(new ProjectResponse(
            project.Id,
            project.TeamId,
            project.Name,
            project.Slug,
            project.Description,
            project.IsArchived,
            environments,
            repository
        ));
    }
}