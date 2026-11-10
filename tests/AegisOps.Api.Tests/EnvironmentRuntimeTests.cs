using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AegisOps.Api.Organization;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Docker;
using AegisOps.Infrastructure.Persistence;
using Docker.DotNet.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectEnvironment = AegisOps.Domain.Organization.Environment;

namespace AegisOps.Api.Tests;

public sealed class EnvironmentRuntimeTests {
    private const string Slug = "site-service";
    private const string EnvName = "Staging";
    private const string Target = "site-staging";
    private const string SecretEnv = "DB_PASSWORD=hunter2-runtime-secret";
    private const string SecretLabel = "label-runtime-secret";
    private const string SecretMount = "/srv/runtime-secret-mount";
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------- Worker inspection against a fake Docker client ----------

    [Fact]
    public async Task InspectionKeepsOnlyOwnedTargetContainersAndMapsStatus() {
        using var client = FakeDocker.Standard();

        var containers = await EnvironmentRuntimeMonitor.InspectAsync(client, Slug, EnvName, Target, Token);

        Assert.Equal([Target, Target + "-previous"], containers.Select(item => item.Name));
        var current = containers[0];
        Assert.Equal("running", current.State);
        Assert.Equal("Up 5 minutes (healthy)", current.Status);
        Assert.Equal("healthy", current.Health);
        Assert.Equal("ghcr.io/acme/site@sha256:" + new string('a', 64), current.Image);
        Assert.Equal("sha256:" + new string('b', 64), current.ImageId);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 8, 55, 0, TimeSpan.Zero).AddTicks(1234567), current.StartedAt);
        Assert.Equal(["0.0.0.0:8081->8080/tcp"], current.Ports);
        var previous = containers[1];
        Assert.Equal("exited", previous.State);
        Assert.Null(previous.Health);
        Assert.Null(previous.StartedAt);
        // Only the containers that were owned were inspected; the foreign ones never were.
        Assert.Equal(["owned-current", "owned-previous"], client.Inspected.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task InspectionNeverStoresEnvironmentLabelsMountsOrForeignContainers() {
        using var client = FakeDocker.Standard();

        var containers = await EnvironmentRuntimeMonitor.InspectAsync(client, Slug, EnvName, Target, Token);
        var json = JsonSerializer.Serialize(containers);

        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretLabel, json, StringComparison.Ordinal);
        Assert.DoesNotContain("aegisops.", json, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretMount, json, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres", json, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign", json, StringComparison.Ordinal);
        Assert.DoesNotContain("9999", json, StringComparison.Ordinal);
        var properties = typeof(RuntimeContainer).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal);
        Assert.Equal(["Health", "Image", "ImageId", "Name", "Ports", "StartedAt", "State", "Status"], properties);
    }

    [Fact]
    public async Task InspectionFallsBackToListViewWhenInspectFails() {
        using var client = FakeDocker.Standard();
        client.FailInspect = true;

        var containers = await EnvironmentRuntimeMonitor.InspectAsync(client, Slug, EnvName, Target, Token);

        var current = Assert.Single(containers, item => item.Name == Target);
        Assert.Equal("running", current.State);
        Assert.Equal("ghcr.io/acme/site:1.0.0", current.Image);
        Assert.Null(current.Health);
    }

    [Theory]
    [InlineData("2026-11-10T08:55:00.123456789Z", true)]
    [InlineData("2026-11-10T08:55:00Z", true)]
    [InlineData("0001-01-01T00:00:00Z", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void StartedAtParsesDockerTimestamps(string? value, bool expected) {
        Assert.Equal(expected, EnvironmentRuntimeMonitor.ParseStartedAt(value).HasValue);
    }

    // ---------- Worker refresh persisted in PostgreSQL schema (SQLite in tests) ----------

    [Fact]
    public async Task RefreshPersistsSnapshotsOnlyForDockerTargetsAndUpdatesInPlace() {
        await using var store = await Store.CreateAsync();
        var clock = new MutableTime(Now);
        using var client = FakeDocker.Standard();

        await using (var db = store.Context()) {
            Assert.Equal(1, await EnvironmentRuntimeMonitor.RefreshAsync(db, () => client, clock, null, Token));
        }

        await using (var db = store.Context()) {
            var snapshot = Assert.Single(await db.EnvironmentRuntimeSnapshots.AsNoTracking().ToListAsync(Token));
            Assert.Equal(store.Docker.Id, snapshot.EnvironmentId);
            Assert.True(snapshot.InspectionAvailable);
            Assert.Equal(Now, snapshot.ObservedAt);
            Assert.Equal([Target, Target + "-previous"], snapshot.Containers.Select(item => item.Name));
            Assert.Equal(["0.0.0.0:8081->8080/tcp"], snapshot.Containers[0].Ports);
        }

        clock.Advance(TimeSpan.FromSeconds(20));
        client.Containers.RemoveAll(item => item.ID == "owned-previous");
        await using (var db = store.Context()) {
            await EnvironmentRuntimeMonitor.RefreshAsync(db, () => client, clock, store.Docker.Id, Token);
        }

        await using (var db = store.Context()) {
            var snapshot = Assert.Single(await db.EnvironmentRuntimeSnapshots.AsNoTracking().ToListAsync(Token));
            Assert.Equal(Now.AddSeconds(20), snapshot.ObservedAt);
            Assert.Equal([Target], snapshot.Containers.Select(item => item.Name));
        }
    }

    [Fact]
    public async Task RefreshRecordsDockerUnavailableTruthfully() {
        await using var store = await Store.CreateAsync();

        await using (var db = store.Context()) {
            await EnvironmentRuntimeMonitor.RefreshAsync(db, () => null, new MutableTime(Now), null, Token);
        }

        await using var read = store.Context();
        var snapshot = Assert.Single(await read.EnvironmentRuntimeSnapshots.AsNoTracking().ToListAsync(Token));
        Assert.False(snapshot.InspectionAvailable);
        Assert.Equal(EnvironmentRuntimeMonitor.DockerUnavailable, snapshot.Reason);
        Assert.Empty(snapshot.Containers);
    }

    [Fact]
    public async Task RefreshStoresFixedReasonWhenDaemonFailsWithoutLeakingErrorDetail() {
        await using var store = await Store.CreateAsync();
        using var client = FakeDocker.Standard();
        client.FailList = true;

        await using (var db = store.Context()) {
            await EnvironmentRuntimeMonitor.RefreshAsync(db, () => client, new MutableTime(Now), null, Token);
        }

        await using var read = store.Context();
        var snapshot = Assert.Single(await read.EnvironmentRuntimeSnapshots.AsNoTracking().ToListAsync(Token));
        Assert.False(snapshot.InspectionAvailable);
        Assert.Equal(EnvironmentRuntimeMonitor.InspectionFailed, snapshot.Reason);
        Assert.DoesNotContain("docker.sock", snapshot.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshWithOnlyNoopTargetsNeverCreatesADockerClient() {
        await using var store = await Store.CreateAsync();
        var created = false;

        await using var db = store.Context();
        var written = await EnvironmentRuntimeMonitor.RefreshAsync(db, () => { created = true; return null; },
            new MutableTime(Now), store.Noop.Id, Token);

        Assert.Equal(0, written);
        Assert.False(created);
        Assert.Empty(await db.EnvironmentRuntimeSnapshots.ToListAsync(Token));
    }

    // ---------- Migration / model ----------

    [Fact]
    public async Task SnapshotTableIsCreatedOnSqliteAndCascadesWithEnvironment() {
        await using var store = await Store.CreateAsync();
        await using (var db = store.Context()) {
            var tables = await db.Database.SqlQueryRaw<string>(
                "SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'environment_runtime_snapshots'")
                .ToListAsync(Token);
            Assert.Single(tables);
            await EnvironmentRuntimeMonitor.RefreshAsync(db, () => null, new MutableTime(Now), null, Token);
        }

        await using (var db = store.Context()) {
            db.Environments.Remove(await db.Environments.SingleAsync(item => item.Id == store.Docker.Id, Token));
            await db.SaveChangesAsync(Token);
            Assert.Empty(await db.EnvironmentRuntimeSnapshots.ToListAsync(Token));
        }
    }

    [Fact]
    public void PostgresMigrationAndModelSnapshotIncludeRuntimeSnapshots() {
        var options = new DbContextOptionsBuilder<AegisOpsDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused").UseSnakeCaseNamingConvention()
            .EnableServiceProviderCaching(false).Options;
        using var db = new AegisOpsDbContext(options);

        var migrations = db.GetService<IMigrationsAssembly>();
        Assert.Contains(migrations.Migrations.Keys, id => id.EndsWith("_EnvironmentRuntimeSnapshots", StringComparison.Ordinal));
        var entity = migrations.ModelSnapshot?.Model.FindEntityType(typeof(EnvironmentRuntimeSnapshot).FullName!);
        Assert.NotNull(entity);
        Assert.Equal("environment_runtime_snapshots", entity.GetTableName());
        Assert.Equal("deploy", entity.GetSchema());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    // ---------- API endpoint ----------

    [Fact]
    public async Task EndpointReportsNoopTargetWithoutPretendingToInspect() {
        await using var host = await Host.CreateAsync();
        var body = await host.GetAsync(host.Member, host.Store.Noop.Id, HttpStatusCode.OK);

        Assert.Equal("noop", body.GetProperty("type").GetString());
        Assert.False(body.GetProperty("inspectionAvailable").GetBoolean());
        Assert.Equal("noop target", body.GetProperty("reason").GetString());
        Assert.Equal(0, body.GetProperty("containers").GetArrayLength());
    }

    [Fact]
    public async Task EndpointReportsMissingSnapshot() {
        await using var host = await Host.CreateAsync();
        var body = await host.GetAsync(host.Member, host.Store.Docker.Id, HttpStatusCode.OK);

        Assert.Equal("docker", body.GetProperty("type").GetString());
        Assert.False(body.GetProperty("inspectionAvailable").GetBoolean());
        Assert.Contains("not recorded", body.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("observedAt").ValueKind);
        Assert.True(body.GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("containers").ValueKind);
    }

    [Fact]
    public async Task EndpointReturnsFreshSnapshotWithoutSecrets() {
        await using var host = await Host.CreateAsync();
        using var client = FakeDocker.Standard();
        await host.RefreshAsync(Now.AddSeconds(-5), () => client);

        var raw = await host.GetRawAsync(host.Member, host.Store.Docker.Id, HttpStatusCode.OK);
        var body = JsonDocument.Parse(raw).RootElement;

        Assert.True(body.GetProperty("inspectionAvailable").GetBoolean());
        Assert.False(body.GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("reason").ValueKind);
        Assert.Equal(Now.AddSeconds(-5), body.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(Target, body.GetProperty("containerName").GetString());
        var containers = body.GetProperty("containers");
        Assert.Equal(2, containers.GetArrayLength());
        var current = containers[0];
        Assert.Equal(Target, current.GetProperty("name").GetString());
        Assert.Equal("running", current.GetProperty("state").GetString());
        Assert.Equal("healthy", current.GetProperty("health").GetString());
        Assert.Equal("0.0.0.0:8081->8080/tcp", current.GetProperty("ports")[0].GetString());
        Assert.DoesNotContain("hunter2", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretLabel, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretMount, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("labels", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("postgres", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndpointFlagsStaleSnapshot() {
        await using var host = await Host.CreateAsync();
        using var client = FakeDocker.Standard();
        await host.RefreshAsync(Now - EnvironmentRuntimeMonitor.StaleAfter - TimeSpan.FromSeconds(1), () => client);

        var body = await host.GetAsync(host.Member, host.Store.Docker.Id, HttpStatusCode.OK);

        Assert.True(body.GetProperty("stale").GetBoolean());
        Assert.True(body.GetProperty("inspectionAvailable").GetBoolean());
        Assert.Equal(2, body.GetProperty("containers").GetArrayLength());
        Assert.Contains("not refreshed", body.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndpointReportsDockerUnavailableToWorker() {
        await using var host = await Host.CreateAsync();
        await host.RefreshAsync(Now.AddSeconds(-3), () => null);

        var body = await host.GetAsync(host.Member, host.Store.Docker.Id, HttpStatusCode.OK);

        Assert.False(body.GetProperty("inspectionAvailable").GetBoolean());
        Assert.False(body.GetProperty("stale").GetBoolean());
        Assert.Equal(EnvironmentRuntimeMonitor.DockerUnavailable, body.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("containers").ValueKind);
    }

    [Fact]
    public async Task EndpointEnforcesTeamScopingWithAdminOverride() {
        await using var host = await Host.CreateAsync();
        using var client = FakeDocker.Standard();
        await host.RefreshAsync(Now, () => client);

        await host.GetAsync(host.Member, host.Store.Docker.Id, HttpStatusCode.OK);
        await host.GetAsync(host.Admin, host.Store.Docker.Id, HttpStatusCode.OK);
        var outsider = await host.GetRawAsync(host.Outsider, host.Store.Docker.Id, HttpStatusCode.NotFound);
        Assert.DoesNotContain(Target, outsider, StringComparison.Ordinal);
        await host.GetRawAsync(null, host.Store.Docker.Id, HttpStatusCode.Unauthorized);
        await host.GetRawAsync(host.Member, Guid.NewGuid(), HttpStatusCode.NotFound);
    }

    // ---------- Fixtures ----------

    private sealed class FakeDocker : IDockerRuntimeClient {
        public List<ContainerListResponse> Containers { get; } = [];
        public Dictionary<string, ContainerInspectResponse> Inspections { get; } = [];
        public List<string> Inspected { get; } = [];
        public bool FailList { get; set; }
        public bool FailInspect { get; set; }

        public static FakeDocker Standard() {
            var fake = new FakeDocker();
            fake.Add("owned-current", Target, Owned(EnvName), "running", "Up 5 minutes (healthy)", 8081,
                new ContainerInspectResponse {
                    ID = "owned-current",
                    Image = "sha256:" + new string('b', 64),
                    State = new ContainerState {
                        Status = "running",
                        StartedAt = "2026-11-10T08:55:00.123456789Z",
                        Health = new Docker.DotNet.Models.Health { Status = "healthy" },
                    },
                    Config = new Config {
                        Image = "ghcr.io/acme/site@sha256:" + new string('a', 64),
                        Env = [SecretEnv, "ASPNETCORE_ENVIRONMENT=Staging"],
                        Labels = Owned(EnvName),
                    },
                    Mounts = [new MountPoint { Source = SecretMount, Destination = "/data" }],
                });
            fake.Add("owned-previous", Target + "-previous", Owned(EnvName), "exited", "Exited (0) 1 minute ago", 0,
                new ContainerInspectResponse {
                    ID = "owned-previous",
                    Image = "sha256:" + new string('c', 64),
                    State = new ContainerState { Status = "exited", StartedAt = "0001-01-01T00:00:00Z" },
                    Config = new Config { Image = "ghcr.io/acme/site:0.9.0", Env = [SecretEnv] },
                });
            // Same name but missing the deployment ownership label: never reported.
            var unlabeled = Owned(EnvName);
            unlabeled.Remove("aegisops.deployment");
            fake.Add("foreign-unlabeled", Target, unlabeled, "running", "Up", 9999, null);
            // Owned by another environment of the same project but sharing the name prefix.
            fake.Add("foreign-production", Target + "-previous", Owned("Production"), "running", "Up", 9999, null);
            // Name-filter substring match on an unrelated container.
            fake.Add("foreign-substring", Target + "-postgres", Owned(EnvName), "running", "Up", 9999, null);
            // An unrelated host container.
            fake.Add("postgres", "postgres", new Dictionary<string, string> { ["com.example"] = SecretLabel }, "running", "Up", 5432, null);
            return fake;
        }

        private static Dictionary<string, string> Owned(string environment) => new() {
            ["aegisops.project"] = Slug,
            ["aegisops.environment"] = environment,
            ["aegisops.deployment"] = Guid.NewGuid().ToString("D"),
            ["aegisops.artifact"] = Guid.NewGuid().ToString("D"),
            ["com.example.secret"] = SecretLabel,
        };

        private void Add(string id, string name, Dictionary<string, string> labels, string state, string status, ushort publicPort, ContainerInspectResponse? inspect) {
            Containers.Add(new ContainerListResponse {
                ID = id,
                Names = ["/" + name],
                Labels = labels,
                State = state,
                Status = status,
                Image = "ghcr.io/acme/site:1.0.0",
                ImageID = "sha256:" + new string('d', 64),
                Ports = publicPort > 0
                    ? [new Port { IP = "0.0.0.0", PublicPort = publicPort, PrivatePort = 8080, Type = "tcp" },
                        new Port { IP = "::", PublicPort = publicPort, PrivatePort = 8080, Type = "tcp" }]
                    : [new Port { PrivatePort = 8080, Type = "tcp" }],
                Mounts = [new MountPoint { Source = SecretMount, Destination = "/data" }],
            });
            if (inspect is not null) Inspections[id] = inspect;
        }

        public Task<IList<ContainerListResponse>> ListContainersAsync(string nameFilter, CancellationToken cancellationToken) {
            if (FailList) throw new InvalidOperationException("connect: no such file unix:///var/run/docker.sock");
            // Docker's name filter is a substring match, so unrelated names can come back too.
            IList<ContainerListResponse> result = Containers.ToList();
            return Task.FromResult(result);
        }

        public Task<ContainerInspectResponse> InspectContainerAsync(string id, CancellationToken cancellationToken) {
            Inspected.Add(id);
            if (FailInspect || !Inspections.TryGetValue(id, out var inspect))
                throw new InvalidOperationException("No such container: " + id);
            return Task.FromResult(inspect);
        }

        public void Dispose() { }
    }

    private sealed class Store : IAsyncDisposable {
        private readonly SqliteConnection _connection;
        public ProjectEnvironment Docker { get; private set; } = null!;
        public ProjectEnvironment Noop { get; private set; } = null!;
        public Team Team { get; private set; } = null!;

        private Store(SqliteConnection connection) => _connection = connection;

        public static async Task<Store> CreateAsync() {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(Token);
            var store = new Store(connection);
            await using var db = store.Context();
            await db.Database.EnsureCreatedAsync(Token);
            store.Team = Team.Create("Platform", "platform", Now);
            var project = Project.Create(store.Team.Id, "Site", Slug, Now);
            store.Docker = ProjectEnvironment.Create(project.Id, EnvName, EnvironmentTier.Staging, 1);
            store.Docker.Retarget(DeploymentTarget.Docker(Target, null, 8081, 8080, "/health", 60));
            store.Noop = ProjectEnvironment.Create(project.Id, "Development", EnvironmentTier.Development, 0);
            db.AddRange(store.Team, project, store.Docker, store.Noop);
            await db.SaveChangesAsync(Token);
            return store;
        }

        // The test assembly shares EF's process-wide provider cache, which fails after twenty
        // entries; these contexts opt out of it so they cannot push other test classes over.
        public AegisOpsDbContext Context() => new(new DbContextOptionsBuilder<AegisOpsDbContext>()
            .UseSqlite(_connection).UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCustomizer, SqliteModelCustomizer>()
            .EnableServiceProviderCaching(false).Options);

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class Host : IAsyncDisposable {
        private readonly WebApplication _app;
        private readonly HttpClient _client;
        public Store Store { get; }
        public Guid Member { get; private set; }
        public Guid Outsider { get; private set; }
        public Guid Admin { get; private set; }

        private Host(WebApplication app, Store store) {
            _app = app;
            Store = store;
            _client = app.GetTestClient();
        }

        public static async Task<Host> CreateAsync() {
            var store = await Store.CreateAsync();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton<TimeProvider>(new MutableTime(Now));
            builder.Services.AddScoped(_ => store.Context());
            builder.Services.AddIdentityCore<User>().AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AegisOpsDbContext>();
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/api/v1/projects/{slug}/environments/{envId:guid}/runtime",
                    (string slug, Guid envId, ClaimsPrincipal principal, UserManager<User> users, AegisOpsDbContext db, HttpContext http, TimeProvider time) =>
                        GetEnvironmentRuntime.Handle(slug, envId, principal, users, db, http, time))
                .RequireAuthorization();
            await app.StartAsync(Token);
            var host = new Host(app, store);
            await host.SeedUsersAsync();
            return host;
        }

        private async Task SeedUsersAsync() {
            await using var scope = _app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            db.Roles.AddRange(
                new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "Admin", NormalizedName = "ADMIN" },
                new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "Developer", NormalizedName = "DEVELOPER" });
            await db.SaveChangesAsync(Token);
            async Task<Guid> Add(string email, string role) {
                var user = User.Create(email, email, Now);
                Assert.True((await users.CreateAsync(user)).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
                return user.Id;
            }
            Member = await Add("member@example.test", "Developer");
            Outsider = await Add("outsider@example.test", "Developer");
            Admin = await Add("admin@example.test", "Admin");
            db.TeamMembers.Add(TeamMember.Create(Store.Team.Id, Member, TeamRole.Member, Now));
            await db.SaveChangesAsync(Token);
        }

        public async Task RefreshAsync(DateTimeOffset observedAt, Func<IDockerRuntimeClient?> factory) {
            await using var db = Store.Context();
            await EnvironmentRuntimeMonitor.RefreshAsync(db, factory, new MutableTime(observedAt), null, Token);
        }

        public async Task<string> GetRawAsync(Guid? user, Guid environmentId, HttpStatusCode expected) {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/projects/{Slug}/environments/{environmentId}/runtime");
            if (user is { } id) request.Headers.Add("X-Test-User", id.ToString());
            using var response = await _client.SendAsync(request, Token);
            var body = await response.Content.ReadAsStringAsync(Token);
            Assert.True(expected == response.StatusCode, $"Expected {expected}, got {response.StatusCode}: {body}");
            return body;
        }

        public async Task<JsonElement> GetAsync(Guid user, Guid environmentId, HttpStatusCode expected) =>
            JsonDocument.Parse(await GetRawAsync(user, environmentId, expected)).RootElement.Clone();

        public async ValueTask DisposeAsync() {
            _client.Dispose();
            await _app.DisposeAsync();
            await Store.DisposeAsync();
        }
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder) {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() {
            if (!Guid.TryParse(Request.Headers["X-Test-User"], out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("sub", userId.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class MutableTime(DateTimeOffset start) : TimeProvider {
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
