using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Identity;

/// <summary>
/// Shared rules of the Admin user-management routes (docs/09 §3 "User lifecycle"): the temporary-password generator,
/// input validation, the "last active Admin" guard rail and refresh-token revocation.
/// </summary>
public static class UserAdministration {
    public const int DisplayNameMaxLength = 100;
    public const int EmailMaxLength = 256;

    /// <summary>18 random bytes → 24 Base64Url characters: above the 12-character policy, no composition rules needed.</summary>
    public static string GenerateTemporaryPassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The signed-in, active user behind the request, or <c>null</c> (the caller answers 401).</summary>
    public static async Task<User?> ResolveActorAsync(ClaimsPrincipal principal, UserManager<User> users) {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(users);
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out _)) {
            return null;
        }

        var actor = await users.FindByIdAsync(subject);
        return actor is { IsActive: true } ? actor : null;
    }

    public static bool IsValidEmail(string email) =>
        email.Length is > 0 and <= EmailMaxLength && new EmailAddressAttribute().IsValid(email);

    public static bool IsValidDisplayName(string displayName) =>
        displayName.Length is > 0 and <= DisplayNameMaxLength;

    /// <summary>
    /// Maps the requested names to the canonical role names of <see cref="IdentityRoles.All"/> (case-insensitive,
    /// de-duplicated). Returns <c>null</c> when the list is empty or names an unknown role.
    /// </summary>
    public static IReadOnlyList<string>? NormalizeRoles(IEnumerable<string?>? requested) {
        if (requested is null) {
            return null;
        }

        var result = new List<string>();
        foreach (var name in requested) {
            var role = IdentityRoles.All.FirstOrDefault(known => string.Equals(known, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (role is null) {
                return null;
            }

            if (!result.Contains(role, StringComparer.Ordinal)) {
                result.Add(role);
            }
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>True when <paramref name="target"/> is an active Admin and no other active Admin exists.</summary>
    public static async Task<bool> IsLastActiveAdminAsync(UserManager<User> users, User target) {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsActive || !await users.IsInRoleAsync(target, IdentityRoles.Admin)) {
            return false;
        }

        var admins = await users.GetUsersInRoleAsync(IdentityRoles.Admin);
        return !admins.Any(admin => admin.Id != target.Id && admin.IsActive);
    }

    /// <summary>Revokes every active refresh token of the user so existing sessions cannot be renewed.</summary>
    public static async Task<int> RevokeRefreshTokensAsync(AegisOpsDbContext db, Guid userId, DateTimeOffset now,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(db);
        var sessions = await db.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions) {
            session.Revoke(now, null);
        }

        return sessions.Count;
    }

    public static async Task<UserListItem> DescribeAsync(UserManager<User> users, User user) {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(user);
        return new UserListItem(user.Id, user.Email ?? string.Empty, user.DisplayName, user.IsActive, await users.GetRolesAsync(user));
    }

    public static IResult Unauthorized() =>
        Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);

    public static IResult NotFound() =>
        Results.Problem(title: "User was not found.", statusCode: StatusCodes.Status404NotFound);

    public static IResult BadRequest(string title, string? detail = null) =>
        Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);

    public static IResult Conflict(string title, string? detail = null) =>
        Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status409Conflict);

    public static void ThrowIfFailed(IdentityResult result) {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded) {
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(error => error.Code)));
        }
    }
}
