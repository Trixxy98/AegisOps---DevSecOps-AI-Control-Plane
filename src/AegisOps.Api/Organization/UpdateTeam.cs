using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public sealed record UpdateTeamRequest(string? Name, string? Description);
public static class UpdateTeam {
    public static async Task<IResult> Handle(
        string slug, 
        UpdateTeamRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http
    ) {
        if (request.Name is null && request.Description is null) {
            return Results.Problem(
                title: "Name or description is required.",
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
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var team = await db.Teams.SingleOrDefaultAsync(item => item.Slug == normalizedSlug, cancellationToken);

        if (team is null) {
            return Results.Problem(
                title: "Team was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var isAdmin = await users.IsInRoleAsync(user, "Admin");
        var isOwner = await db.TeamMembers.AnyAsync(
            member => 
                member.TeamId == team.Id
                && member.UserId == userId
                && member.Role == TeamRole.Owner,
            cancellationToken
        );

        if (!isAdmin && !isOwner) {
            return Results.Problem(
                title: "Team was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        try {
            if (request.Name is not null) {
                team.Rename(request.Name);
            }

            if (request.Description is not null) {
                team.Describe(request.Description);
            }

            await db.SaveChangesAsync(cancellationToken);
        } catch (ArgumentException exception) {
            return Results.Problem(
                title: exception.Message,
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var membership = await db.TeamMembers.AsNoTracking().SingleOrDefaultAsync(
            member => member.TeamId == team.Id && member.UserId == userId,
            cancellationToken
        );

        return Results.Ok(new TeamListItem(
            team.Id,
            team.Name,
            team.Slug,
            team.Description,
            membership?.Role.ToString()
        ));
    }
}