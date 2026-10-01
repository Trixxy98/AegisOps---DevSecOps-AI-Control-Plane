using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public sealed record CreateTeamRequest(string Name, string Slug, string? Description);

public static class CreateTeam {
    public static async Task<IResult> Handle(
        CreateTeamRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        HttpContext http
    ) {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Slug)) {
            return Results.Problem(
                title: "Name and slug are required.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

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
        var slug = request.Slug.Trim().ToLowerInvariant();
        var exists = await db.Teams.AnyAsync(team => team.Slug == slug, cancellationToken);
        if (exists) {
            return Results.Problem(
                title: "A team with this slug already exists.",
                statusCode: StatusCodes.Status409Conflict
            );
        }

        try {
            var now = time.GetUtcNow();
            var team = Team.Create(request.Name, slug, now, request.Description);
            db.Teams.Add(team);
            db.TeamMembers.Add(TeamMember.Create(team.Id, userId, TeamRole.Owner, now));
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created(
                $"/api/v1/teams/{team.Slug}",
                new TeamListItem(team.Id, team.Name, team.Slug, team.Description, TeamRole.Owner.ToString())
            );
        } catch (ArgumentException exception) {
            return Results.Problem(
                title: exception.Message,
                statusCode: StatusCodes.Status400BadRequest
            );
        }
    }
}
