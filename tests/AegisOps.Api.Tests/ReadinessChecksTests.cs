using AegisOps.Infrastructure.Health;
using AegisOps.Infrastructure.Persistence;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace AegisOps.Api.Tests;

public sealed class ReadinessChecksTests {
    [Fact]
    public async Task Liveness_does_not_probe_dependencies_but_readiness_does() {
        await using var connection = new SqliteConnection("Data Source=/nonexistent/aegisops-readiness-test.sqlite;Mode=ReadOnly");
        // The database cannot open, and Redis is not configured.
        using var services = CreateServices(connection);
        var health = services.GetRequiredService<HealthCheckService>();
        var live = await health.CheckHealthAsync(_ => false, TestContext.Current.CancellationToken);
        var ready = await health.CheckHealthAsync(
            check => check.Tags.Contains("ready"), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, live.Status);
        Assert.Empty(live.Entries);
        Assert.Equal(HealthStatus.Unhealthy, ready.Status);
        Assert.Equal(HealthStatus.Unhealthy, ready.Entries["redis"].Status);
        Assert.Equal(HealthStatus.Unhealthy, ready.Entries["postgres"].Status);
    }

    [Fact]
    public async Task Postgres_probe_accepts_connected_database_without_pending_migrations() {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var services = CreateServices(connection);
        var report = await services.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            check => check.Name == "postgres", TestContext.Current.CancellationToken);

        Assert.True(report.Status == HealthStatus.Healthy,
            $"{report.Entries["postgres"].Description}: {report.Entries["postgres"].Exception}");
    }

    [Fact]
    public async Task Redis_probe_reports_connection_factory_failure_as_unhealthy() {
        using var services = new ServiceCollection()
            .AddSingleton<IConnectionMultiplexer>(_ => throw new InvalidOperationException("connection failed"))
            .BuildServiceProvider();
        var result = await new RedisReadinessCheck(services).CheckHealthAsync(
            new HealthCheckContext(), TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Redis is unavailable.", result.Description);
    }

    [Fact]
    public async Task Worker_poll_becomes_ready_then_stale_after_thirty_seconds() {
        var clock = new TestClock();
        var heartbeat = new WorkerPollHeartbeat(clock);
        var check = new WorkerPollReadinessCheck(heartbeat);
        var context = new HealthCheckContext();
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(context, cancellationToken)).Status);
        heartbeat.MarkSuccessfulPoll();
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(context, cancellationToken)).Status);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(context, cancellationToken)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, HealthStatus.Healthy)]
    [InlineData(HttpStatusCode.ServiceUnavailable, HealthStatus.Degraded)]
    public async Task Ollama_probe_reports_optional_dependency(HttpStatusCode response, HealthStatus expected) {
        using var client = new HttpClient(new StubHandler(response));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Ai:Ollama:BaseUrl"] = "http://localhost:11434/",
        }).Build();
        var result = await new OllamaReadinessCheck(client, configuration)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
    }

    private sealed class TestClock : TimeProvider {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Assert.Equal("http://localhost:11434/api/tags", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private static ServiceProvider CreateServices(SqliteConnection connection) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection,
            sqlite => sqlite.MigrationsAssembly(typeof(ReadinessChecksTests).Assembly.FullName))
            .ReplaceService<IModelCustomizer, SqliteCustomizer>());
        services.AddDependencyReadiness();
        return services.BuildServiceProvider();
    }

    private sealed class SqliteCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<AegisOps.Domain.Identity.ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<AegisOps.Domain.Policy.Policy>().Ignore(policy => policy.AppliesToTiers);
        }
    }
}
