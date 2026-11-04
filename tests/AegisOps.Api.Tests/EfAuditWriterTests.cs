using AegisOps.Domain.Audit;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace AegisOps.Api.Tests;

public sealed class EfAuditWriterTests {
    [Fact]
    public async Task Write_tracks_event_in_same_unit_of_work_without_saving() {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AegisOpsDbContext>()
            .UseSqlite(connection).UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCustomizer, SqliteCustomizer>().Options;
        await using var db = new AegisOpsDbContext(options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var audit = AuditEvent.Record(DateTimeOffset.UtcNow, "System", null, "policy-engine",
            "deployment.evaluated", "Deployment", Guid.NewGuid(), "Denied", "correlation-1");

        new EfAuditWriter(db).Write(audit);

        Assert.Same(audit, Assert.Single(db.ChangeTracker.Entries<AuditEvent>()).Entity);
        Assert.Equal(EntityState.Added, db.Entry(audit).State);
        Assert.Empty(await db.AuditEvents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var persisted = Assert.Single(await db.AuditEvents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(audit.Id, persisted.Id);
        Assert.Equal("System", persisted.ActorType);
        Assert.Equal("Denied", persisted.Outcome);
        Assert.Equal("correlation-1", persisted.CorrelationId);
    }

    private sealed class SqliteCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<AegisOps.Domain.Identity.ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<AegisOps.Domain.Policy.Policy>().Ignore(policy => policy.AppliesToTiers);
        }
    }
}
