using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using AegisOps.Api.Identity;
using AegisOps.Application.Audit;
using AegisOps.Domain.Deploy;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AegisOps.Api.Tests;

/// <summary>
/// Exercises the real login, refresh, logout, JWT bearer and API-key authentication paths
/// end to end over HTTP (TestServer + SQLite), covering roadmap checks P1-02, P1-03 and P1-06.
/// </summary>
public sealed class IdentityFlowTests {
    private const string Password = "correct-horse-battery-staple";
    private const string WhoAmI = "/test/whoami";

    [Fact]
    public async Task LoginIssuesUsableAccessTokenHardenedRefreshCookieAndAuditEntry() {
        await using var host = await IdentityHost.StartAsync();
        var userId = await host.CreateUserAsync("dev@example.test");

        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("dev@example.test", Password), IdentityHost.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>(IdentityHost.Token);
        Assert.False(string.IsNullOrWhiteSpace(body!.AccessToken));
        Assert.Equal(host.Time.GetUtcNow().AddMinutes(15), body.ExpiresAt);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{RefreshCookie.Name}=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"path={RefreshCookie.Path}", cookie, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(userId.ToString(), await host.WhoAmIAsync(body.AccessToken));

        await host.WithDbAsync(async db => {
            var stored = Assert.Single(await db.RefreshTokens.ToListAsync(IdentityHost.Token));
            Assert.Equal(userId, stored.UserId);
            Assert.NotEqual(ReadCookieValue(cookie), stored.TokenHash);
            Assert.Equal(RefreshTokenHasher.Hash(ReadCookieValue(cookie)), stored.TokenHash);
            var audit = Assert.Single(await db.AuditEvents.ToListAsync(IdentityHost.Token));
            Assert.Equal("auth.login", audit.Action);
            Assert.Equal(userId, audit.ActorId);
        });
    }

    [Theory]
    [InlineData("dev@example.test", "wrong-password-value")]
    [InlineData("nobody@example.test", Password)]
    [InlineData("inactive@example.test", Password)]
    public async Task LoginRejectsBadCredentialsAndInactiveUsersWithoutIssuingTokens(string email, string password) {
        await using var host = await IdentityHost.StartAsync();
        await host.CreateUserAsync("dev@example.test");
        await host.CreateUserAsync("inactive@example.test", active: false);

        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, password), IdentityHost.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await host.WithDbAsync(async db => {
            Assert.Empty(await db.RefreshTokens.ToListAsync(IdentityHost.Token));
            Assert.Empty(await db.AuditEvents.ToListAsync(IdentityHost.Token));
        });
    }

    [Fact]
    public async Task LoginRequiresEmailAndPassword() {
        await using var host = await IdentityHost.StartAsync();
        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(" ", ""), IdentityHost.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RefreshRotatesTokenAndReuseOfRotatedTokenRevokesWholeFamily() {
        await using var host = await IdentityHost.StartAsync();
        var userId = await host.CreateUserAsync("dev@example.test");
        var (_, first) = await host.LoginAsync("dev@example.test");

        using var rotated = await host.RefreshAsync(first);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = ReadCookieValue(Assert.Single(rotated.Headers.GetValues("Set-Cookie")));
        Assert.NotEqual(first, second);
        var refreshed = await rotated.Content.ReadFromJsonAsync<LoginResponse>(IdentityHost.Token);
        Assert.Equal(userId.ToString(), await host.WhoAmIAsync(refreshed!.AccessToken));

        await host.WithDbAsync(async db => {
            var tokens = await db.RefreshTokens.AsNoTracking().ToListAsync(IdentityHost.Token);
            Assert.Equal(2, tokens.Count);
            var old = tokens.Single(token => token.TokenHash == RefreshTokenHasher.Hash(first));
            var next = tokens.Single(token => token.TokenHash == RefreshTokenHasher.Hash(second));
            Assert.Equal(next.Id, old.ReplacedByTokenId);
            Assert.NotNull(old.RevokedAt);
            Assert.Null(next.RevokedAt);
            Assert.Equal(old.FamilyId, next.FamilyId);
        });

        // Replaying the already-rotated token signals theft: it fails and kills the live successor too.
        using var replay = await host.RefreshAsync(first);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var successor = await host.RefreshAsync(second);
        Assert.Equal(HttpStatusCode.Unauthorized, successor.StatusCode);
        await host.WithDbAsync(async db =>
            Assert.All(await db.RefreshTokens.AsNoTracking().ToListAsync(IdentityHost.Token),
                token => Assert.NotNull(token.RevokedAt)));
    }

    [Fact]
    public async Task RefreshRejectsMissingUnknownExpiredAndDeactivatedUserTokens() {
        await using var host = await IdentityHost.StartAsync();
        var userId = await host.CreateUserAsync("dev@example.test");

        using (var missing = await host.Client.PostAsync("/api/v1/auth/refresh", null, IdentityHost.Token))
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using (var unknown = await host.RefreshAsync("not-a-real-token"))
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        var (_, expiring) = await host.LoginAsync("dev@example.test");
        host.Time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        using (var expired = await host.RefreshAsync(expiring))
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        var (_, live) = await host.LoginAsync("dev@example.test");
        await host.WithDbAsync(async db => {
            var user = await db.Users.SingleAsync(item => item.Id == userId, IdentityHost.Token);
            user.Deactived();
            await db.SaveChangesAsync(IdentityHost.Token);
        });
        using (var deactivated = await host.RefreshAsync(live))
            Assert.Equal(HttpStatusCode.Unauthorized, deactivated.StatusCode);
        await host.WithDbAsync(async db => {
            var token = await db.RefreshTokens.AsNoTracking()
                .SingleAsync(item => item.TokenHash == RefreshTokenHasher.Hash(live), IdentityHost.Token);
            Assert.NotNull(token.RevokedAt);
        });
    }

    [Fact]
    public async Task LogoutRevokesRefreshFamilyAndIsAudited() {
        await using var host = await IdentityHost.StartAsync();
        var userId = await host.CreateUserAsync("dev@example.test");
        var (accessToken, refresh) = await host.LoginAsync("dev@example.test");

        using var unauthenticated = await host.Client.PostAsync("/api/v1/auth/logout", null, IdentityHost.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Cookie", $"{RefreshCookie.Name}={refresh}");
        using var logout = await host.Client.SendAsync(request, IdentityHost.Token);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var afterLogout = await host.RefreshAsync(refresh);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
        await host.WithDbAsync(async db => {
            Assert.Contains(await db.AuditEvents.ToListAsync(IdentityHost.Token),
                item => item.Action == "auth.logout" && item.ActorId == userId);
        });
    }

    [Fact]
    public async Task LogoutRefusesToRevokeAnotherUsersRefreshToken() {
        await using var host = await IdentityHost.StartAsync();
        await host.CreateUserAsync("alice@example.test");
        await host.CreateUserAsync("mallory@example.test");
        var (_, aliceRefresh) = await host.LoginAsync("alice@example.test");
        var (malloryAccess, _) = await host.LoginAsync("mallory@example.test");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", malloryAccess);
        request.Headers.Add("Cookie", $"{RefreshCookie.Name}={aliceRefresh}");
        using var logout = await host.Client.SendAsync(request, IdentityHost.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, logout.StatusCode);
        using var aliceStillWorks = await host.RefreshAsync(aliceRefresh);
        Assert.Equal(HttpStatusCode.OK, aliceStillWorks.StatusCode);
    }

    [Fact]
    public async Task ForgedOrExpiredAccessTokensAreRejected() {
        await using var host = await IdentityHost.StartAsync();
        await host.CreateUserAsync("dev@example.test");
        var (accessToken, _) = await host.LoginAsync("dev@example.test");

        Assert.Null(await host.WhoAmIAsync(accessToken[..^4] + "AAAA"));
        Assert.Null(await host.WhoAmIAsync(null));

        // JWT lifetime validation uses wall-clock time; mint a token that expired in the past.
        var stale = await host.WithDbAsync(async db => {
            var user = await db.Users.SingleAsync(IdentityHost.Token);
            return JwtAccessTokenFactory.Create(user, [], IdentityHost.Jwt, DateTimeOffset.UtcNow.AddHours(-2));
        });
        Assert.Null(await host.WhoAmIAsync(stale));
    }

    [Fact]
    public async Task ValidApiKeyAuthenticatesAsProjectScopedPrincipalAndRecordsUse() {
        await using var host = await IdentityHost.StartAsync();
        var userId = await host.CreateUserAsync("owner@example.test");
        var (keyId, plaintext) = await host.CreateApiKeyAsync(userId, host.Time.GetUtcNow().AddDays(30));

        Assert.Equal($"apiKey:{keyId}", await host.WhoAmIAsync(plaintext));
        await host.WithDbAsync(async db => {
            var key = await db.ApiKeys.AsNoTracking().SingleAsync(IdentityHost.Token);
            Assert.Equal(host.Time.GetUtcNow(), key.LastUsedAt);
            Assert.True(ApiKeyGenerator.TryParse(plaintext, out _, out var secret));
            Assert.DoesNotContain(secret, key.KeyHash);
        });
    }

    [Fact]
    public async Task ApiKeyWithWrongSecretMalformedExpiredOrRevokedIsRejected() {
        await using var host = await IdentityHost.StartAsync();
        var userId = await host.CreateUserAsync("owner@example.test");
        var (_, plaintext) = await host.CreateApiKeyAsync(userId, host.Time.GetUtcNow().AddDays(1));
        Assert.True(ApiKeyGenerator.TryParse(plaintext, out var prefix, out var secret));

        Assert.Null(await host.WhoAmIAsync($"aok_{prefix}_{new string('x', secret.Length)}"));
        Assert.Null(await host.WhoAmIAsync("aok_short_secret"));
        Assert.Null(await host.WhoAmIAsync($"aok_ZZZZZZZZ_{secret}"));

        var (revokedId, revoked) = await host.CreateApiKeyAsync(userId, host.Time.GetUtcNow().AddDays(30));
        await host.WithDbAsync(async db => {
            var key = await db.ApiKeys.SingleAsync(item => item.Id == revokedId, IdentityHost.Token);
            key.Revoke(host.Time.GetUtcNow());
            await db.SaveChangesAsync(IdentityHost.Token);
        });
        Assert.Null(await host.WhoAmIAsync(revoked));

        host.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        Assert.Null(await host.WhoAmIAsync(plaintext));
        await host.WithDbAsync(async db =>
            Assert.All(await db.ApiKeys.AsNoTracking().ToListAsync(IdentityHost.Token), key => Assert.Null(key.LastUsedAt)));
    }

    private static string ReadCookieValue(string setCookie) {
        var pair = setCookie.Split(';', 2)[0];
        return pair[(pair.IndexOf('=') + 1)..];
    }

    private sealed class IdentityHost : IAsyncDisposable {
        public static readonly JwtOptions Jwt = new() {
            Issuer = "aegisops-tests",
            Audience = "aegisops-tests",
            SigningKey = "identity-flow-tests-signing-key-0123456789",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7,
        };

        private readonly WebApplication _app;
        private readonly SqliteConnection _connection;

        private IdentityHost(WebApplication app, SqliteConnection connection, MutableTimeProvider time) {
            _app = app;
            _connection = connection;
            Time = time;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }
        public MutableTimeProvider Time { get; }
        public static CancellationToken Token => TestContext.Current.CancellationToken;

        public static async Task<IdentityHost> StartAsync() {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var time = new MutableTimeProvider(DateTimeOffset.UtcNow);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Jwt:Issuer"] = Jwt.Issuer,
                ["Jwt:Audience"] = Jwt.Audience,
                ["Jwt:SigningKey"] = Jwt.SigningKey,
                ["Jwt:AccessTokenMinutes"] = Jwt.AccessTokenMinutes.ToString(CultureInfo.InvariantCulture),
                ["Jwt:RefreshTokenDays"] = Jwt.RefreshTokenDays.ToString(CultureInfo.InvariantCulture),
            });
            builder.Services.AddSingleton<TimeProvider>(time);
            builder.Services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection)
                .UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteModelCustomizer>());
            builder.Services.AddIdentityCore<User>(identity => {
                    identity.User.RequireUniqueEmail = true;
                    identity.Password.RequiredLength = 12;
                    identity.Password.RequireDigit = false;
                    identity.Password.RequireLowercase = false;
                    identity.Password.RequireUppercase = false;
                    identity.Password.RequireNonAlphanumeric = false;
                })
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<AegisOpsDbContext>();
            builder.Services.AddScoped<IAuditWriter, EfAuditWriter>();
            builder.Services.AddJwtAuthentication(builder.Configuration);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapPost("/api/v1/auth/login", (LoginRequest body, UserManager<User> users, IOptions<JwtOptions> jwt,
                TimeProvider clock, AegisOpsDbContext db, IAuditWriter audit, HttpContext http) =>
                Login.Handle(body, users, jwt, clock, db, audit, http));
            app.MapPost("/api/v1/auth/refresh", (HttpContext http, AegisOpsDbContext db, UserManager<User> users,
                IOptions<JwtOptions> jwt, TimeProvider clock) => Refresh.Handle(http, db, users, jwt, clock));
            app.MapPost("/api/v1/auth/logout", (HttpContext http, ClaimsPrincipal principal, AegisOpsDbContext db,
                IAuditWriter audit, TimeProvider clock) => Logout.Handle(http, principal, db, audit, clock))
                .RequireAuthorization();
            app.MapGet(WhoAmI, (HttpContext http) => http.User.FindFirst("sub")?.Value ?? "").RequireAuthorization();
            await app.StartAsync(TestContext.Current.CancellationToken);

            var host = new IdentityHost(app, connection, time);
            await host.WithDbAsync(db => db.Database.EnsureCreatedAsync(IdentityHost.Token));
            return host;
        }

        public async Task<Guid> CreateUserAsync(string email, bool active = true) {
            await using var scope = _app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = User.Create(email, email.Split('@')[0], Time.GetUtcNow());
            if (!active) user.Deactived();
            var result = await users.CreateAsync(user, Password);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
            return user.Id;
        }

        public async Task<(Guid Id, string Plaintext)> CreateApiKeyAsync(Guid ownerId, DateTimeOffset expiresAt) {
            var generated = ApiKeyGenerator.Create();
            return await WithDbAsync(async db => {
                var project = await db.Projects.FirstOrDefaultAsync(Token);
                if (project is null) {
                    var team = Team.Create("CI", $"ci-{Guid.NewGuid():N}"[..12], Time.GetUtcNow());
                    project = Project.Create(team.Id, "Circuit", "circuit-service", Time.GetUtcNow());
                    db.AddRange(team, project);
                }
                var key = ApiKey.Create(project.Id, "ci", generated.Prefix, generated.Hash,
                    ["artifacts:write"], expiresAt, ownerId, Time.GetUtcNow());
                db.ApiKeys.Add(key);
                await db.SaveChangesAsync(Token);
                return (key.Id, generated.Plaintext);
            });
        }

        public async Task<(string AccessToken, string Refresh)> LoginAsync(string email) {
            using var response = await Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password), Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<LoginResponse>(Token);
            return (body!.AccessToken, ReadCookieValue(Assert.Single(response.Headers.GetValues("Set-Cookie"))));
        }

        public Task<HttpResponseMessage> RefreshAsync(string refreshToken) {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
            request.Headers.Add("Cookie", $"{RefreshCookie.Name}={refreshToken}");
            return Client.SendAsync(request, Token);
        }

        /// <summary>Returns the authenticated subject, or null when the request is rejected with 401.</summary>
        public async Task<string?> WhoAmIAsync(string? bearer) {
            using var request = new HttpRequestMessage(HttpMethod.Get, WhoAmI);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await Client.SendAsync(request, Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync(Token);
        }

        public async Task WithDbAsync(Func<AegisOpsDbContext, Task> action) {
            await using var scope = _app.Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>());
        }

        public async Task<T> WithDbAsync<T>(Func<AegisOpsDbContext, Task<T>> action) {
            await using var scope = _app.Services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>());
        }

        public async ValueTask DisposeAsync() {
            Client.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<Policy>().Ignore(policy => policy.AppliesToTiers);
        }
    }
}
