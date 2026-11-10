using System.Security.Claims;
using AegisOps.Api.Deploy;
using AegisOps.Application.Audit;
using AegisOps.Application.Idempotency;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Health;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OrgEnvironment = AegisOps.Domain.Organization.Environment;

namespace AegisOps.Api.Tests;

/// <summary>
/// Audit check P1-22: the whole control-plane deployment loop against a real, migrated PostgreSQL database.
/// The API side runs the production endpoint handlers (request, approve) with real Identity users and roles;
/// the Worker side is the production <see cref="DeploymentJobService"/> polling loop, which claims jobs with
/// <c>FOR UPDATE SKIP LOCKED</c>, evaluates policy, re-checks it before executing, and completes jobs.
/// Noop targets keep Docker out of the loop (the Docker steps have their own streaming tests).
/// Set AEGISOPS_TEST_POSTGRES to an administrative connection string; each run uses its own database.
/// </summary>
[Collection("WorkerMetrics")]
public sealed class DeploymentLoopIntegrationTests {
    private static readonly TimeSpan LoopTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Staging_allow_production_two_approvals_and_a_failed_test_deny_complete_through_the_worker() {
        var admin = System.Environment.GetEnvironmentVariable("AEGISOPS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(admin)) Assert.Skip("Set AEGISOPS_TEST_POSTGRES to run the PostgreSQL deployment loop test.");
        var ct = TestContext.Current.CancellationToken;
        var name = "aegisops_loop_" + Guid.NewGuid().ToString("N");
        await ExecuteAdminAsync(admin, $"CREATE DATABASE {name}", ct);
        try {
            var target = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
            await using var services = BuildServices(target);
            var seed = await SeedAsync(services, ct);

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var worker = new DeploymentJobService(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
                new WorkerPollHeartbeat(TimeProvider.System));
            await worker.StartAsync(stop.Token);
            try {
                // Journey A, Staging: Allow → executed by the Worker without any human step.
                var staging = await RequestAsync(services, seed.Developer, seed.Artifact.Id, seed.Staging.Id, ct);
                await WaitForStatusAsync(services, staging, DeploymentStatus.Succeeded, ct);

                // Journey A, Production: the baseline policy needs two approvals from people other than the requester.
                var production = await RequestAsync(services, seed.Developer, seed.Artifact.Id, seed.Production.Id, ct);
                await WaitForStatusAsync(services, production, DeploymentStatus.AwaitingApproval, ct);
                Assert.Equal(StatusCodes.Status403Forbidden, await DecideAsync(services, seed.Developer, production, ct));
                Assert.Equal(StatusCodes.Status204NoContent, await DecideAsync(services, seed.Security, production, ct));
                // One approval re-evaluates to AwaitingApproval again; the Worker never executes early.
                await WaitForStatusAsync(services, production, DeploymentStatus.AwaitingApproval, ct, minimumEvaluations: 2);
                Assert.Equal(StatusCodes.Status204NoContent, await DecideAsync(services, seed.Approver, production, ct));
                await WaitForStatusAsync(services, production, DeploymentStatus.Succeeded, ct);

                // Journey B shape: a failed test run is denied by policy and never reaches execution.
                var denied = await RequestAsync(services, seed.Developer, seed.FailedArtifact.Id, seed.Production.Id, ct);
                await WaitForStatusAsync(services, denied, DeploymentStatus.Denied, ct);

                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
                var environments = await db.Environments.AsNoTracking().Where(item => item.ProjectId == seed.ProjectId).ToListAsync(ct);
                Assert.All(environments, environment => Assert.Equal(seed.Artifact.Id, environment.CurrentArtifactId));

                Assert.Equal([DeploymentEventType.Requested, DeploymentEventType.PolicyEvaluated, DeploymentEventType.ApprovalRequested,
                        DeploymentEventType.ApprovalReceived, DeploymentEventType.ApprovalRequested, DeploymentEventType.ApprovalReceived,
                        DeploymentEventType.Approved, DeploymentEventType.DeployStarted, DeploymentEventType.Succeeded],
                    await TimelineAsync(db, production.Id, ct));
                Assert.Equal([DeploymentEventType.Requested, DeploymentEventType.PolicyEvaluated, DeploymentEventType.Denied],
                    await TimelineAsync(db, denied.Id, ct));

                // Every job the loop created finished; nothing is left pending, failed or dead-lettered.
                var jobs = await db.Jobs.AsNoTracking().Where(item => item.Type == JobType.EvaluateDeployment || item.Type == JobType.ExecuteDeployment)
                    .ToListAsync(ct);
                Assert.All(jobs, job => Assert.Equal(JobStatus.Succeeded, job.Status));
                Assert.Equal(2, jobs.Count(job => job.Type == JobType.ExecuteDeployment));

                // The audit trail of the Production request carries one correlation ID end to end (docs/01 §7 Journey A step 6).
                var audit = await db.AuditEvents.AsNoTracking().Where(item => item.ResourceId == production.Id)
                    .OrderBy(item => item.Timestamp).ToListAsync(ct);
                Assert.All(audit, item => Assert.Equal(production.CorrelationId, item.CorrelationId));
                var actions = audit.Select(item => item.Action).ToArray();
                Assert.Contains("deployment.requested", actions);
                Assert.Equal(2, actions.Count(action => action == "deployment.approved"));
                Assert.Contains("deployment.evaluated", actions);
                Assert.Contains("deployment.succeeded", actions);
                Assert.Equal("deployment.succeeded", actions[^1]);
                Assert.Contains(audit, item => item.Action == "deployment.decision_denied" && item.ActorId == seed.Developer.Id);
            } finally {
                await stop.CancelAsync();
                await worker.StopAsync(CancellationToken.None);
            }
        } finally {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(admin, $"DROP DATABASE {name} WITH (FORCE)", CancellationToken.None);
        }
    }

    private static ServiceProvider BuildServices(string connectionString) {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddDbContext<AegisOpsDbContext>(options => options.UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention().EnableServiceProviderCaching(false));
        collection.AddScoped<IAuditWriter, EfAuditWriter>();
        collection.AddIdentityCore<User>().AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AegisOpsDbContext>();
        return collection.BuildServiceProvider();
    }

    private sealed record Seed(Guid ProjectId, OrgEnvironment Staging, OrgEnvironment Production, Artifact Artifact,
        Artifact FailedArtifact, User Developer, User Security, User Approver);

    private static async Task<Seed> SeedAsync(IServiceProvider services, CancellationToken ct) {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
        await db.Database.MigrateAsync(ct);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var now = DateTimeOffset.UtcNow;
        async Task<User> AddUserAsync(string role) {
            var user = User.Create($"{role.ToLowerInvariant()}@loop.test", role, now);
            Assert.True((await users.CreateAsync(user)).Succeeded);
            if (!await db.Roles.AnyAsync(item => item.Name == role, ct)) {
                db.Roles.Add(new IdentityRole<Guid>(role) { Id = Guid.NewGuid(), NormalizedName = role.ToUpperInvariant() });
                await db.SaveChangesAsync(ct);
            }
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            return user;
        }
        var admin = await AddUserAsync("Admin");
        var developer = await AddUserAsync("Developer");
        var security = await AddUserAsync("Security");
        var approver = await AddUserAsync("Approver");

        var team = Team.Create("Loop", "loop", now);
        var project = Project.Create(team.Id, "circuit-service", "circuit-service", now);
        var staging = OrgEnvironment.Create(project.Id, "Staging", EnvironmentTier.Staging, 1, DeploymentTarget.Noop());
        var production = OrgEnvironment.Create(project.Id, "Production", EnvironmentTier.Production, 2, DeploymentTarget.Noop());
        var artifact = Artifact.Create(project.Id, "1.0.0", new string('a', 40), "main", "ghcr.io/example/circuit-service:1.0.0",
            CheckStatus.Passed, CheckStatus.Passed, now, createdById: developer.Id);
        var failed = Artifact.Create(project.Id, "1.0.1", new string('b', 40), "main", "ghcr.io/example/circuit-service:1.0.1",
            CheckStatus.Passed, CheckStatus.Failed, now, createdById: developer.Id);
        db.AddRange(team, project, staging, production, artifact, failed);
        db.AddRange(new[] { developer, security, approver }.Select(user => TeamMember.Create(team.Id, user.Id, TeamRole.Member, now)));

        // The seeded "Production baseline" shape (DevelopmentUserSeed): tests must pass, and two approvals.
        var policy = Policy.Create("Production baseline", PolicyScope.Global, [EnvironmentTier.Production], admin.Id, now,
            description: "Tests must pass, and production needs two approvals.");
        db.Policies.Add(policy);
        db.PolicyRules.Add(PolicyRule.Create(policy.Id, PolicyRuleType.RequireTestsPassed, RuleEffect.Deny, 0, "{}"));
        db.PolicyRules.Add(PolicyRule.Create(policy.Id, PolicyRuleType.RequireApprovals, RuleEffect.RequireApproval, 1, """{"count":2}"""));
        await db.SaveChangesAsync(ct);
        return new Seed(project.Id, staging, production, artifact, failed, developer, security, approver);
    }

    private static ClaimsPrincipal Principal(User user) => new(new ClaimsIdentity([new Claim("sub", user.Id.ToString())], "test"));

    private static DefaultHttpContext Http(IServiceProvider services) {
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("N");
        return http;
    }

    private static async Task<Deployment> RequestAsync(IServiceProvider root, User actor, Guid artifactId, Guid environmentId, CancellationToken ct) {
        await using var scope = root.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AegisOpsDbContext>();
        var result = await DeploymentEndpoints.Request(new RequestDeploymentRequest(artifactId, environmentId, null, "loop test"),
            Principal(actor), services.GetRequiredService<UserManager<User>>(), db, services.GetRequiredService<IAuditWriter>(),
            TimeProvider.System, new NoReplay(), Http(services));
        Assert.Equal(StatusCodes.Status202Accepted, ((IStatusCodeHttpResult)result).StatusCode);
        return await db.Deployments.AsNoTracking().Where(item => item.EnvironmentId == environmentId && item.ArtifactId == artifactId)
            .OrderByDescending(item => item.RequestedAt).FirstAsync(ct);
    }

    private static async Task<int> DecideAsync(IServiceProvider root, User actor, Deployment deployment, CancellationToken ct) {
        await using var scope = root.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var result = await DeploymentEndpoints.Decide(deployment.Id, new DeploymentDecisionRequest("Approved", "looks good"),
            Principal(actor), services.GetRequiredService<UserManager<User>>(), services.GetRequiredService<AegisOpsDbContext>(),
            services.GetRequiredService<IAuditWriter>(), TimeProvider.System, Http(services));
        return ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;
    }

    private static async Task WaitForStatusAsync(IServiceProvider root, Deployment deployment, DeploymentStatus expected,
        CancellationToken ct, int minimumEvaluations = 1) {
        var deadline = DateTimeOffset.UtcNow + LoopTimeout;
        DeploymentStatus? last = null;
        while (DateTimeOffset.UtcNow < deadline) {
            await using (var scope = root.CreateAsyncScope()) {
                var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
                last = await db.Deployments.AsNoTracking().Where(item => item.Id == deployment.Id).Select(item => item.Status).SingleAsync(ct);
                var evaluations = await db.PolicyEvaluations.CountAsync(item => item.DeploymentId == deployment.Id, ct);
                var pending = await db.Jobs.AnyAsync(item => item.CorrelationId == deployment.CorrelationId &&
                    (item.Status == JobStatus.Pending || item.Status == JobStatus.Running), ct);
                if (last == expected && evaluations >= minimumEvaluations && !pending) return;
            }
            await Task.Delay(100, ct);
        }
        Assert.Fail($"Deployment stayed {last} instead of reaching {expected} within {LoopTimeout.TotalSeconds:0} s.");
    }

    private static async Task<DeploymentEventType[]> TimelineAsync(AegisOpsDbContext db, Guid deploymentId, CancellationToken ct) =>
        await db.DeploymentEvents.AsNoTracking().Where(item => item.DeploymentId == deploymentId)
            .OrderBy(item => item.Sequence).Select(item => item.Type).ToArrayAsync(ct);

    private static async Task ExecuteAdminAsync(string connectionString, string sql, CancellationToken ct) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class NoReplay : IIdempotencyStore {
        public Task<StoredResponse?> FindAsync(string principal, string key, CancellationToken cancellationToken) =>
            Task.FromResult<StoredResponse?>(null);
        public Task<string?> TryBeginAsync(string principal, string key, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(Guid.NewGuid().ToString("N"));
        public Task<bool> SaveAsync(string principal, string key, string owner, StoredResponse response, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
