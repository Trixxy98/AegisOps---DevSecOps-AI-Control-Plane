using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AegisOps.Api.Identity;
using AegisOps.Api.Organization;
using AegisOps.Application.Audit;
using AegisOps.Application.Authorization;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
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

namespace AegisOps.Api.Tests;

/// <summary>
/// Drives the real project, repository, environment-target, rule-catalogue and policy-simulation
/// handlers over HTTP with real JWT + permission authorization (roadmap checks P1-08, P1-09, P1-12).
/// </summary>
public sealed class ProjectAndPolicyEndpointTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreatingProjectProvisionsOrderedNoopEnvironmentsAndAudits() {
        await using var host = await OrgHost.StartAsync();
        var (adminId, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var teamId = await host.CreateTeamAsync();

        using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/projects", admin,
            new CreateProjectRequest(teamId, "Circuit Service", "  Circuit-Service ", "Demo target"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/v1/projects/circuit-service", created.Headers.Location?.OriginalString);
        var project = await created.Content.ReadFromJsonAsync<ProjectResponse>(Token);
        Assert.Equal("circuit-service", project!.Slug);
        Assert.Equal(teamId, project.TeamId);
        Assert.Null(project.Repository);
        Assert.Equal(["Development", "Staging", "Production"], project.Environments.Select(env => env.Tier));
        Assert.Equal([0, 1, 2], project.Environments.Select(env => env.Order));
        Assert.All(project.Environments, env => {
            Assert.Equal("noop", env.TargetType);
            Assert.False(env.AllowApiKeyProduction);
            Assert.Null(env.CurrentArtifactId);
        });

        await host.WithDbAsync(async db => {
            var stored = await db.Environments.AsNoTracking().Where(env => env.ProjectId == project.Id).ToListAsync(Token);
            Assert.Equal(3, stored.Count);
            Assert.Equal(project.Environments.Select(env => env.Id).Order(), stored.Select(env => env.Id).Order());
            var audit = Assert.Single(await db.AuditEvents.AsNoTracking().ToListAsync(Token));
            Assert.Equal(("project.created", adminId, project.Id), (audit.Action, audit.ActorId, audit.ResourceId));
        });

        using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/projects/circuit-service", admin);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var fetched = await read.Content.ReadFromJsonAsync<ProjectResponse>(Token);
        Assert.Equal(project.Environments.Select(env => (env.Id, env.Tier, env.Order)),
            fetched!.Environments.Select(env => (env.Id, env.Tier, env.Order)));
    }

    [Fact]
    public async Task CreatingProjectRejectsInvalidDuplicateUnknownTeamAndUnprivilegedRequests() {
        await using var host = await OrgHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (_, developer) = await host.CreateUserAsync("dev@example.test", "Developer");
        var teamId = await host.CreateTeamAsync();

        async Task<HttpStatusCode> Create(string bearer, CreateProjectRequest body) {
            using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/projects", bearer, body);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Created, await Create(admin, new(teamId, "Circuit", "circuit", null)));
        Assert.Equal(HttpStatusCode.Conflict, await Create(admin, new(teamId, "Circuit 2", "CIRCUIT", null)));
        Assert.Equal(HttpStatusCode.NotFound, await Create(admin, new(Guid.NewGuid(), "Ghost", "ghost", null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(admin, new(Guid.Empty, "Bad", "bad", null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(admin, new(teamId, " ", "blank", null)));
        // docs/09: projects:write = Admin or Owner of the team. A Developer who is not Owner reaches the handler and is
        // refused without revealing the team (404); Security lacks projects:write entirely (403).
        Assert.Equal(HttpStatusCode.NotFound, await Create(developer, new(teamId, "Sneaky", "sneaky", null)));
        var (_, security) = await host.CreateUserAsync("security@example.test", "Security");
        Assert.Equal(HttpStatusCode.Forbidden, await Create(security, new(teamId, "Sneaky", "sneaky-sec", null)));
        using (var anonymous = await host.SendAsync(HttpMethod.Post, "/api/v1/projects", null, new CreateProjectRequest(teamId, "Anon", "anon", null)))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await host.WithDbAsync(async db => {
            Assert.Equal(["circuit"], await db.Projects.Select(project => project.Slug).ToListAsync(Token));
            Assert.Equal(3, await db.Environments.CountAsync(Token));
        });
    }

    [Fact]
    public async Task RepositoryConfigurationIsCreatedThenReplacedInPlaceAndAudited() {
        await using var host = await OrgHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var project = await host.CreateProjectAsync(admin);

        using var first = await host.SendAsync(HttpMethod.Put, "/api/v1/projects/circuit-service/repository", admin,
            new UpsertRepositoryRequest("github", "datasc/circuit-service", "main", "https://github.com/datasc/circuit-service"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<RepositoryResponse>(Token);
        Assert.Equal(("GitHub", "datasc/circuit-service", "main", project.Id),
            (created!.Provider, created.FullName, created.DefaultBranch, created.ProjectId));

        using var second = await host.SendAsync(HttpMethod.Put, "/api/v1/projects/circuit-service/repository", admin,
            new UpsertRepositoryRequest("GitHub", "datasc/circuit-svc", "release", "https://github.com/datasc/circuit-svc"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var replaced = await second.Content.ReadFromJsonAsync<RepositoryResponse>(Token);
        Assert.Equal(created.Id, replaced!.Id);
        Assert.Equal(("datasc/circuit-svc", "release"), (replaced.FullName, replaced.DefaultBranch));

        using var invalid = await host.SendAsync(HttpMethod.Put, "/api/v1/projects/circuit-service/repository", admin,
            new UpsertRepositoryRequest("gitlab", "x/y", "main", "https://gitlab.com/x/y"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var missing = await host.SendAsync(HttpMethod.Put, "/api/v1/projects/nope/repository", admin,
            new UpsertRepositoryRequest("github", "x/y", "main", "https://github.com/x/y"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/projects/circuit-service", admin);
        var fetched = await read.Content.ReadFromJsonAsync<ProjectResponse>(Token);
        Assert.Equal(replaced.Id, fetched!.Repository?.Id);
        Assert.Equal("datasc/circuit-svc", fetched.Repository?.FullName);

        await host.WithDbAsync(async db => {
            Assert.Equal(1, await db.Repositories.CountAsync(Token));
            Assert.Equal(2, await db.AuditEvents.CountAsync(item => item.Action == "repository.upserted", Token));
        });
    }

    [Fact]
    public async Task EnvironmentTargetCanBeSetToNoopAndRejectsUnknownTypesAndOtherProjects() {
        await using var host = await OrgHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (_, developer) = await host.CreateUserAsync("dev@example.test", "Developer");
        var project = await host.CreateProjectAsync(admin);
        var other = await host.CreateProjectAsync(admin, "other-service");
        var production = project.Environments.Single(env => env.Tier == "Production");
        var path = $"/api/v1/projects/circuit-service/environments/{production.Id}";

        using var updated = await host.SendAsync(HttpMethod.Patch, path, admin, new UpdateEnvironmentRequest("Prod", null,
            new UpdateEnvironmentTargetRequest("NOOP", true, null, null, null, null, null, null)));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await updated.Content.ReadFromJsonAsync<EnvironmentResponse>(Token);
        Assert.Equal(("Prod", "noop", true, "Production"), (body!.Name, body.TargetType, body.AllowApiKeyProduction, body.Tier));

        using var badType = await host.SendAsync(HttpMethod.Patch, path, admin, new UpdateEnvironmentRequest(null, null,
            new UpdateEnvironmentTargetRequest("kubernetes", null, null, null, null, null, null, null)));
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        using var empty = await host.SendAsync(HttpMethod.Patch, path, admin, new UpdateEnvironmentRequest(null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var foreignEnv = other.Environments.Single(env => env.Tier == "Production").Id;
        using var crossProject = await host.SendAsync(HttpMethod.Patch, $"/api/v1/projects/circuit-service/environments/{foreignEnv}",
            admin, new UpdateEnvironmentRequest("Hijack", null, null));
        Assert.Equal(HttpStatusCode.NotFound, crossProject.StatusCode);
        // docs/09: environments:write = Admin or Owner of the team; a non-Owner Developer is refused by the handler (404).
        using var forbidden = await host.SendAsync(HttpMethod.Patch, path, developer, new UpdateEnvironmentRequest("Dev rename", null, null));
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);

        await host.WithDbAsync(async db => {
            var stored = await db.Environments.AsNoTracking().SingleAsync(env => env.Id == production.Id, Token);
            Assert.Equal(("Prod", "noop", true), (stored.Name, stored.Target.Type, stored.Target.AllowApiKeyProduction));
            Assert.Equal("Production", (await db.Environments.AsNoTracking().SingleAsync(env => env.Id == foreignEnv, Token)).Name);
        });
    }

    [Fact]
    public async Task RuleTypeCatalogueListsEveryEngineRuleWithValidEffectsForAnyPolicyReader() {
        await using var host = await OrgHost.StartAsync();
        var (_, developer) = await host.CreateUserAsync("dev@example.test", "Developer");

        using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/policies/rule-types", developer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var items = json.RootElement.EnumerateArray().ToArray();

        Assert.Equal(Enum.GetNames<PolicyRuleType>().Order(), items.Select(item => item.GetProperty("type").GetString()!).Order());
        Assert.All(items, item => {
            Assert.True(Enum.TryParse<RuleEffect>(item.GetProperty("effect").GetString(), out _));
            Assert.Equal(JsonValueKind.Object, item.GetProperty("parameters").ValueKind);
        });

        using var anonymous = await host.SendAsync(HttpMethod.Get, "/api/v1/policies/rule-types", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task SimulationReturnsDecisionPerTierAndArtifactWithoutPersistingAnything() {
        await using var host = await OrgHost.StartAsync();
        var (adminId, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (_, developer) = await host.CreateUserAsync("dev@example.test", "Developer");
        var project = await host.CreateProjectAsync(admin);
        var staging = project.Environments.Single(env => env.Tier == "Staging").Id;
        var production = project.Environments.Single(env => env.Tier == "Production").Id;

        var (good, risky) = await host.WithDbAsync(async db => {
            var now = DateTimeOffset.UtcNow;
            var policy = Policy.Create("Production gate", PolicyScope.Global, [EnvironmentTier.Production], adminId, now);
            var approvals = Policy.Create("Two-person rule", PolicyScope.Project, [EnvironmentTier.Production], adminId, now, project.Id);
            var otherProject = Policy.Create("Unrelated project", PolicyScope.Project, [EnvironmentTier.Staging], adminId, now, Guid.NewGuid());
            db.AddRange(policy, approvals, otherProject,
                PolicyRule.Create(policy.Id, PolicyRuleType.RequireTestsPassed, RuleEffect.Deny, 0),
                PolicyRule.Create(policy.Id, PolicyRuleType.AllowedBranches, RuleEffect.Deny, 1, """{"patterns":["main"]}"""),
                PolicyRule.Create(approvals.Id, PolicyRuleType.RequireApprovals, RuleEffect.RequireApproval, 0, """{"count":2}"""),
                PolicyRule.Create(otherProject.Id, PolicyRuleType.RequireImageDigest, RuleEffect.Deny, 0));
            var goodArtifact = Artifact.Create(project.Id, "1.8.2", new string('a', 40), "main", "ghcr.io/datasc/circuit:1.8.2",
                CheckStatus.Passed, CheckStatus.Passed, now, createdById: adminId);
            var riskyArtifact = Artifact.Create(project.Id, "1.9.0-wip", new string('b', 40), "feature/x", "ghcr.io/datasc/circuit:wip",
                CheckStatus.Passed, CheckStatus.Failed, now, createdById: adminId);
            db.AddRange(goodArtifact, riskyArtifact);
            await db.SaveChangesAsync(Token);
            return (goodArtifact.Id, riskyArtifact.Id);
        });
        var auditBefore = await host.WithDbAsync(db => db.AuditEvents.CountAsync(Token));

        var deny = await host.SimulateAsync(developer, risky, production);
        Assert.Equal("Deny", deny.GetProperty("decision").GetString());
        Assert.Equal("Build and tests must both be Passed.", deny.GetProperty("message").GetString());
        var failed = deny.GetProperty("results").EnumerateArray()
            .Where(result => !result.GetProperty("passed").GetBoolean())
            .Select(result => result.GetProperty("type").GetString()).ToArray();
        Assert.Contains("RequireTestsPassed", failed);
        Assert.Contains("AllowedBranches", failed);

        var approval = await host.SimulateAsync(developer, good, production);
        Assert.Equal("RequireApproval", approval.GetProperty("decision").GetString());
        Assert.Equal(2, approval.GetProperty("approvalsRequired").GetInt32());

        // Production-only and other-project policies do not apply to this project's Staging environment.
        var allow = await host.SimulateAsync(developer, risky, staging);
        Assert.Equal("Allow", allow.GetProperty("decision").GetString());
        Assert.Empty(allow.GetProperty("results").EnumerateArray());

        var other = await host.CreateProjectAsync(admin, "other-service");
        using var mismatched = await host.SendAsync(HttpMethod.Post, "/api/v1/policies/simulate", developer,
            new SimulatePolicyRequest(good, other.Environments[2].Id));
        Assert.Equal(HttpStatusCode.NotFound, mismatched.StatusCode);
        using var unknown = await host.SendAsync(HttpMethod.Post, "/api/v1/policies/simulate", developer,
            new SimulatePolicyRequest(Guid.NewGuid(), production));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var anonymous = await host.SendAsync(HttpMethod.Post, "/api/v1/policies/simulate", null,
            new SimulatePolicyRequest(good, production));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await host.WithDbAsync(async db => {
            Assert.Empty(await db.Deployments.ToListAsync(Token));
            Assert.Equal(auditBefore + 1, await db.AuditEvents.CountAsync(Token)); // only other-service's project.created
        });
    }

    [Fact]
    public async Task SimulationEvaluatesTheDeploymentWindowAtTheRequestedInstant() {
        await using var host = await OrgHost.StartAsync();
        var (adminId, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (_, developer) = await host.CreateUserAsync("dev@example.test", "Developer");
        var project = await host.CreateProjectAsync(admin);
        var production = project.Environments.Single(env => env.Tier == "Production").Id;
        var artifact = await host.WithDbAsync(async db => {
            var now = DateTimeOffset.UtcNow;
            // Journey D's blackout: deployments allowed Mon 08:00 → Fri 18:00 Asia/Kuala_Lumpur only.
            var policy = Policy.Create("Production baseline", PolicyScope.Global, [EnvironmentTier.Production], adminId, now);
            var item = Artifact.Create(project.Id, "1.8.2", new string('a', 40), "main", "ghcr.io/datasc/circuit:1.8.2",
                CheckStatus.Passed, CheckStatus.Passed, now, createdById: adminId);
            db.AddRange(policy, item, PolicyRule.Create(policy.Id, PolicyRuleType.DeploymentWindow, RuleEffect.Deny, 0,
                """{"timezone":"Asia/Kuala_Lumpur","allowed":[{"days":["Mon"],"from":"08:00","to":"23:59:59.9999999"},{"days":["Tue","Wed","Thu"],"from":"00:00","to":"23:59:59.9999999"},{"days":["Fri"],"from":"00:00","to":"17:59:59.9999999"}]}"""));
            await db.SaveChangesAsync(Token);
            return item.Id;
        });

        var fridayNight = new DateTimeOffset(2026, 11, 13, 20, 0, 0, TimeSpan.FromHours(8));
        var denied = await host.SimulateAsync(developer, artifact, production, fridayNight);
        Assert.Equal("Deny", denied.GetProperty("decision").GetString());
        Assert.Equal(fridayNight, denied.GetProperty("evaluatedAt").GetDateTimeOffset());
        Assert.Contains(denied.GetProperty("results").EnumerateArray(), result =>
            result.GetProperty("type").GetString() == "DeploymentWindow" && !result.GetProperty("passed").GetBoolean());

        var wednesdayMorning = new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal("Allow", (await host.SimulateAsync(developer, artifact, production, wednesdayMorning)).GetProperty("decision").GetString());
        var mondayEarly = new DateTimeOffset(2026, 11, 16, 7, 59, 0, TimeSpan.FromHours(8));
        Assert.Equal("Deny", (await host.SimulateAsync(developer, artifact, production, mondayEarly)).GetProperty("decision").GetString());
    }

    [Fact]
    public async Task PolicySaveRejectsInvalidBranchGlobsWithProblemDetailsAndAcceptsValidOnes() {
        await using var host = await OrgHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");

        static CreatePolicyRequest Request(string parameters) => new("Branches", null, "Global", null, ["Production"],
            [new CreatePolicyRuleRequest("AllowedBranches", "Deny", 0, JsonDocument.Parse(parameters).RootElement.Clone())]);

        foreach (var (parameters, message) in new[] {
            ("""{"patterns":[]}""", "patterns"),
            ("""{}""", "patterns"),
            ("""{"patterns":"main"}""", "patterns"),
            ("""{"patterns":[""]}""", "must not be empty"),
            ("""{"patterns":["   "]}""", "must not be empty"),
            ($$"""{"patterns":["{{new string('a', 256)}}"]}""", "at most 255"),
            ("""{"patterns":["main\u0000"]}""", "control characters"),
            ($$"""{"patterns":[{{string.Join(",", Enumerable.Repeat("\"main\"", 51))}}]}""", "at most 50"),
        }) {
            using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/policies", admin, Request(parameters));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
            Assert.Contains(message, problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        }
        await host.WithDbAsync(async db => Assert.Equal(0, await db.Policies.CountAsync(Token)));

        using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/policies", admin,
            Request("""{"patterns":["main","release/*","hotfix/*-urgent","(a+)+$"]}"""));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var policy = await created.Content.ReadFromJsonAsync<PolicyResponse>(Token);

        // Replacing the rules is validated the same way, and a rejected replace keeps the stored rules.
        using var replaced = await host.SendAsync(HttpMethod.Put, $"/api/v1/policies/{policy!.Id}", admin, Request("""{"patterns":[""]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, replaced.StatusCode);
        await host.WithDbAsync(async db => Assert.Contains("release/*",
            (await db.PolicyRules.AsNoTracking().SingleAsync(rule => rule.PolicyId == policy.Id, Token)).Parameters, StringComparison.Ordinal));
    }

    private sealed class OrgHost : IAsyncDisposable {
        private static readonly JwtOptions Jwt = new() {
            Issuer = "aegisops-tests",
            Audience = "aegisops-tests",
            SigningKey = "project-policy-tests-signing-key-0123456789",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7,
        };

        private readonly WebApplication _app;
        private readonly SqliteConnection _connection;
        private readonly HttpClient _client;
        private Guid _teamId;

        private OrgHost(WebApplication app, SqliteConnection connection) {
            _app = app;
            _connection = connection;
            _client = app.GetTestClient();
        }

        public static async Task<OrgHost> StartAsync() {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(Token);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Jwt:Issuer"] = Jwt.Issuer,
                ["Jwt:Audience"] = Jwt.Audience,
                ["Jwt:SigningKey"] = Jwt.SigningKey,
                ["Jwt:AccessTokenMinutes"] = Jwt.AccessTokenMinutes.ToString(CultureInfo.InvariantCulture),
            });
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection)
                .UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteModelCustomizer>());
            builder.Services.AddIdentityCore<User>(IdentityStoreExtensions.ConfigureIdentity).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AegisOpsDbContext>();
            builder.Services.AddScoped<IAuditWriter, EfAuditWriter>();
            builder.Services.AddJwtAuthentication(builder.Configuration);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            // Typed lambdas avoid an ASP.NET route-handler analyzer crash on method groups in this test project.
            app.MapPost("/api/v1/projects", (CreateProjectRequest body, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                CreateProject.Handle(body, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.ProjectsWrite);
            app.MapGet("/api/v1/projects/{slug}", (string slug, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, HttpContext http) => GetProject.Handle(slug, user, users, db, http))
                .RequireAuthorization(Permissions.ProjectsRead);
            app.MapPut("/api/v1/projects/{slug}/repository", (string slug, UpsertRepositoryRequest body, ClaimsPrincipal user,
                UserManager<User> users, AegisOpsDbContext db, IAuditWriter audit, HttpContext http) =>
                UpsertRepository.Handle(slug, body, user, users, db, audit, http))
                .RequireAuthorization(Permissions.ProjectsWrite);
            app.MapPatch("/api/v1/projects/{slug}/environments/{envId:guid}", (string slug, Guid envId, UpdateEnvironmentRequest body,
                ClaimsPrincipal user, UserManager<User> users, AegisOpsDbContext db, IAuditWriter audit, HttpContext http) =>
                UpdateEnvironment.Handle(slug, envId, body, user, users, db, audit, http))
                .RequireAuthorization(Permissions.EnvironmentsWrite);
            app.MapPost("/api/v1/policies", (CreatePolicyRequest body, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                CreatePolicy.Handle(body, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.PoliciesWrite);
            app.MapPut("/api/v1/policies/{id:guid}", (Guid id, CreatePolicyRequest body, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                PolicyPhase1Endpoints.Replace(id, body, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.PoliciesWrite);
            app.MapGet("/api/v1/policies/rule-types", () => PolicyPhase1Endpoints.RuleTypes())
                .RequireAuthorization(Permissions.PoliciesRead);
            app.MapPost("/api/v1/policies/simulate", (SimulatePolicyRequest body, AegisOpsDbContext db, TimeProvider time,
                HttpContext http) => PolicyPhase1Endpoints.Simulate(body, db, time, http))
                .RequireAuthorization(Permissions.PoliciesRead);
            await app.StartAsync(Token);

            var host = new OrgHost(app, connection);
            await host.WithDbAsync(db => db.Database.EnsureCreatedAsync(Token));
            return host;
        }

        public async Task<(Guid Id, string Bearer)> CreateUserAsync(string email, string role) {
            await using var scope = _app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync(role))
                Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() })).Succeeded);
            var user = User.Create(email, email.Split('@')[0], DateTimeOffset.UtcNow);
            Assert.True((await users.CreateAsync(user)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            return (user.Id, JwtAccessTokenFactory.Create(user, [role], Jwt, DateTimeOffset.UtcNow));
        }

        public async Task<Guid> CreateTeamAsync() {
            if (_teamId != Guid.Empty) return _teamId;
            var team = Team.Create("Platform", "platform", DateTimeOffset.UtcNow);
            await WithDbAsync(async db => {
                db.Teams.Add(team);
                await db.SaveChangesAsync(Token);
            });
            return _teamId = team.Id;
        }

        public async Task<ProjectResponse> CreateProjectAsync(string bearer, string slug = "circuit-service") {
            var teamId = await CreateTeamAsync();
            using var response = await SendAsync(HttpMethod.Post, "/api/v1/projects", bearer, new CreateProjectRequest(teamId, slug, slug, null));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ProjectResponse>(Token))!;
        }

        public async Task<JsonElement> SimulateAsync(string bearer, Guid artifactId, Guid environmentId, DateTimeOffset? at = null) {
            using var response = await SendAsync(HttpMethod.Post, "/api/v1/policies/simulate", bearer,
                new SimulatePolicyRequest(artifactId, environmentId, at));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            return json.RootElement.Clone();
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer, object? body = null) {
            var request = new HttpRequestMessage(method, path);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
            return _client.SendAsync(request, Token);
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
            _client.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    /// <summary>
    /// SQLite has no text[] columns. Unlike other test fixtures (which ignore it), persist policy tiers
    /// as comma-separated text so the simulation's tier filtering is genuinely exercised.
    /// </summary>
    private sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<Policy>().Property(policy => policy.AppliesToTiers)
                .HasConversion(new ValueConverter<IReadOnlyList<EnvironmentTier>, string>(
                    tiers => string.Join(",", tiers),
                    text => text.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(tier => Enum.Parse<EnvironmentTier>(tier)).ToArray()))
                .HasColumnType("TEXT");
            modelBuilder.Entity<Artifact>().Property(item => item.CreatedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
        }
    }
}
