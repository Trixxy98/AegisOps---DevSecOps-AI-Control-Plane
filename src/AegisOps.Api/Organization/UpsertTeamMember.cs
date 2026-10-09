using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;


public sealed record UpsertTeamMemberRequest(string Role);

public static class UpsertTeamMember {
    public static async Task<IResult> Handle(
        string slug,
        Guid userId,
        UpsertTeamMemberRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        HttpContext http
    ) {
        if (!Enum.TryParse<TeamRole>(request.Role, ignoreCase: true, out var role) || !Enum.IsDefined(role)) {
            return Results.Problem(
                title: "Role must be Owner or Member.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

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
        var team = await db.Teams.SingleOrDefaultAsync(item => item.Slug == normalizedSlug, cancellationToken);

        if (team is null) {
            return Results.Problem(
                title: "Team not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var isAdmin = await users.IsInRoleAsync(actor, "Admin");
        var isOwner = await db.TeamMembers.AnyAsync(member => member.TeamId == team.Id && member.UserId == actorId && member.Role == TeamRole.Owner, cancellationToken);

        if (!isAdmin && !isOwner) {
            return Results.Problem(
                title: "Team was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var target = await users.FindByIdAsync(userId.ToString());
        if (target is null || !target.IsActive) {
            return Results.Problem(
                title: "User not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var membership = await db.TeamMembers.SingleOrDefaultAsync(
            member => member.TeamId == team.Id && member.UserId == userId,
            cancellationToken
        );

        if (membership is null) {
            db.TeamMembers.Add(TeamMember.Create(team.Id, userId, role, time.GetUtcNow()));
        } else {
            membership.ChangeRole(role);
        }

        if (role != TeamRole.Owner) {
            var ownersRemaining = await db.TeamMembers.CountAsync(
                member => member.TeamId == team.Id
                && member.Role == TeamRole.Owner
                && member.UserId != userId,
                cancellationToken
            );

            if (ownersRemaining == 0) {
                return Results.Problem(
                    title: "A team must keep at least one Owner.",
                    statusCode: StatusCodes.Status400BadRequest
                );
            }
        }

        AuditLog.Write(db, time.GetUtcNow(), actor.Id, actor.Email ?? actor.DisplayName, "team.member_upserted", "Team", team.Id);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new TeamMemberItem(
            target.Id,
            target.Email ?? string.Empty,
            target.DisplayName,
            role.ToString()
        ));
    }
}