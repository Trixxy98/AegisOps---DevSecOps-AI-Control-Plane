using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public sealed record ProjectListItem(
    Guid Id,
    Guid TeamId,
    string Name,
    string Slug,
    string? Description,
    bool IsArchived
);

public static class ListProjects {
    public static async Task<IResult> Handle(
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http,
        Guid? teamId = null,
        string? search = null,
        bool? archived = null
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
        var isAdmin = await users.IsInRoleAsync(user, "Admin");

        var query = db.Projects.AsNoTracking().AsQueryable();

        if (!isAdmin) {
            var teamIds = db.TeamMembers
                .AsNoTracking()
                .Where(member => member.UserId == userId)
                .Select(member => member.TeamId);

            query = query.Where(project => teamIds.Contains(project.TeamId));
        }

        if (teamId is not null) {
            query = query.Where(project => project.TeamId == teamId);
        }

        if (!string.IsNullOrWhiteSpace(search)) {
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(project =>
                EF.Functions.ILike(project.Name, pattern)
                || EF.Functions.ILike(project.Slug, pattern)
            );
        }

        if (archived is not null) {
            query = query.Where(project => project.IsArchived == archived);
        } else {
            query = query.Where(project => !project.IsArchived);
        }

        var projects = await query
            .OrderBy(project => project.Name)
            .Select(project => new ProjectListItem(
                project.Id,
                project.TeamId,
                project.Name,
                project.Slug,
                project.Description,
                project.IsArchived
            ))
            .ToListAsync(cancellationToken);

        return Results.Ok(projects);
    }

    private static string EscapeLike(string value) {
    return value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
    }
}