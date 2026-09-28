using System.Security.Cryptography;
using AegisOps.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace AegisOps.Api.Identity;

public static partial class DevelopmentUserSeed {
    public const string Email = "admin@aegisops.local";
    public const string AdminRole = "Admin";

    public static async Task SeedAsync(IServiceProvider services) {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var logger = scope.ServiceProvider 
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(DevelopmentUserSeed));

        await EnsureRoleAsync(roles, AdminRole);

        var user = await users.FindByEmailAsync(Email);
        if (user is null) {
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            user = User.Create(Email, "AegisOps Admin", time.GetUtcNow());
            ThrowIfFailed(await users.CreateAsync(user, password));
            LogCreated(logger, Email, password);
        }


        if (!await users.IsInRoleAsync(user, AdminRole)) {
            ThrowIfFailed(await users.AddToRoleAsync(user, AdminRole));
        }
    }

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
