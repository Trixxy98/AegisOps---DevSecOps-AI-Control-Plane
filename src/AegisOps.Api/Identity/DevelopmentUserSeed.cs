using System.Security.Cryptography;
using AegisOps.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace AegisOps.Api.Identity;

public static partial class DevelopmentUserSeed {
    public const string Email = "admin@aegisops.local";

    public static async Task SeedAsync(IServiceProvider services) {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var logger = scope.ServiceProvider 
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(DevelopmentUserSeed));

        if (await users.FindByEmailAsync(Email) is not null) {
            return;
        }

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var user = User.Create(Email, "AegisOps Admin", time.GetUtcNow());
        var result = await users.CreateAsync(user, password);

        if (!result.Succeeded) {
            var errors = string.Join("; ", result.Errors.Select(error => error.Description));
            throw new InvalidOperationException(errors);
        }

        LogCreated(logger, Email, password);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Development user {Email} created. Password: {Password}")]
    private static partial void LogCreated(ILogger logger, string email, string password);
}
