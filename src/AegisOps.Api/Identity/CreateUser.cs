using System.Security.Claims;
using AegisOps.Application.Audit;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Identity;

/// <summary>Optional team membership for a new user: the team by <c>teamId</c> or <c>slug</c>, and Owner or Member.</summary>
public sealed record CreateUserTeamRequest(Guid? TeamId, string? Slug, string? Role);

public sealed record CreateUserRequest(string? Email, string? DisplayName, IReadOnlyList<string>? Roles, CreateUserTeamRequest? Team);

public sealed record CreateUserTeamMembership(Guid TeamId, string Slug, string Role);

public sealed record CreateUserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    CreateUserTeamMembership? Team,
    string TemporaryPassword,
    bool MustChangePassword
);

/// <summary>
/// <c>POST /api/v1/admin/users</c> (docs/09 §3, <c>users:manage</c>): creates a user with a random temporary password
/// (same generator as <see cref="ResetUserPassword"/>) that is returned once with <c>Cache-Control: no-store</c> and
/// must be changed at first sign-in. User, roles, optional team membership and the <c>user.created</c> audit event
/// are written in one transaction; the password is never logged or audited.
/// </summary>
public static class CreateUser {
    public const string AuditAction = "user.created";

    public static async Task<IResult> Handle(
        CreateUserRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        IAuditWriter audit,
        TimeProvider time,
        HttpContext http
    ) {
        ArgumentNullException.ThrowIfNull(request);
        var email = request.Email?.Trim() ?? string.Empty;
        if (!UserAdministration.IsValidEmail(email)) {
            return UserAdministration.BadRequest("Email must be a valid email address.");
        }

        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (!UserAdministration.IsValidDisplayName(displayName)) {
            return UserAdministration.BadRequest(
                $"Display name is required and must be at most {UserAdministration.DisplayNameMaxLength} characters.");
        }

        var roles = UserAdministration.NormalizeRoles(request.Roles);
        if (roles is null) {
            return UserAdministration.BadRequest("Choose at least one role.",
                "Roles must be one or more of: " + string.Join(", ", IdentityRoles.All) + ".");
        }

        var actor = await UserAdministration.ResolveActorAsync(principal, users);
        if (actor is null) {
            return UserAdministration.Unauthorized();
        }

        var cancellationToken = http.RequestAborted;
        Team? team = null;
        var teamRole = TeamRole.Member;
        if (request.Team is not null) {
            if (!Enum.TryParse(request.Team.Role, ignoreCase: true, out teamRole) || !Enum.IsDefined(teamRole)) {
                return UserAdministration.BadRequest("Team role must be Owner or Member.");
            }

            var slug = request.Team.Slug?.Trim().ToLowerInvariant();
            if (request.Team.TeamId is { } teamId) {
                team = await db.Teams.SingleOrDefaultAsync(item => item.Id == teamId, cancellationToken);
            } else if (!string.IsNullOrEmpty(slug)) {
                team = await db.Teams.SingleOrDefaultAsync(item => item.Slug == slug, cancellationToken);
            } else {
                return UserAdministration.BadRequest("Team needs a teamId or a slug.");
            }

            if (team is null) {
                return UserAdministration.BadRequest("Team was not found.");
            }
        }

        if (await users.FindByEmailAsync(email) is not null) {
            return UserAdministration.Conflict("A user with this email already exists.");
        }

        var temporary = UserAdministration.GenerateTemporaryPassword();
        var now = time.GetUtcNow();
        var user = User.Create(email, displayName, now); // MustChangePassword and LockoutEnabled are true for new users.

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken)) {
            var created = await users.CreateAsync(user, temporary);
            if (!created.Succeeded) {
                // A concurrent request may have taken the email between the check and the insert.
                return created.Errors.Any(error => error.Code is "DuplicateEmail" or "DuplicateUserName")
                    ? UserAdministration.Conflict("A user with this email already exists.")
                    : UserAdministration.BadRequest("The user could not be created.",
                        string.Join(" ", created.Errors.Select(error => error.Description)));
            }

            UserAdministration.ThrowIfFailed(await users.AddToRolesAsync(user, roles));
            if (team is not null) {
                db.TeamMembers.Add(TeamMember.Create(team.Id, user.Id, teamRole, now));
            }

            audit.Write(now, actor.Id, actor.Email ?? actor.DisplayName, AuditAction, "User", user.Id);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        http.Response.Headers.CacheControl = "no-store";
        return Results.Created($"/api/v1/admin/users/{user.Id}", new CreateUserResponse(
            user.Id,
            user.Email ?? email,
            user.DisplayName,
            roles,
            team is null ? null : new CreateUserTeamMembership(team.Id, team.Slug, teamRole.ToString()),
            temporary,
            true));
    }
}
