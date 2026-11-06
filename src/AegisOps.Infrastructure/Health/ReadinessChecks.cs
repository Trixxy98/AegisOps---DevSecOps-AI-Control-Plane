using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace AegisOps.Infrastructure.Health;

public static class ReadinessChecks {
    public static IHealthChecksBuilder AddDependencyReadiness(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck<PostgresReadinessCheck>("postgres", tags: ["ready"], timeout: TimeSpan.FromSeconds(5))
            .AddCheck<RedisReadinessCheck>("redis", tags: ["ready"], timeout: TimeSpan.FromSeconds(3));
}

public sealed class PostgresReadinessCheck(AegisOpsDbContext db) : IHealthCheck {
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) {
        try {
            if (!await db.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
            if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                return HealthCheckResult.Unhealthy("Database migrations are pending.");
            return HealthCheckResult.Healthy();
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            return HealthCheckResult.Unhealthy("PostgreSQL readiness check failed.", exception);
        }
    }
}

public sealed class RedisReadinessCheck(IServiceProvider services) : IHealthCheck {
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) {
        try {
            var redis = services.GetService<IConnectionMultiplexer>();
            if (redis is null) return HealthCheckResult.Unhealthy("Redis is not configured.");
            await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            return HealthCheckResult.Unhealthy("Redis is unavailable.", exception);
        }
    }
}
