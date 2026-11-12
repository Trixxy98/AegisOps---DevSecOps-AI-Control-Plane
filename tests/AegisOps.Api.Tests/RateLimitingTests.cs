using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AegisOps.Api.Hosting;
using AegisOps.Api.Identity;
using AegisOps.Application.Audit;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AegisOps.Api.Tests;

/// <summary>
/// Roadmap v1.1 item 4: the global per-principal limiter, the <c>uploads</c> and <c>auth</c> policies, and 429 Problem
/// Details with <c>Retry-After</c>. Uses the real <see cref="ApiRateLimiting"/>, JWT and API-key authentication with the
/// same middleware order as Program.cs, and small limits with an hour-long refill so no token comes back mid-test.
/// </summary>
public sealed class RateLimitingTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Limits(int global = 1000, int uploads = 1000, int auth = 1000) => new() {
        ["RateLimits:Global:TokenLimit"] = global.ToString(CultureInfo.InvariantCulture),
        ["RateLimits:Global:TokensPerPeriod"] = "1",
        ["RateLimits:Global:ReplenishmentPeriodSeconds"] = "3600",
        ["RateLimits:Uploads:TokenLimit"] = uploads.ToString(CultureInfo.InvariantCulture),
        ["RateLimits:Uploads:TokensPerPeriod"] = "1",
        ["RateLimits:Uploads:ReplenishmentPeriodSeconds"] = "3600",
        ["RateLimits:Auth:PermitLimit"] = auth.ToString(CultureInfo.InvariantCulture),
        ["RateLimits:Auth:WindowSeconds"] = "3600",
    };

    [Fact]
    public async Task UsersHaveSeparateBudgets() {
        await using var host = await LimitHost.StartAsync(Limits(global: 3));
        var alice = LimitHost.UserToken();
        var bob = LimitHost.UserToken();

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/v1/ping", alice)).StatusCode);
        using var rejected = await host.SendAsync(HttpMethod.Get, "/api/v1/ping", alice);
        await AssertTooManyRequestsAsync(rejected);

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/v1/ping", bob)).StatusCode);
    }

    [Fact]
    public async Task ApiKeysHaveTheirOwnBudgetSeparateFromEachOtherAndFromUsers() {
        await using var host = await LimitHost.StartAsync(Limits(global: 2));
        var first = await host.CreateApiKeyAsync();
        var second = await host.CreateApiKeyAsync();

        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/v1/ping", first)).StatusCode);
        using var rejected = await host.SendAsync(HttpMethod.Get, "/api/v1/ping", first);
        await AssertTooManyRequestsAsync(rejected);

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/v1/ping", second)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/v1/ping", LimitHost.UserToken())).StatusCode);
    }

    [Fact]
    public async Task UploadsPolicyLimitsScanUploadsPerPrincipalWithoutBlockingOtherRoutes() {
        await using var host = await LimitHost.StartAsync(Limits(global: 100, uploads: 2));
        var key = await host.CreateApiKeyAsync();
        var other = await host.CreateApiKeyAsync();
        var upload = $"/api/v1/artifacts/{Guid.NewGuid()}/scans";

        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.Accepted, (await host.SendAsync(HttpMethod.Post, upload, key)).StatusCode);
        using var rejected = await host.SendAsync(HttpMethod.Post, upload, key);
        await AssertTooManyRequestsAsync(rejected);

        // Other routes of the same principal only count against the global budget.
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/v1/ping", key)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await host.SendAsync(HttpMethod.Post, upload, other)).StatusCode);
    }

    [Fact]
    public async Task AuthPolicyStaysPerClientAddressAndAnswersWithRetryAfter() {
        await using var host = await LimitHost.StartAsync(Limits(auth: 2));
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", null)).StatusCode);
        using var rejected = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", null);
        var retryAfter = await AssertTooManyRequestsAsync(rejected);
        Assert.InRange(retryAfter, 1, 3600);
    }

    [Fact]
    public async Task RejectionCarriesRetryAfterFromTheTokenBucket() {
        var settings = Limits(global: 1);
        settings["RateLimits:Global:ReplenishmentPeriodSeconds"] = "30";
        await using var host = await LimitHost.StartAsync(settings);
        var user = LimitHost.UserToken();
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/v1/ping", user)).StatusCode);
        using var rejected = await host.SendAsync(HttpMethod.Get, "/api/v1/ping", user);
        var retryAfter = await AssertTooManyRequestsAsync(rejected);
        Assert.InRange(retryAfter, 1, 30); // one replenishment period, not the 60-second fallback
    }

    [Fact]
    public async Task HealthAndMetricsAreNeverLimitedButAnonymousApiCallsArePerAddress() {
        await using var host = await LimitHost.StartAsync(Limits(global: 1));
        for (var i = 0; i < 20; i++) {
            Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/health/live", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/metrics", null)).StatusCode);
        }

        // Anonymous requests share the client-address bucket: the first reaches authorization (401), the next is 429.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/api/v1/ping", null)).StatusCode);
        using var rejected = await host.SendAsync(HttpMethod.Get, "/api/v1/ping", null);
        await AssertTooManyRequestsAsync(rejected);
        // An authenticated user is not affected by the anonymous bucket.
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/v1/ping", LimitHost.UserToken())).StatusCode);
    }

    [Fact]
    public async Task HubsHaveASeparateBucketFromTheApi() {
        await using var host = await LimitHost.StartAsync(Limits(global: 1));
        var user = LimitHost.UserToken();
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/v1/ping", user)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.SendAsync(HttpMethod.Get, "/api/v1/ping", user)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Post, "/hubs/ops/negotiate", user)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.SendAsync(HttpMethod.Post, "/hubs/ops/negotiate", user)).StatusCode);
    }

    [Fact]
    public void DefaultsAndInvalidSettings() {
        var empty = new ConfigurationBuilder().Build();
        Assert.Equal(new ApiRateLimiting.TokenBucketSettings(300, 50, 10),
            ApiRateLimiting.TokenBucketSettings.Read(empty.GetSection("RateLimits:Global"), ApiRateLimiting.GlobalDefaults));
        Assert.Equal(new ApiRateLimiting.TokenBucketSettings(30, 5, 10),
            ApiRateLimiting.TokenBucketSettings.Read(empty.GetSection("RateLimits:Uploads"), ApiRateLimiting.UploadsDefaults));
        Assert.Equal(new ApiRateLimiting.FixedWindowSettings(10, 60),
            ApiRateLimiting.FixedWindowSettings.Read(empty.GetSection("RateLimits:Auth"), ApiRateLimiting.AuthDefaults));

        foreach (var (key, value) in new[] {
            ("RateLimits:Global:TokenLimit", "0"),
            ("RateLimits:Global:TokensPerPeriod", "301"),
            ("RateLimits:Uploads:ReplenishmentPeriodSeconds", "0"),
            ("RateLimits:Auth:PermitLimit", "-1"),
            ("RateLimits:Auth:WindowSeconds", "0"),
        }) {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
            var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddApiRateLimiting(configuration));
            Assert.StartsWith(key[..key.LastIndexOf(':')], error.Message, StringComparison.Ordinal);
        }
    }

    private static async Task<int> AssertTooManyRequestsAsync(HttpResponseMessage response) {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var header = Assert.Single(response.Headers.GetValues("Retry-After"));
        var seconds = int.Parse(header, NumberStyles.None, CultureInfo.InvariantCulture);
        Assert.True(seconds >= 1);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(429, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Too many requests", problem.RootElement.GetProperty("title").GetString());
        Assert.Contains(header, problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        return seconds;
    }

    private sealed class LimitHost : IAsyncDisposable {
        private static readonly JwtOptions Jwt = new() {
            Issuer = "aegisops-tests",
            Audience = "aegisops-tests",
            SigningKey = "rate-limit-tests-signing-key-0123456789",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7,
        };

        private readonly WebApplication _app;
        private readonly SqliteConnection _connection;
        private readonly HttpClient _client;
        private Guid _projectId;
        private Guid _ownerId;

        private LimitHost(WebApplication app, SqliteConnection connection) {
            _app = app;
            _connection = connection;
            _client = app.GetTestClient();
        }

        public static async Task<LimitHost> StartAsync(Dictionary<string, string?> settings) {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(Token);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Jwt:Issuer"] = Jwt.Issuer,
                ["Jwt:Audience"] = Jwt.Audience,
                ["Jwt:SigningKey"] = Jwt.SigningKey,
                ["Jwt:AccessTokenMinutes"] = Jwt.AccessTokenMinutes.ToString(CultureInfo.InvariantCulture),
            });
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection)
                .UseSnakeCaseNamingConvention().EnableServiceProviderCaching(false)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>());
            builder.Services.AddIdentityCore<User>(IdentityStoreExtensions.ConfigureIdentity)
                .AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AegisOpsDbContext>();
            builder.Services.AddScoped<IAuditWriter, EfAuditWriter>();
            builder.Services.AddJwtAuthentication(builder.Configuration);
            builder.Services.AddApiProblemDetails();
            builder.Services.AddApiRateLimiting(builder.Configuration);

            var app = builder.Build();
            // Same order as Program.cs.
            app.UseAuthentication();
            app.UseRateLimiter();
            app.UseAuthorization();
            app.MapGet("/health/live", () => Results.Ok());
            app.MapGet("/metrics", () => Results.Ok());
            app.MapPost("/hubs/ops/negotiate", () => Results.Ok()).RequireAuthorization();
            app.MapPost("/api/v1/auth/refresh", () => Results.Ok()).RequireRateLimiting(ApiRateLimiting.AuthPolicy);
            app.MapGet("/api/v1/ping", () => Results.Ok()).RequireAuthorization();
            app.MapPost("/api/v1/artifacts/{id:guid}/scans", (Guid id) => Results.Accepted())
                .RequireAuthorization()
                .RequireRateLimiting(ApiRateLimiting.UploadsPolicy);
            await app.StartAsync(Token);

            var host = new LimitHost(app, connection);
            await host.SeedAsync();
            return host;
        }

        private async Task SeedAsync() {
            await using var scope = _app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            await db.Database.EnsureCreatedAsync(Token);
            var now = DateTimeOffset.UtcNow;
            var owner = User.Create("owner@example.test", "owner", now);
            var team = Team.Create("Platform", "platform", now);
            var project = Project.Create(team.Id, "circuit", "circuit", now);
            db.AddRange(owner, team, project);
            await db.SaveChangesAsync(Token);
            _projectId = project.Id;
            _ownerId = owner.Id;
        }

        public static string UserToken() {
            var user = User.Create($"{Guid.NewGuid():N}@example.test", "user", DateTimeOffset.UtcNow);
            return JwtAccessTokenFactory.Create(user, ["Developer"], Jwt, DateTimeOffset.UtcNow);
        }

        public async Task<string> CreateApiKeyAsync() {
            await using var scope = _app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            var generated = ApiKeyGenerator.Create();
            var now = DateTimeOffset.UtcNow;
            db.ApiKeys.Add(ApiKey.Create(_projectId, "ci", generated.Prefix, generated.Hash, ["scans:write"], now.AddDays(1),
                _ownerId, now.AddMinutes(-1)));
            await db.SaveChangesAsync(Token);
            return generated.Plaintext;
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer) {
            var request = new HttpRequestMessage(method, path);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return _client.SendAsync(request, Token);
        }

        public async ValueTask DisposeAsync() {
            _client.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    /// <summary>SQLite has no text[] columns: store API-key scopes as comma-separated text.</summary>
    private sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ApiKey>().Property(key => key.Scopes)
                .HasConversion(new ValueConverter<IReadOnlyList<string>, string>(
                    scopes => string.Join(",", scopes),
                    text => text.Split(',', StringSplitOptions.RemoveEmptyEntries)))
                .HasColumnType("TEXT");
            modelBuilder.Entity<Policy>().Ignore(policy => policy.AppliesToTiers);
        }
    }
}
