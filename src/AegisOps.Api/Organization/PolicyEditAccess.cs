using AegisOps.Domain.Identity;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Organization;

/// <summary>
/// docs/09 section 5 EditPolicyHandler, applied after the policies:write route permission:
/// Global -> Admin; Team -> Security/Admin; Project -> Admin, or Security who is a member of the project's team.
/// </summary>
internal static class PolicyEditAccess {
    public static async Task<IResult?> CheckAsync(
        User user,
        PolicyScope scope,
        Guid? scopeId,
        UserManager<User> users,
        AegisOpsDbContext db,
        CancellationToken cancellationToken
    ) {
        var roles = await users.GetRolesAsync(user);
        if (roles.Contains("Admin", StringComparer.Ordinal)) {
            return null;
        }

        var isSecurity = roles.Contains("Security", StringComparer.Ordinal);
        var allowed = scope switch {
            PolicyScope.Team => isSecurity,
            PolicyScope.Project => isSecurity && scopeId is Guid projectId && await HasProjectTeamAccess(user.Id, projectId, db, cancellationToken),
            _ => false,
        };

        return allowed
            ? null
            : Results.Problem(
                title: "You are not allowed to edit policies in this scope.",
                statusCode: StatusCodes.Status403Forbidden
            );
    }

    private static async Task<bool> HasProjectTeamAccess(Guid userId, Guid projectId, AegisOpsDbContext db, CancellationToken cancellationToken) {
        var teamId = await db.Projects.AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => (Guid?)project.TeamId)
            .SingleOrDefaultAsync(cancellationToken);
        return teamId is not null && await db.TeamMembers.AsNoTracking()
            .AnyAsync(member => member.TeamId == teamId && member.UserId == userId, cancellationToken);
    }
}
