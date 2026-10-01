using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public sealed record TeamMemberItem(
    Guid UserId,
    string Email,
    string DisplayName,
    string Role
);

public sealed record TeamDetailResponse(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    string? MyRole,
    IReadOnlyList<TeamMemberItem> Members
);

public static class GetTeam {
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
        var team = await db.Teams
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Slug == normalizedSlug, cancellationToken);

        if (team is null) {
            return Results.Problem(
                title: "Team was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var isAdmin = await users.IsInRoleAsync(user, "Admin");
        var membership = await db.TeamMembers
            .AsNoTracking()
            .SingleOrDefaultAsync(member => member.TeamId == team.Id && member.UserId == userId,
            cancellationToken
            );

        if (!isAdmin && membership is null) {
            return Results.Problem(
                title: "Team was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var members = await (
            from member in db.TeamMembers.AsNoTracking()
            join account in db.Users.AsNoTracking() on member.UserId equals account.Id
            where member.TeamId == team.Id
            orderby member.Role, account.Email
            select new TeamMemberItem(
                account.Id,
                account.Email,
                account.DisplayName,
                member.Role.ToString()
            )
        ).ToListAsync(cancellationToken);

        return Results.Ok(new TeamDetailResponse(
            team.Id,
            team.Name,
            team.Slug,
            team.Description,
            membership?.Role.ToString(),
            members
        ));
    }
}