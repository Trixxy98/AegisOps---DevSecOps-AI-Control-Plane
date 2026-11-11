using System.Text.Json;
using AegisOps.Api.Identity;
using AegisOps.Api.Organization;
using AegisOps.Application.PolicyEngine;
using AegisOps.Domain.Ai;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using DomainEnvironment = AegisOps.Domain.Organization.Environment;

namespace AegisOps.Api.Tests;

/// <summary>
/// Runs the real <see cref="DevelopmentUserSeed"/> against SQLite with the production Identity
/// configuration (roadmap checks P1-04 roles/demo users and P3-09 seeded scan gates).
/// </summary>
public sealed class DevelopmentSeedTests : IAsyncLifetime {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly (string Email, string[] Roles)[] DemoUsers = [
        ("admin@aegisops.local", ["Admin"]),
        ("developer@aegisops.local", ["Developer"]),
        ("rith@aegisops.local", ["Developer"]),
        ("security@aegisops.local", ["Approver", "Security"]),
        ("approver@aegisops.local", ["Approver"]),
    ];

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync() {
        await _connection.OpenAsync(Token);
        var services = new ServiceCollection();
        services.AddLogging();
        // Use the production Identity registration (password rules, unique email, roles) and only swap
        // the Npgsql provider for SQLite. No connection is ever opened against the placeholder string.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:Postgres"] = "Host=unused.invalid;Database=unused",
        }).Build();
        services.AddIdentityStore(configuration);
        services.RemoveAll<IDbContextOptionsConfiguration<AegisOpsDbContext>>();
        services.RemoveAll<DbContextOptions<AegisOpsDbContext>>();
        services.RemoveAll<DbContextOptions>();
        services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(_connection)
            .UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteModelCustomizer>());
        _services = services.BuildServiceProvider(validateScopes: true);
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>().Database.EnsureCreatedAsync(Token);
    }

    public async ValueTask DisposeAsync() {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SeedCreatesRolesAndDemoUsersWithTheirRoles() {
        await DevelopmentUserSeed.SeedAsync(_services);

        await using var scope = _services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.Equal(["Admin", "Approver", "Developer", "Security"],
            (await roles.Roles.Select(role => role.Name!).ToListAsync(Token)).Order(StringComparer.Ordinal));
        foreach (var role in new[] { "Admin", "Developer", "Security", "Approver" })
            Assert.True(await roles.RoleExistsAsync(role), role);

        Assert.Equal(DemoUsers.Length, await users.Users.CountAsync(Token));
        foreach (var (email, expectedRoles) in DemoUsers) {
            var user = await users.FindByEmailAsync(email);
            Assert.NotNull(user);
            Assert.True(user.IsActive, email);
            Assert.False(string.IsNullOrEmpty(user.PasswordHash), $"{email} must have a password");
            Assert.Equal(expectedRoles, (await users.GetRolesAsync(user)).Order(StringComparer.Ordinal));
        }

        await using var dbScope = _services.CreateAsyncScope();
        var db = dbScope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
        var team = await db.Teams.AsNoTracking().SingleAsync(item => item.Slug == "network-platform", Token);
        var owners = await db.TeamMembers.AsNoTracking().Where(member => member.TeamId == team.Id)
            .Join(db.Users, member => member.UserId, user => user.Id, (member, user) => new { user.Email, member.Role })
            .ToListAsync(Token);
        Assert.Equal(["approver@aegisops.local:Member", "developer@aegisops.local:Owner", "rith@aegisops.local:Owner", "security@aegisops.local:Member"],
            owners.Select(item => $"{item.Email}:{item.Role}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SeedCreatesTheThreeDemoProjectsWithEnvironmentsAndStagingDockerTargets() {
        await DevelopmentUserSeed.SeedAsync(_services);
        await DevelopmentUserSeed.SeedAsync(_services);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
        var team = await db.Teams.AsNoTracking().SingleAsync(item => item.Slug == "network-platform", Token);
        var projects = await db.Projects.AsNoTracking().OrderBy(item => item.Slug).ToListAsync(Token);
        Assert.Equal(["circuit-service", "notification-service", "site-service"], projects.Select(item => item.Slug));
        Assert.All(projects, project => Assert.Equal(team.Id, project.TeamId));
        var environments = await db.Environments.AsNoTracking().ToListAsync(Token);
        Assert.Equal(9, environments.Count);
        foreach (var project in projects) {
            Assert.Equal(["Development", "Staging", "Production"], environments.Where(item => item.ProjectId == project.Id)
                .OrderBy(item => item.Order).Select(item => item.Name));
        }
        string TargetOf(string slug) => environments.Single(item =>
            item.ProjectId == projects.Single(project => project.Slug == slug).Id && item.Tier == EnvironmentTier.Staging).Target.Type;
        Assert.Equal("docker", TargetOf("circuit-service"));
        Assert.Equal("docker", TargetOf("site-service"));
        Assert.Equal("noop", TargetOf("notification-service"));
    }

    [Fact]
    public async Task RunningTheSeedTwiceIsIdempotent() {
        await DevelopmentUserSeed.SeedAsync(_services);
        var first = await SnapshotAsync();

        await DevelopmentUserSeed.SeedAsync(_services);
        var second = await SnapshotAsync();

        Assert.Equal(first, second);
        Assert.Equal(4, second.Roles);
        Assert.Equal(DemoUsers.Length, second.Users);
        Assert.Equal(6, second.UserRoles); // security@ has two roles
        Assert.Equal(1, second.Teams);
        Assert.Equal(4, second.Members); // two Owners, plus security@ and approver@ as approving Members
        Assert.Equal(3, second.Policies); // Production baseline, Global baseline, Staging gate
        Assert.Equal(6, second.Rules);
        Assert.Equal((1, 1), (second.AiModels, second.AiPolicies));
    }

    [Fact]
    public async Task SeedPersistsGlobalCriticalPolicyAndStagingScanGate() {
        await DevelopmentUserSeed.SeedAsync(_services);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
        var adminId = (await db.Users.AsNoTracking().SingleAsync(user => user.Email == DevelopmentUserSeed.Email, Token)).Id;
        var policies = await db.Policies.AsNoTracking().ToListAsync(Token);
        var rules = await db.PolicyRules.AsNoTracking().ToListAsync(Token);

        var baseline = Assert.Single(policies, policy => policy.Name == "Global baseline");
        Assert.Equal((PolicyScope.Global, (Guid?)null, true, (DateTimeOffset?)null, adminId),
            (baseline.Scope, baseline.ScopeId, baseline.IsEnabled, baseline.ArchivedAt, baseline.CreatedById));
        Assert.Equal([EnvironmentTier.Development, EnvironmentTier.Staging, EnvironmentTier.Production], baseline.AppliesToTiers);
        var baselineRules = rules.Where(rule => rule.PolicyId == baseline.Id).OrderBy(rule => rule.Order).ToArray();
        Assert.Equal(2, baselineRules.Length);
        Assert.Equal((PolicyRuleType.RequireTestsPassed, RuleEffect.Deny, true), (baselineRules[0].Type, baselineRules[0].Effect, baselineRules[0].IsEnabled));
        Assert.Equal((PolicyRuleType.MaxFindings, RuleEffect.Deny, true), (baselineRules[1].Type, baselineRules[1].Effect, baselineRules[1].IsEnabled));
        AssertJson("""{"severity":"Critical","max":0}""", baselineRules[1].Parameters);

        var staging = Assert.Single(policies, policy => policy.Name == "Staging gate");
        Assert.Equal((PolicyScope.Global, true, (DateTimeOffset?)null), (staging.Scope, staging.IsEnabled, staging.ArchivedAt));
        Assert.Equal([EnvironmentTier.Staging], staging.AppliesToTiers);
        var stagingRules = rules.Where(rule => rule.PolicyId == staging.Id).OrderBy(rule => rule.Order).ToArray();
        Assert.Equal(2, stagingRules.Length);
        Assert.Equal((PolicyRuleType.RequireScan, RuleEffect.Deny), (stagingRules[0].Type, stagingRules[0].Effect));
        AssertJson("""{"scanners":["Gitleaks","Trivy","Semgrep"],"maxAgeHours":168}""", stagingRules[0].Parameters);
        Assert.Equal((PolicyRuleType.MaxFindings, RuleEffect.Warn), (stagingRules[1].Type, stagingRules[1].Effect));
        AssertJson("""{"severity":"High","max":10}""", stagingRules[1].Parameters);

        var production = Assert.Single(policies, policy => policy.Name == "Production baseline");
        Assert.Equal([EnvironmentTier.Production], production.AppliesToTiers);
        var productionRules = rules.Where(rule => rule.PolicyId == production.Id).OrderBy(rule => rule.Order).ToArray();
        Assert.Equal([PolicyRuleType.RequireTestsPassed, PolicyRuleType.RequireApprovals], productionRules.Select(rule => rule.Type));
        Assert.Equal(RuleEffect.RequireApproval, productionRules[1].Effect);
        AssertJson("""{"count":2}""", productionRules[1].Parameters);

        var aiPolicy = await db.AiPolicies.AsNoTracking().SingleAsync(Token);
        Assert.Equal((PolicyScope.Global, true), (aiPolicy.Scope, aiPolicy.IsEnabled));
        Assert.True((await db.AiModels.AsNoTracking().SingleAsync(Token)).IsEnabled);
    }

    [Fact]
    public async Task SeededPoliciesDenyCriticalFindingsInEveryTierAndGateStagingOnScans() {
        await DevelopmentUserSeed.SeedAsync(_services);
        var policies = await LoadApplicablePoliciesAsync();
        var now = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        ScanEvidence Scan(string scanner, int critical = 0, int high = 0, double ageHours = 1, string status = "Completed") =>
            new(scanner, status, now.AddHours(-ageHours), critical, high, 0, 0, 0, 0);
        ScanEvidence[] Clean() => [Scan("Gitleaks"), Scan("Trivy"), Scan("Semgrep")];
        EvaluationResult Evaluate(EnvironmentTier tier, IReadOnlyList<ScanEvidence> scans, int approvals = 0) =>
            PolicyEngine.Evaluate(new PolicyContext("main", CheckStatus.Passed, CheckStatus.Passed, "sha256:abc", tier,
                projectId, teamId, null, approvals, now, scans), policies);

        // A single critical finding is denied in Development, Staging and Production by the global baseline.
        foreach (var tier in new[] { EnvironmentTier.Development, EnvironmentTier.Staging, EnvironmentTier.Production }) {
            var critical = Evaluate(tier, [Scan("Gitleaks"), Scan("Trivy", critical: 1), Scan("Semgrep")], approvals: 2);
            Assert.Equal(PolicyDecision.Deny, critical.Decision);
            Assert.Equal("1 Critical finding(s) exceed limit 0.", critical.Message);
            var failed = Assert.Single(critical.Results, result => !result.Passed);
            Assert.Equal(("Global baseline", "MaxFindings", "Deny"), (failed.PolicyName, failed.Type, failed.Effect));
        }

        // Staging: clean, fresh scans from all three scanners are allowed.
        var stagingClean = Evaluate(EnvironmentTier.Staging, Clean());
        Assert.Equal(PolicyDecision.Allow, stagingClean.Decision);
        Assert.All(stagingClean.Results, result => Assert.True(result.Passed, result.Message));
        Assert.Equal(["Global baseline", "Staging gate"], stagingClean.Results.Select(result => result.PolicyName).Distinct().Order(StringComparer.Ordinal));

        // Staging: a missing, failed or stale scan is denied by the Staging scan gate.
        var missing = Evaluate(EnvironmentTier.Staging, [Scan("Gitleaks"), Scan("Semgrep")]);
        Assert.Equal((PolicyDecision.Deny, "Trivy scan is missing."), (missing.Decision, missing.Message));
        var failedScan = Evaluate(EnvironmentTier.Staging, [Scan("Gitleaks"), Scan("Trivy", status: "Failed"), Scan("Semgrep")]);
        Assert.Equal((PolicyDecision.Deny, "Trivy scan failed and is treated as missing."), (failedScan.Decision, failedScan.Message));
        var stale = Evaluate(EnvironmentTier.Staging, [Scan("Gitleaks", ageHours: 169), Scan("Trivy"), Scan("Semgrep")]);
        Assert.Equal((PolicyDecision.Deny, "Gitleaks scan is older than 168 hours."), (stale.Decision, stale.Message));

        // Staging: more than 10 high findings only warns.
        var manyHigh = Evaluate(EnvironmentTier.Staging, [Scan("Gitleaks"), Scan("Trivy", high: 11), Scan("Semgrep")]);
        Assert.Equal(PolicyDecision.Allow, manyHigh.Decision);
        var warning = Assert.Single(manyHigh.Results, result => !result.Passed);
        Assert.Equal(("Staging gate", "MaxFindings", "Warn"), (warning.PolicyName, warning.Type, warning.Effect));

        // Production: the scan gate does not apply, but two approvals are required for a clean artifact.
        var productionClean = Evaluate(EnvironmentTier.Production, []);
        Assert.Equal((PolicyDecision.RequireApproval, 2), (productionClean.Decision, productionClean.ApprovalsRequired));
        Assert.DoesNotContain(productionClean.Results, result => result.PolicyName == "Staging gate");
        Assert.Equal(PolicyDecision.Allow, Evaluate(EnvironmentTier.Production, [], approvals: 2).Decision);
    }

    [Fact]
    public async Task PolicySimulationDeniesAnArtifactWithACriticalFindingUnderSeededPolicies() {
        await DevelopmentUserSeed.SeedAsync(_services);
        var now = DateTimeOffset.UtcNow;
        Guid artifactId, stagingId, productionId;
        await using (var scope = _services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            var team = await db.Teams.SingleAsync(item => item.Slug == "network-platform", Token);
            var adminId = (await db.Users.SingleAsync(user => user.Email == DevelopmentUserSeed.Email, Token)).Id;
            var project = Project.Create(team.Id, "Simulation", "simulation-service", now); // the seed owns circuit-service
            var staging = DomainEnvironment.Create(project.Id, "Staging", EnvironmentTier.Staging, 1);
            var production = DomainEnvironment.Create(project.Id, "Production", EnvironmentTier.Production, 2);
            var artifact = Artifact.Create(project.Id, "1.0.0", new string('a', 40), "main", "ghcr.io/x/circuit:1.0.0",
                CheckStatus.Passed, CheckStatus.Passed, now, imageDigest: "sha256:" + new string('b', 64), createdById: adminId);
            db.AddRange(project, staging, production, artifact);
            foreach (var scanner in new[] { ScannerType.Gitleaks, ScannerType.Trivy, ScannerType.Semgrep }) {
                var scan = SecurityScan.Begin(artifact.Id, scanner, ScanSource.CiUploaded, ReportFormat.Sarif, now, null, "/r.sarif", true);
                var finding = SecurityFinding.Create(scan.Id, artifact.Id,
                    scanner == ScannerType.Trivy ? Severity.Critical : Severity.Low, "rule", "title", $"fp-{scanner}",
                    null, null, null, null, null, null, null, null, FindingStatus.Open, null, null);
                scan.Complete(ScanSummary.From([finding]), now);
                db.AddRange(scan, finding);
            }
            await db.SaveChangesAsync(Token);
            (artifactId, stagingId, productionId) = (artifact.Id, staging.Id, production.Id);
        }

        foreach (var environmentId in new[] { stagingId, productionId }) {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            var result = await PolicyPhase1Endpoints.Simulate(new SimulatePolicyRequest(artifactId, environmentId), db, TimeProvider.System, http);
            var body = JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            Assert.Equal("Deny", body.GetProperty("decision").ToString());
            Assert.Equal("1 Critical finding(s) exceed limit 0.", body.GetProperty("Message").GetString());
        }
    }

    private async Task<IReadOnlyList<ApplicablePolicy>> LoadApplicablePoliciesAsync() {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
        var policies = await db.Policies.AsNoTracking().Where(item => item.ArchivedAt == null && item.IsEnabled).ToListAsync(Token);
        var rules = await db.PolicyRules.AsNoTracking().Where(item => item.IsEnabled).ToListAsync(Token);
        return policies.Select(policy => new ApplicablePolicy(policy.Id, policy.Name, policy.Version, policy.Scope, policy.ScopeId,
            policy.AppliesToTiers, policy.IsEnabled,
            rules.Where(rule => rule.PolicyId == policy.Id)
                .Select(rule => new ApplicableRule(policy.Id, policy.Name, policy.Version, rule.Id, rule.Type, rule.Effect, rule.Parameters, rule.Order))
                .ToArray())).ToArray();
    }

    private sealed record Snapshot(int Roles, int Users, int UserRoles, int Teams, int Members, int Policies, int Rules, int AiModels, int AiPolicies,
        string UserIds, string PolicyIds);

    private async Task<Snapshot> SnapshotAsync() {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
        var userIds = string.Join(",", (await db.Users.AsNoTracking().Select(user => user.Id).ToListAsync(Token)).Order());
        var policyIds = string.Join(",", (await db.Policies.AsNoTracking().Select(policy => policy.Id).ToListAsync(Token)).Order());
        return new Snapshot(
            await db.Roles.CountAsync(Token),
            await db.Users.CountAsync(Token),
            await db.UserRoles.CountAsync(Token),
            await db.Teams.CountAsync(Token),
            await db.TeamMembers.CountAsync(Token),
            await db.Policies.CountAsync(Token),
            await db.PolicyRules.CountAsync(Token),
            await db.AiModels.CountAsync(Token),
            await db.AiPolicies.CountAsync(Token),
            userIds,
            policyIds);
    }

    private static void AssertJson(string expected, string actual) =>
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(expected).RootElement, JsonDocument.Parse(actual).RootElement),
            $"Expected {expected} but was {actual}");

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
        }
    }
}
