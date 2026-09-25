using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AegisOps.Infrastructure.Identity;

public static class IdentityStoreExtensions {
    public static IServiceCollection AddIdentityStore(
        this IServiceCollection services,
        IConfiguration configuration
    ) {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");

        services.AddDbContext<AegisOpsDbContext>(options => 
            options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

        services
            .AddIdentityCore<User>(identity => {
                identity.User.RequireUniqueEmail = true;
                identity.Password.RequiredLength = 12;
                identity.Password.RequireDigit = false;
                identity.Password.RequireLowercase = false;
                identity.Password.RequireUppercase = false;
                identity.Password.RequireNonAlphanumeric = false;
                identity.Lockout.MaxFailedAccessAttempts = 5;
                identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AegisOpsDbContext>();

        return services;
    }
}