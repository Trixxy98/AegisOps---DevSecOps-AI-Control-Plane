using AegisOps.Domain.Ai;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Ai;
using AegisOps.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AegisOps.Api.Tests;

public sealed class AiContentRetentionServiceTests {
    [Fact]
    public async Task Cleanup_clears_only_expired_stored_content_and_keeps_governance_metadata() {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=False");
        await connection.OpenAsync(ct);
        await using var provider = Services(connection, retentionDays: null);
        var storing = Policy(storeContent: true);
        var old = Request(storing, "old private prompt", now.AddDays(-31));
        var recent = Request(storing, "recent private prompt", now.AddDays(-10));
        var oldPreviewOnly = Request(Policy(storeContent: false), "preview only", now.AddDays(-90));
        Assert.NotNull(old.PromptContent);
        Assert.NotNull(old.ResponseContent);
        Assert.Null(oldPreviewOnly.PromptContent);
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
            db.AiRequests.AddRange(old, recent, oldPreviewOnly);
            await db.SaveChangesAsync(ct);
        }

        Assert.Equal(1, await provider.GetRequiredService<AiContentRetentionService>().CleanupAsync(ct));

        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            var rows = await db.AiRequests.AsNoTracking().ToDictionaryAsync(request => request.Id, ct);
            Assert.Null(rows[old.Id].PromptContent);
            Assert.Null(rows[old.Id].ResponseContent);
            // Governance metadata survives retention.
            Assert.Equal(old.PromptHash, rows[old.Id].PromptHash);
            Assert.Equal(old.PromptPreview, rows[old.Id].PromptPreview);
            Assert.Equal(old.ResponsePreview, rows[old.Id].ResponsePreview);
            Assert.Equal(old.DetectedSensitiveTypesJson, rows[old.Id].DetectedSensitiveTypesJson);
            Assert.Equal(old.PromptTokens, rows[old.Id].PromptTokens);
            Assert.Equal("recent private prompt", rows[recent.Id].PromptContent);
            Assert.NotNull(rows[recent.Id].ResponseContent);
            Assert.Equal(oldPreviewOnly.PromptPreview, rows[oldPreviewOnly.Id].PromptPreview);
        }
        Assert.Equal(0, await provider.GetRequiredService<AiContentRetentionService>().CleanupAsync(ct));
    }

    [Fact]
    public async Task Retention_days_are_configurable() {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=False");
        await connection.OpenAsync(ct);
        await using var provider = Services(connection, retentionDays: 3);
        var request = Request(Policy(storeContent: true), "four days old", DateTimeOffset.UtcNow.AddDays(-4));
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
            db.AiRequests.Add(request);
            await db.SaveChangesAsync(ct);
        }

        Assert.Equal(1, await provider.GetRequiredService<AiContentRetentionService>().CleanupAsync(ct));
    }

    private static AiPolicy Policy(bool storeContent) {
        var policy = AiPolicy.Create("retention", PolicyScope.Global, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        policy.Configure([Guid.NewGuid()], 10, 100, 1000, SensitiveDataAction.Redact, [], true, storeContent, true,
            Guid.NewGuid(), DateTimeOffset.UtcNow);
        return policy;
    }

    private static AiRequest Request(AiPolicy policy, string prompt, DateTimeOffset at) =>
        AiRequest.Record(Guid.NewGuid(), null, null, policy, AiRequestStatus.Completed, null, prompt, prompt,
            "full private response", ["EMAIL"], 1, 12, 34, 5, "corr", at, safeResponse: "safe response");

    private static ServiceProvider Services(SqliteConnection connection, int? retentionDays) {
        var settings = new Dictionary<string, string?>();
        if (retentionDays is not null) settings["Ai:ContentRetentionDays"] = retentionDays.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection).UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCustomizer, SqliteCustomizer>());
        services.AddSingleton<AiContentRetentionService>();
        return services.BuildServiceProvider();
    }

    private sealed class SqliteCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<AegisOps.Domain.Identity.ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<AegisOps.Domain.Policy.Policy>().Ignore(policy => policy.AppliesToTiers);
            modelBuilder.Entity<AiRequest>().Property(request => request.CreatedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
        }
    }
}
