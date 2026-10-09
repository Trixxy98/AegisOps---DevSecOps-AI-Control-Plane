using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public static class RemoveTeamMember {
    public static async Task<IResult> Handle(
        string slug,
        Guid userId,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http
    ) {
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out var actorId)) {
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var actor = await users.FindByIdAsync(subject);
        if (actor is null || !actor.IsActive) {
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var cancellationToken = http.RequestAborted;
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var team = await db.Teams.SingleOrDefaultAsync(
            item => item.Slug == normalizedSlug,
            cancellationToken
        );

        if (team is null) {
            return Results.Problem(
                title: "Team was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var isAdmin = await users.IsInRoleAsync(actor, "Admin");
        var isOwner = await db.TeamMembers.AnyAsync(
            member =>
                member.TeamId == team.Id
                && member.UserId == actorId
                && member.Role == TeamRole.Owner,
            cancellationToken
        );

        if (!isAdmin && !isOwner) {
            return Results.Problem(
                title: "Team was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var membership = await db.TeamMembers.SingleOrDefaultAsync(
            member => member.TeamId == team.Id && member.UserId == userId,
            cancellationToken
        );

        if (membership is null) {
            return Results.Problem(
                title: "Team member was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        if (membership.Role == TeamRole.Owner) {
            var ownersRemaining = await db.TeamMembers.CountAsync(
                member => 
                    member.TeamId == team.Id
                    && member.Role == TeamRole.Owner
                    && member.UserId != userId,
                cancellationToken
            );

            if (ownersRemaining == 0){
                return Results.Problem(
                    title: "A team must keep at least one Owner.",
                    statusCode: StatusCodes.Status400BadRequest
                );
            }
        }

        db.TeamMembers.Remove(membership);
        AuditLog.Write(db, TimeProvider.System.GetUtcNow(), actor.Id, actor.Email ?? actor.DisplayName, "team.member_removed", "Team", team.Id);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }
}