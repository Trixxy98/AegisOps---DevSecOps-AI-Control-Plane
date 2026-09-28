using System.Security.Cryptography;
using AegisOps.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace AegisOps.Api.Identity;

public static partial class DevelopmentUserSeed {
    public const string Email = "admin@aegisops.local";
    public const string AdminRole = "Admin";
    public const string DeveloperRole = "Developer";
    public const string SecurityRole = "Security";
    public const string ApproverRole = "Approver";

    public static async Task SeedAsync(IServiceProvider services) {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var logger = scope.ServiceProvider 
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(DevelopmentUserSeed));
        
        foreach (var roleName in new[] {AdminRole, DeveloperRole, SecurityRole, ApproverRole}) {
            await EnsureRoleAsync(roles, roleName);
        }

        var now = time.GetUtcNow();
        foreach (var account in Accounts) {
            var user = await users.FindByEmailAsync(account.Email);
            if (user is null) {
                    var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
                    user = User.Create(account.Email, account.DisplayName, now);
                    ThrowIfFailed(await users.CreateAsync(user, password));
                    LogCreated(logger, account.Email, password);
            }

            foreach (var roleName in account.Roles) {
                if (!await users.IsInRoleAsync(user, roleName)) {
                    ThrowIfFailed(await users.AddToRoleAsync(user, roleName));
                }
            }
        }
    }

    private sealed record DemoAccount(string Email, string DisplayName, string[] Roles);
    private static readonly DemoAccount[] Accounts = [
        new(Email, "AegisOps Admin", [AdminRole]),
        new("developer@aegisops.local", "AegisOps Developer", [DeveloperRole]),
        new("security@aegisops.local", "AegisOps Security", [SecurityRole, ApproverRole]),
        new("approver@aegisops.local", "AegisOps Approver", [ApproverRole]),
    ];

    private static async Task EnsureRoleAsync(RoleManager<IdentityRole<Guid>> roles, string name) {
        if (await roles.RoleExistsAsync(name)) {
            return;
        }

        ThrowIfFailed(await roles.CreateAsync(new IdentityRole<Guid> {
            Id = Guid.CreateVersion7(),
            Name = name,
        }));
    }

    private static void ThrowIfFailed(IdentityResult result) {
        if (result.Succeeded) {
            return;
        }

        var errors = string.Join("; ", result.Errors.Select(error => error.Description));
        throw new InvalidOperationException(errors);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Development user {Email} created. Password: {Password}")]
    private static partial void LogCreated(ILogger logger, string email, string password);
}
