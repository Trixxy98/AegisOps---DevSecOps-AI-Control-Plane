using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using AegisOps.Application.Audit;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Identity;

/// <summary>
/// <c>bootstrap-admin</c> (roadmap v1.1 item 2): creates the first Admin of an installation that has no demo seed.
/// Refuses when any user already holds the Admin role, so it cannot be used to add a second, unaudited Admin later.
/// The temporary password is random (same generator as <see cref="ResetUserPassword"/>), printed once to the
/// operator's terminal, must be changed at first sign-in, and is never logged or audited.
/// </summary>
public static class BootstrapAdmin {
    public const string AuditAction = "user.bootstrapped";
    public const string ActorDisplay = "bootstrap-admin";

    /// <summary>Exit code when the command refuses (an Admin exists, invalid or duplicate email).</summary>
    public const int Refused = 1;

    public static async Task<int> RunAsync(
        IServiceProvider services,
        string email,
        string? displayName,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        email = email?.Trim() ?? string.Empty;
        if (email.Length == 0 || !new EmailAddressAttribute().IsValid(email)) {
            await error.WriteLineAsync("bootstrap-admin: --email must be a valid email address.");
            return Refused;
        }

        displayName = string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName.Trim();

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var users = provider.GetRequiredService<UserManager<User>>();
        var roles = provider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var db = provider.GetRequiredService<AegisOpsDbContext>();
        var audit = provider.GetRequiredService<IAuditWriter>();
        var time = provider.GetRequiredService<TimeProvider>();

        await IdentityRoles.EnsureAsync(roles);
        if ((await users.GetUsersInRoleAsync(IdentityRoles.Admin)).Count > 0) {
            await error.WriteLineAsync(
                "bootstrap-admin: refused. An Admin already exists; sign in as that Admin and manage users from AegisOps instead.");
            return Refused;
        }

        if (await users.FindByEmailAsync(email) is not null) {
            await error.WriteLineAsync($"bootstrap-admin: refused. A user with the email {email} already exists.");
            return Refused;
        }

        // 18 random bytes → 24 Base64Url characters: above the 12-character policy, no composition rules needed.
        var temporary = Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
        var now = time.GetUtcNow();
        var user = User.Create(email, displayName, now); // MustChangePassword is true for new users.

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken)) {
            var created = await users.CreateAsync(user, temporary);
            if (!created.Succeeded) {
                await error.WriteLineAsync("bootstrap-admin: could not create the user: " +
                    string.Join("; ", created.Errors.Select(item => item.Description)));
                return Refused;
            }

            ThrowIfFailed(await users.AddToRoleAsync(user, IdentityRoles.Admin));
            audit.Write(AuditEvent.Record(now, "System", null, ActorDisplay, AuditAction, "User", user.Id, "Succeeded", null));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await output.WriteLineAsync($"Created Admin {email} ({displayName}).");
        await output.WriteLineAsync($"Temporary password (shown only once): {temporary}");
        await output.WriteLineAsync("Sign in with it now; AegisOps makes you change it at first sign-in.");
        return 0;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void ThrowIfFailed(IdentityResult result) {
        if (!result.Succeeded) {
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(error => error.Code)));
        }
    }
}
