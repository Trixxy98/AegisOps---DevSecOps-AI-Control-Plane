using System.Security.Claims;
using AegisOps.Application.Audit;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace AegisOps.Api.Identity;

public sealed record UpdateUserRequest(string? DisplayName, bool? IsActive);

/// <summary>
/// <c>PATCH /api/v1/admin/users/{id}</c> (docs/09 §3, <c>users:manage</c>): renames, deactivates or reactivates a user.
/// Deactivation uses <see cref="User.Deactived"/> (locks the account) and revokes every active refresh token; an
/// Admin cannot deactivate themself and the last active Admin cannot be deactivated (409). Audited as
/// <c>user.updated</c>, <c>user.deactivated</c> and <c>user.activated</c>; a request that changes nothing writes nothing.
/// </summary>
public static class UpdateUser {
    public const string UpdatedAction = "user.updated";
    public const string DeactivatedAction = "user.deactivated";
    public const string ActivatedAction = "user.activated";

    public static async Task<IResult> Handle(
        Guid id,
        UpdateUserRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        IAuditWriter audit,
        TimeProvider time,
        HttpContext http
    ) {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DisplayName is null && request.IsActive is null) {
            return UserAdministration.BadRequest("Nothing to update.", "Send displayName, isActive or both.");
        }

        var displayName = request.DisplayName?.Trim();
        if (displayName is not null && !UserAdministration.IsValidDisplayName(displayName)) {
            return UserAdministration.BadRequest(
                $"Display name is required and must be at most {UserAdministration.DisplayNameMaxLength} characters.");
        }

        var actor = await UserAdministration.ResolveActorAsync(principal, users);
        if (actor is null) {
            return UserAdministration.Unauthorized();
        }

        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) {
            return UserAdministration.NotFound();
        }

        var rename = displayName is not null && !string.Equals(displayName, user.DisplayName, StringComparison.Ordinal);
        var deactivate = request.IsActive == false && user.IsActive;
        var activate = request.IsActive == true && !user.IsActive;

        if (deactivate) {
            if (user.Id == actor.Id) {
                return UserAdministration.Conflict("You cannot deactivate your own account.",
                    "Ask another Admin to deactivate it.");
            }

            if (await UserAdministration.IsLastActiveAdminAsync(users, user)) {
                return UserAdministration.Conflict("The last active Admin cannot be deactivated.",
                    "Give another active user the Admin role first.");
            }
        }

        if (!rename && !deactivate && !activate) {
            return Results.Ok(await UserAdministration.DescribeAsync(users, user));
        }

        var now = time.GetUtcNow();
        var cancellationToken = http.RequestAborted;
        var actorDisplay = actor.Email ?? actor.DisplayName;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken)) {
            if (rename) {
                user.Rename(displayName!);
                audit.Write(now, actor.Id, actorDisplay, UpdatedAction, "User", user.Id);
            }

            if (deactivate) {
                user.Deactived();
                await UserAdministration.RevokeRefreshTokensAsync(db, user.Id, now, cancellationToken);
                audit.Write(now, actor.Id, actorDisplay, DeactivatedAction, "User", user.Id);
            } else if (activate) {
                user.Active();
                audit.Write(now, actor.Id, actorDisplay, ActivatedAction, "User", user.Id);
            }

            // UpdateAsync saves the shared context: the user, the revoked tokens and the audit rows in one SaveChanges.
            UserAdministration.ThrowIfFailed(await users.UpdateAsync(user));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Ok(await UserAdministration.DescribeAsync(users, user));
    }
}
