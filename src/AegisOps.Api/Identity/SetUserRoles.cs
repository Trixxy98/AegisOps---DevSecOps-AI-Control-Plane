using System.Security.Claims;
using AegisOps.Application.Audit;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace AegisOps.Api.Identity;

public sealed record SetUserRolesRequest(IReadOnlyList<string>? Roles);

/// <summary>
/// <c>PUT /api/v1/admin/users/{id}/roles</c> (docs/09 §3, <c>roles:manage</c>): replaces the user's global roles with a
/// non-empty subset of <see cref="IdentityRoles.All"/>. An Admin cannot remove their own Admin role and the last active
/// Admin cannot lose it (409). Because access tokens carry the roles, a change revokes the user's refresh tokens, so
/// the new roles apply at the next sign-in (at most one access-token lifetime later). Audited as
/// <c>user.roles_changed</c>; an unchanged set writes nothing.
/// </summary>
public static class SetUserRoles {
    public const string AuditAction = "user.roles_changed";

    public static async Task<IResult> Handle(
        Guid id,
        SetUserRolesRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        IAuditWriter audit,
        TimeProvider time,
        HttpContext http
    ) {
        ArgumentNullException.ThrowIfNull(request);
        var roles = UserAdministration.NormalizeRoles(request.Roles);
        if (roles is null) {
            return UserAdministration.BadRequest("Choose at least one role.",
                "Roles must be one or more of: " + string.Join(", ", IdentityRoles.All) + ".");
        }

        var actor = await UserAdministration.ResolveActorAsync(principal, users);
        if (actor is null) {
            return UserAdministration.Unauthorized();
        }

        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) {
            return UserAdministration.NotFound();
        }

        var current = await users.GetRolesAsync(user);
        var toRemove = current.Where(role => !roles.Contains(role, StringComparer.Ordinal)).ToList();
        var toAdd = roles.Where(role => !current.Contains(role, StringComparer.Ordinal)).ToList();
        if (toRemove.Count == 0 && toAdd.Count == 0) {
            return Results.Ok(await UserAdministration.DescribeAsync(users, user));
        }

        if (toRemove.Contains(IdentityRoles.Admin, StringComparer.Ordinal)) {
            if (user.Id == actor.Id) {
                return UserAdministration.Conflict("You cannot remove your own Admin role.",
                    "Ask another Admin to change your roles.");
            }

            if (await UserAdministration.IsLastActiveAdminAsync(users, user)) {
                return UserAdministration.Conflict("The last active Admin cannot lose the Admin role.",
                    "Give another active user the Admin role first.");
            }
        }

        var now = time.GetUtcNow();
        var cancellationToken = http.RequestAborted;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken)) {
            if (toRemove.Count > 0) {
                UserAdministration.ThrowIfFailed(await users.RemoveFromRolesAsync(user, toRemove));
            }

            if (toAdd.Count > 0) {
                UserAdministration.ThrowIfFailed(await users.AddToRolesAsync(user, toAdd));
            }

            await UserAdministration.RevokeRefreshTokensAsync(db, user.Id, now, cancellationToken);
            audit.Write(now, actor.Id, actor.Email ?? actor.DisplayName, AuditAction, "User", user.Id);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Ok(await UserAdministration.DescribeAsync(users, user));
    }
}
