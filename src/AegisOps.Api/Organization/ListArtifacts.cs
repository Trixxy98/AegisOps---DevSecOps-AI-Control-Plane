using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Organization;

public static class ListArtifacts {
    public static async Task<IResult> Handle(
        string slug,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http,
        string? branch = null,
        string? sort = null
    ) {
        var subject = principal.FindFirst("sub")?.Value;
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

        var descending = sort is null || sort == "-createdAt";
        if (!descending && sort != "createdAt") {
            return Results.Problem(
                title: "Sort must be createdAt or -createdAt.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var query = db.Artifacts
            .AsNoTracking()
            .Where(item => item.ProjectId == project.Id);

        if (!string.IsNullOrWhiteSpace(branch)) {
            var normalizedBranch = branch.Trim();
            query = query.Where(item => item.Branch == normalizedBranch);
        }

        query = descending
            ? query.OrderByDescending(item => item.CreatedAt)
            : query.OrderBy(item => item.CreatedAt);

        var artifacts = await query
            .Select(item => new ArtifactResponse(
                item.Id,
                item.ProjectId,
                item.Version,
                item.CommitSha,
                item.Branch,
                item.ImageReference,
                item.ImageDigest,
                item.BuildStatus.ToString(),
                item.TestStatus.ToString(),
                item.CreatedAt
            ))
            .ToListAsync(cancellationToken);

        return Results.Ok(artifacts);
    }
}