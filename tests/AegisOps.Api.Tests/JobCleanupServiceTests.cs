using AegisOps.Domain.Jobs;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;

namespace AegisOps.Api.Tests;

public sealed class JobCleanupServiceTests {
    [Fact]
    public async Task Cleanup_deletes_only_succeeded_jobs_past_retention_and_keeps_dead_letters() {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using var provider = Services(connection);
        var now = DateTimeOffset.UtcNow;
        Guid expiredId, recentId, deadId, pendingId, runningId;
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
            var expired = Succeeded(now.AddDays(-8));
            var recent = Succeeded(now.AddDays(-6));
            var dead = Job.Enqueue(JobType.SendNotification, "{}", now.AddDays(-30), "dead");
            dead.MarkRunning(now.AddDays(-30), "worker");
            dead.Abandon("failed", now.AddDays(-30));
            var pending = Job.Enqueue(JobType.ParseScanReport, "{}", now.AddDays(-30), "pending");
            var running = Job.Enqueue(JobType.EvaluateDeployment, "{}", now.AddDays(-30), "running");
            running.MarkRunning(now.AddDays(-30), "worker");
            db.Jobs.AddRange(expired, recent, dead, pending, running);
            await db.SaveChangesAsync(ct);
            // Webhook attempt history belongs to its job and must not block the delete.
            db.WebhookDeliveryAttempts.Add(WebhookDeliveryAttempt.Start(expired.Id, 1, now.AddDays(-8)));
            await db.SaveChangesAsync(ct);
            (expiredId, recentId, deadId, pendingId, runningId) = (expired.Id, recent.Id, dead.Id, pending.Id, running.Id);
        }

        var deleted = await provider.GetRequiredService<JobCleanupService>().CleanupAsync(ct);

        Assert.Equal(1, deleted);
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            var remaining = await db.Jobs.AsNoTracking().Select(job => job.Id).ToListAsync(ct);
            Assert.DoesNotContain(expiredId, remaining);
            Assert.Equal(new[] { recentId, deadId, pendingId, runningId }.Order(), remaining.Order());
            Assert.Empty(await db.WebhookDeliveryAttempts.AsNoTracking().ToListAsync(ct));
        }
        Assert.Equal(0, await provider.GetRequiredService<JobCleanupService>().CleanupAsync(ct));
    }

    [Fact]
    public async Task Cleanup_drains_more_than_one_batch() {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using var provider = Services(connection);
        var old = DateTimeOffset.UtcNow.AddDays(-9);
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
            db.Jobs.AddRange(Enumerable.Range(0, JobCleanupService.BatchSize + 3).Select(_ => Succeeded(old)));
            await db.SaveChangesAsync(ct);
        }

        Assert.Equal(JobCleanupService.BatchSize + 3, await provider.GetRequiredService<JobCleanupService>().CleanupAsync(ct));
        await using (var scope = provider.CreateAsyncScope()) {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>().Jobs.CountAsync(ct));
        }
    }

    private static Job Succeeded(DateTimeOffset completedAt) {
        var job = Job.Enqueue(JobType.ExecuteDeployment, "{}", completedAt, "done");
        job.MarkRunning(completedAt, "worker");
        job.Succeed(completedAt);
        return job;
    }

    private static ServiceProvider Services(SqliteConnection connection) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection).UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCustomizer, SqliteCustomizer>());
        services.AddSingleton<JobCleanupService>();
        return services.BuildServiceProvider();
    }

    private sealed class SqliteCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<AegisOps.Domain.Identity.ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<AegisOps.Domain.Policy.Policy>().Ignore(policy => policy.AppliesToTiers);
            // SQLite cannot compare DateTimeOffset text; store as sortable binary like production timestamptz.
            modelBuilder.Entity<Job>().Property(job => job.CompletedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
        }
    }
}
