using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public sealed record TeamListItem(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    string? MyRole
);

public static class ListTeams {
    public static async Task<IResult> Handle(
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http
    ) {
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out var userId)) {
            return Results.Problem(
                title: "Invalid credentials",
                status: StatusCodes.Status401Unauthorized
            );
        }

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsActive) {
            return Results.Problem(
                title: "Invalid credentials",
                status: StatusCodes.Status401Unauthorized
            );
        }

        var isAdmin = await users.IsInRoleAsync(user, "Admin");
        va cancellationToken = http.RequestAborted;

        if (isAdmin) {
            var teams = await db.Teams
                .AsNoTracking()
                .OrderBy(team => team.Name)
                .Select(team => new TeamListItem(
                    team.Id,
                    team.Name,
                    team.Slug,
                    team.Description,
                    null
                ))
                .ToListAsync(cancellationToken);

            return Results.Ok(teams);
        }

        var visible = await (
            from team in db.Teams.AsNoTracking()
            join member in db.TeamMembers.AsNoTracking() on team.Id equals member.TeamId
            where member.UserId == userId
            orderby team.Name
            select new TeamListItem(
                team.Id,
                team.Name,
                team.Slug,
                team.Description,
                member.Role.ToString()
            )
        ).ToListAsync(cancellationToken); 
        
        return Results.Ok(visible);
    }
}