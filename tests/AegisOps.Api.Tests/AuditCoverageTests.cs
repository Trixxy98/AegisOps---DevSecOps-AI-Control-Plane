using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using AegisOps.Api.Ai;
using AegisOps.Api.Deploy;
using AegisOps.Api.Identity;
using AegisOps.Api.Jobs;
using AegisOps.Api.Organization;
using AegisOps.Application.Audit;
using AegisOps.Application.Authorization;
using AegisOps.Application.Idempotency;
using AegisOps.Domain.Ai;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Ai;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ProjectEnvironment = AegisOps.Domain.Organization.Environment;

namespace AegisOps.Api.Tests;

/// <summary>
/// Audit checks P1-19 ("audit coverage is tested for all Phase 1 actions") and P8-05 ("audit coverage test verifies
/// all security-sensitive actions").
/// <list type="bullet">
/// <item>A guard test parses Program.cs and fails when a mutating route is missing from <see cref="RouteTable"/>,
/// so a new route cannot skip audit coverage.</item>
/// <item><see cref="EventTable"/> lists the security-sensitive and Worker actions that are not a single route
/// (failed login, refresh reuse, denials, policy-engine and executor transitions).</item>
/// <item>Every table row names the behavioural test below that calls the real handler (with a principal built from a
/// real, validated JWT and real Identity roles) and asserts exactly one persisted AuditEvent with the expected action,
/// actor, resource and outcome. A row may carry a <c>Gap</c> (a known product gap); its test must then be skipped with
/// exactly that reason, and a guard test keeps the table and the skip reasons in sync. There are currently no gaps.</item>
/// </list>
/// </summary>
public sealed partial class AuditCoverageTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------- Expected audit table ----------

    /// <summary>One audited action. <paramref name="Test"/> is the test in this class that asserts it.</summary>
    private sealed record AuditExpectation(
        string Method,
        string Route,
        string? Action,
        string Test,
        string? Gap = null,
        string? NoAuditBecause = null
    );

    /// <summary>Every POST/PUT/PATCH/DELETE route in src/AegisOps.Api/Program.cs.</summary>
    private static readonly AuditExpectation[] RouteTable = [
        new("POST", "/api/v1/auth/login", "auth.login", nameof(LoginLogoutAndPasswordChangeAreAudited)),
        new("POST", "/api/v1/auth/refresh", "auth.refreshed", nameof(RefreshRotationIsAudited)),
        new("POST", "/api/v1/auth/logout", "auth.logout", nameof(LoginLogoutAndPasswordChangeAreAudited)),
        new("POST", "/api/v1/auth/change-password", "auth.password_changed", nameof(LoginLogoutAndPasswordChangeAreAudited)),
        new("POST", "/api/v1/ai/chat", "ai.request", nameof(AiChatIsAuditedWhenCompletedAndWhenBlocked)),
        new("PUT", "/api/v1/ai/policies", "ai.policy_updated", nameof(AiPolicySaveIsAudited)),
        new("POST", "/api/v1/admin/ai/models/sync", "ai.model_synced", nameof(AiModelSyncIsAudited)),
        new("POST", "/api/v1/teams", "team.created", nameof(TeamLifecycleAndMembershipChangesAreAudited)),
        new("PATCH", "/api/v1/teams/{slug}", "team.updated", nameof(TeamLifecycleAndMembershipChangesAreAudited)),
        new("PUT", "/api/v1/teams/{slug}/webhook", "team.webhook_updated", nameof(WebhookSettingsAndKeyRewrapAreAudited)),
        new("POST", "/api/v1/admin/webhooks/rewrap", "team.webhook_keys_rewrapped", nameof(WebhookSettingsAndKeyRewrapAreAudited)),
        new("PUT", "/api/v1/teams/{slug}/members/{userId:guid}", "team.member_upserted", nameof(TeamLifecycleAndMembershipChangesAreAudited)),
        new("DELETE", "/api/v1/teams/{slug}/members/{userId:guid}", "team.member_removed", nameof(TeamLifecycleAndMembershipChangesAreAudited)),
        new("POST", "/api/v1/projects", "project.created", nameof(ProjectEnvironmentRepositoryAndApiKeyChangesAreAudited)),
        new("PATCH", "/api/v1/projects/{slug}", "project.updated", nameof(ProjectEnvironmentRepositoryAndApiKeyChangesAreAudited)),
        new("PUT", "/api/v1/projects/{slug}/repository", "repository.upserted", nameof(ProjectEnvironmentRepositoryAndApiKeyChangesAreAudited)),
        new("PATCH", "/api/v1/projects/{slug}/environments/{envId:guid}", "environment.updated", nameof(ProjectEnvironmentRepositoryAndApiKeyChangesAreAudited)),
        new("POST", "/api/v1/projects/{slug}/api-keys", "apikey.created", nameof(ProjectEnvironmentRepositoryAndApiKeyChangesAreAudited)),
        new("DELETE", "/api/v1/projects/{slug}/api-keys/{keyId:guid}", "apikey.revoked", nameof(ProjectEnvironmentRepositoryAndApiKeyChangesAreAudited)),
        new("POST", "/api/v1/projects/{slug}/artifacts", "artifact.created", nameof(ArtifactScanAndFindingChangesAreAudited)),
        new("PATCH", "/api/v1/artifacts/{id:guid}", "artifact.digest_backfilled", nameof(ArtifactScanAndFindingChangesAreAudited)),
        new("POST", "/api/v1/artifacts/{id:guid}/scans", "scan.uploaded", nameof(ArtifactScanAndFindingChangesAreAudited)),
        new("POST", "/api/v1/findings/{id:guid}/ai-explain", "ai.request", nameof(AiFindingExplanationAndDeploymentSummaryAreAudited)),
        new("POST", "/api/v1/projects/{slug}/finding-suppressions", "finding.suppressed", nameof(ArtifactScanAndFindingChangesAreAudited)),
        new("POST", "/api/v1/policies", "policy.created", nameof(PolicyLifecycleIsAudited)),
        new("POST", "/api/v1/policies/simulate", null, nameof(PolicySimulationIsReadOnlyAndWritesNoAudit),
            NoAuditBecause: "Read-only dry run: evaluates policies in memory and persists nothing."),
        new("PATCH", "/api/v1/policies/{id:guid}", "policy.enabled|policy.disabled", nameof(PolicyLifecycleIsAudited)),
        new("DELETE", "/api/v1/policies/{id:guid}", "policy.archived", nameof(PolicyLifecycleIsAudited)),
        new("PUT", "/api/v1/policies/{id:guid}", "policy.updated", nameof(PolicyLifecycleIsAudited)),
        new("POST", "/api/v1/deployments", "deployment.requested", nameof(DeploymentRequestApprovalCancelAndRollbackAreAudited)),
        new("POST", "/api/v1/deployments/{id:guid}/ai-summary", "ai.request", nameof(AiFindingExplanationAndDeploymentSummaryAreAudited)),
        new("POST", "/api/v1/deployments/{id:guid}/approvals", "deployment.approved|deployment.rejected", nameof(DeploymentRequestApprovalCancelAndRollbackAreAudited)),
        new("POST", "/api/v1/deployments/{id:guid}/cancel", "deployment.cancelled", nameof(DeploymentRequestApprovalCancelAndRollbackAreAudited)),
        new("POST", "/api/v1/deployments/{id:guid}/rollback", "deployment.requested", nameof(DeploymentRequestApprovalCancelAndRollbackAreAudited)),
        new("POST", "/api/v1/admin/jobs/{id:guid}/retry", "job.retried", nameof(DeadLetteredJobRetryIsAudited)),
    ];

    /// <summary>Security-sensitive and Worker actions that are not one route (docs/09, docs/17 §6).</summary>
    private static readonly AuditExpectation[] EventTable = [
        new("EVENT", "login with wrong password", "auth.login_failed", nameof(FailedLoginIsAudited)),
        new("EVENT", "rotated refresh token replayed", "auth.refresh_reuse_detected", nameof(RefreshTokenReuseIsAudited)),
        new("EVENT", "invalid API key presented", "apikey.auth_failed", nameof(ApiKeyAuthenticationFailureIsAudited)),
        new("EVENT", "route permission denied (403)", "authorization.denied", nameof(RouteAuthorizationDenialIsAudited)),
        new("EVENT", "self-approval / approval without required role", "deployment.decision_denied", nameof(DeploymentRequestApprovalCancelAndRollbackAreAudited)),
        new("EVENT", "cancel by non-requester non-owner", "deployment.cancel_denied", nameof(DeploymentRequestApprovalCancelAndRollbackAreAudited)),
        new("EVENT", "Worker: policy evaluation allows", "deployment.evaluated", nameof(WorkerEvaluationAndNoopExecutionAreAudited)),
        new("EVENT", "Worker: policy evaluation denies", "deployment.evaluated", nameof(WorkerPolicyDenialIsAudited)),
        new("EVENT", "Worker: execution succeeds", "deployment.succeeded", nameof(WorkerEvaluationAndNoopExecutionAreAudited)),
        new("EVENT", "Worker: docker execution fails", "deployment.failed", nameof(WorkerDockerExecutionFailureIsAudited)),
        new("EVENT", "AI request blocked by governance", "ai.request", nameof(AiChatIsAuditedWhenCompletedAndWhenBlocked)),
        new("EVENT", "job retry attributed to the authenticated admin", "job.retried", nameof(DeadLetteredJobRetryIsAttributedToTheAdmin)),
        new("EVENT", "API-key actor recorded as ApiKey", "artifact.created", nameof(ApiKeyActionsAreAttributedToTheApiKeyActorType)),
    ];

    // ---------- Guard tests ----------

    [GeneratedRegex("""app\.Map(?<verb>Post|Put|Patch|Delete)\(\s*"(?<route>[^"]+)"\s*,""")]
    private static partial Regex MutatingRoute();

    [Fact]
    public void EveryMutatingRouteInProgramHasAnExpectedAuditAction() {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "AegisOps.Api", "Program.cs"));
        var routes = MutatingRoute().Matches(source)
            .Select(match => $"{match.Groups["verb"].Value.ToUpperInvariant()} {match.Groups["route"].Value}")
            .ToList();
        Assert.True(routes.Count >= 30, $"Expected the Program.cs route parser to find the mutating routes; found {routes.Count}.");
        Assert.Equal(routes.Count, routes.Distinct(StringComparer.Ordinal).Count());

        var table = RouteTable.Select(item => $"{item.Method} {item.Route}").ToList();
        Assert.Equal(table.Count, table.Distinct(StringComparer.Ordinal).Count());

        var missing = routes.Except(table, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "Mutating routes without an expected audit action in AuditCoverageTests.RouteTable: " + string.Join(", ", missing));
        var stale = table.Except(routes, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "RouteTable rows that no longer exist in Program.cs: " + string.Join(", ", stale));
    }

    [Fact]
    public void EveryTableRowIsBackedByABehaviouralTestWhoseSkipStateMatchesItsGap() {
        foreach (var row in RouteTable.Concat(EventTable)) {
            Assert.True(row.Action is not null || row.NoAuditBecause is not null,
                $"{row.Method} {row.Route}: give an expected audit action or a reason why none is written.");
            var method = typeof(AuditCoverageTests).GetMethod(row.Test, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(method is not null, $"{row.Method} {row.Route}: test {row.Test} does not exist.");
            var fact = method!.GetCustomAttribute<FactAttribute>();
            Assert.True(fact is not null, $"{row.Test} is not an xUnit test.");
            // A covered action must have a running test; a gap must be a skipped test that states the gap.
            Assert.Equal(row.Gap, fact!.Skip);
        }
    }

    [Fact]
    public void AuditEventsExposeNoUpdateOrDeletePath() {
        // Entity: no public setters and no public mutators beyond the Record factory.
        Assert.All(typeof(AuditEvent).GetProperties(), property => Assert.Null(property.GetSetMethod(nonPublic: false)));
        Assert.DoesNotContain(typeof(AuditEvent).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => !method.IsSpecialName);
        Assert.Equal(RecordFactoryOnly, typeof(AuditEvent)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(method => method.Name));

        // Writer abstraction is append-only.
        Assert.Equal(WriteOnly, typeof(IAuditWriter).GetMethods().Select(method => method.Name));

        // No production code updates or deletes audit rows, and the API exposes audit only through GET.
        var root = RepoRoot();
        var mutation = new Regex(@"AuditEvents\s*\.\s*(Remove|RemoveRange|Update|UpdateRange|Attach|AttachRange|ExecuteDelete|ExecuteDeleteAsync|ExecuteUpdate|ExecuteUpdateAsync)\b",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var rawSql = new Regex(@"(UPDATE|DELETE\s+FROM)\s+""?audit""?\.""?audit_events", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => {
                var text = File.ReadAllText(path);
                return mutation.IsMatch(text) || rawSql.IsMatch(text);
            })
            .ToList();
        Assert.Empty(offenders);

        var program = File.ReadAllText(Path.Combine(root, "src", "AegisOps.Api", "Program.cs"));
        var auditRoutes = Regex.Matches(program, @"app\.Map(?<verb>\w+)\(\s*""/api/v1/audit[^""]*""", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => match.Groups["verb"].Value).ToList();
        Assert.Equal(GetOnly, auditRoutes);
    }

    // ---------- Identity ----------

    [Fact]
    public async Task LoginLogoutAndPasswordChangeAreAudited() {
        await using var host = await AuditHost.StartAsync();
        var user = await host.CreateUserAsync("dev@example.test", "Developer");

        var login = await host.CallAsync((services, http) => Login.Handle(new LoginRequest(user.Email, AuditHost.Password),
            Users(services), services.GetRequiredService<IOptions<JwtOptions>>(), TimeProvider.System, Db(services), Audit(services), http));
        Assert.Equal(StatusCodes.Status200OK, login.Status);
        AssertSingleAudit(login.Rows, "auth.login", "User", user.Id, user.Email, "User", user.Id, "Succeeded");
        var cookie = RefreshCookieOf(login.Http);

        var logout = await host.CallAsync((services, http) => Logout.Handle(http, user.Principal, Db(services), Audit(services), TimeProvider.System),
            http => http.Request.Headers.Cookie = cookie);
        Assert.Equal(StatusCodes.Status204NoContent, logout.Status);
        AssertSingleAudit(logout.Rows, "auth.logout", "User", user.Id, user.Email, "User", user.Id, "Succeeded");

        var changed = await host.CallAsync((services, _) => ChangePassword.Handle(new ChangePasswordRequest(AuditHost.Password, "Another-Strong-Passw0rd!"),
            user.Principal, Users(services), Db(services), Audit(services), TimeProvider.System));
        Assert.Equal(StatusCodes.Status204NoContent, changed.Status);
        AssertSingleAudit(changed.Rows, "auth.password_changed", "User", user.Id, user.Email, "User", user.Id, "Succeeded");
    }

    [Fact]
    public async Task FailedLoginIsAudited() {
        await using var host = await AuditHost.StartAsync();
        var user = await host.CreateUserAsync("dev@example.test", "Developer");

        var failed = await host.CallAsync((services, http) => Login.Handle(new LoginRequest(user.Email, "wrong-password-123!"),
            Users(services), services.GetRequiredService<IOptions<JwtOptions>>(), TimeProvider.System, Db(services), Audit(services), http));
        Assert.Equal(StatusCodes.Status401Unauthorized, failed.Status);
        // Anonymous actor with a fixed display: neither the submitted email nor the password is recorded.
        AssertSingleAudit(failed.Rows, "auth.login_failed", "Anonymous", null, "anonymous", "User", user.Id, "Denied");

        var unknown = await host.CallAsync((services, http) => Login.Handle(new LoginRequest("nobody@example.test", AuditHost.Password),
            Users(services), services.GetRequiredService<IOptions<JwtOptions>>(), TimeProvider.System, Db(services), Audit(services), http));
        Assert.Equal(StatusCodes.Status401Unauthorized, unknown.Status);
        AssertSingleAudit(unknown.Rows, "auth.login_failed", "Anonymous", null, "anonymous", "User", null, "Denied");
    }

    [Fact]
    public async Task RefreshRotationIsAudited() {
        await using var host = await AuditHost.StartAsync();
        var user = await host.CreateUserAsync("dev@example.test", "Developer");
        var cookie = await host.LoginCookieAsync(user);

        var refreshed = await host.CallAsync((services, http) => Refresh.Handle(http, Db(services), Users(services),
            services.GetRequiredService<IOptions<JwtOptions>>(), TimeProvider.System, Audit(services)), http => http.Request.Headers.Cookie = cookie);

        Assert.Equal(StatusCodes.Status200OK, refreshed.Status);
        AssertSingleAudit(refreshed.Rows, "auth.refreshed", "User", user.Id, user.Email, "User", user.Id, "Succeeded");
    }

    [Fact]
    public async Task RefreshTokenReuseIsAudited() {
        await using var host = await AuditHost.StartAsync();
        var user = await host.CreateUserAsync("dev@example.test", "Developer");
        var cookie = await host.LoginCookieAsync(user);
        var first = await host.CallAsync((services, http) => Refresh.Handle(http, Db(services), Users(services),
            services.GetRequiredService<IOptions<JwtOptions>>(), TimeProvider.System, Audit(services)), http => http.Request.Headers.Cookie = cookie);
        Assert.Equal(StatusCodes.Status200OK, first.Status);

        var replayed = await host.CallAsync((services, http) => Refresh.Handle(http, Db(services), Users(services),
            services.GetRequiredService<IOptions<JwtOptions>>(), TimeProvider.System, Audit(services)), http => http.Request.Headers.Cookie = cookie);

        Assert.Equal(StatusCodes.Status401Unauthorized, replayed.Status);
        AssertSingleAudit(replayed.Rows, "auth.refresh_reuse_detected", "User", user.Id, user.Email, "User", user.Id, "Denied");
        // Persisted together with the family revocation.
        Assert.False(await host.WithDbAsync(db => db.RefreshTokens.AnyAsync(token => token.RevokedAt == null, Token)));
    }

    [Fact]
    public async Task ApiKeyAuthenticationFailureIsAudited() {
        await using var host = await AuditHost.StartAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");
        var world = await host.SeedWorldAsync();

        // Malformed or unknown keys need no credentials to send: 401 without an audit row, so
        // anonymous traffic cannot grow the immutable audit table (docs/09 §7).
        var before = await host.AuditIdsAsync();
        using (var malformed = await host.SendAsync(HttpMethod.Get, AuditHost.ProbePath, "aok_notakey_xxxxxxxxxxxxxxxxxxxx")) {
            Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);
        }
        using (var unknown = await host.SendAsync(HttpMethod.Get, AuditHost.ProbePath, ApiKeyGenerator.Create().Plaintext)) {
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        }
        Assert.Empty(await host.AuditSinceAsync(before));

        // Known prefix, wrong secret: attributed to that key, display is only the 8-character prefix.
        var issued = await host.CallAsync((services, http) => CreateApiKey.Handle(world.ProjectSlug,
            new CreateApiKeyRequest("ci", ArtifactScopes, DateTimeOffset.UtcNow.AddDays(30)),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status201Created, issued.Status);
        var (keyId, prefix) = await host.WithDbAsync(async db => {
            var key = await db.ApiKeys.SingleAsync(Token);
            return (key.Id, key.KeyPrefix);
        });
        const string wrongSecret = "wrong-secret-value-that-must-never-be-logged";
        before = await host.AuditIdsAsync();
        using (var wrong = await host.SendAsync(HttpMethod.Get, AuditHost.ProbePath, $"aok_{prefix}_{wrongSecret}")) {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        var row = AssertSingleAudit(await host.AuditSinceAsync(before), "apikey.auth_failed", "ApiKey", keyId, prefix, "ApiKey", keyId, "Denied");
        Assert.Equal(8, row.ActorDisplay.Length);
        Assert.DoesNotContain(wrongSecret, row.ActorDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiKeyAuthenticationFailureStays401WhenTheAuditWriteFails() {
        await using var host = await AuditHost.StartAsync(failingAudit: true);
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");
        var world = await host.SeedWorldAsync();
        // A real key tried with a wrong secret is audited, so the throwing writer is exercised.
        var generated = ApiKeyGenerator.Create();
        await host.WithDbAsync(async db => {
            db.ApiKeys.Add(ApiKey.Create(world.ProjectId, "ci", generated.Prefix, generated.Hash, ArtifactScopes,
                DateTimeOffset.UtcNow.AddDays(30), admin.Id, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Token);
        });

        using var response = await host.SendAsync(HttpMethod.Get, AuditHost.ProbePath, $"aok_{generated.Prefix}_wrong-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RouteAuthorizationDenialIsAudited() {
        await using var host = await AuditHost.StartAsync();
        var developer = await host.CreateUserAsync("dev@example.test", "Developer");

        var before = await host.AuditIdsAsync();
        using var response = await host.SendAsync(HttpMethod.Get, AuditHost.ProbePath, developer.Bearer);
        var rows = await host.AuditSinceAsync(before);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // Written by AuditingAuthorizationResultHandler (docs/09 §5).
        AssertSingleAudit(rows, AuditingAuthorizationResultHandler.DeniedAction, "User", developer.Id, developer.Email, "Endpoint", null, "Denied");
    }

    // ---------- Teams and webhooks ----------

    [Fact]
    public async Task TeamLifecycleAndMembershipChangesAreAudited() {
        await using var host = await AuditHost.StartAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");
        var developer = await host.CreateUserAsync("dev@example.test", "Developer");

        var created = await host.CallAsync((services, http) => CreateTeam.Handle(new CreateTeamRequest("Platform", "platform", null),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status201Created, created.Status);
        var teamId = await host.WithDbAsync(db => db.Teams.Where(team => team.Slug == "platform").Select(team => team.Id).SingleAsync(Token));
        AssertSingleAudit(created.Rows, "team.created", "User", admin.Id, admin.Email, "Team", teamId, "Succeeded");

        var updated = await host.CallAsync((services, http) => UpdateTeam.Handle("platform", new UpdateTeamRequest("Platform Eng", null),
            admin.Principal, Users(services), Db(services), Audit(services), http));
        Assert.Equal(StatusCodes.Status200OK, updated.Status);
        AssertSingleAudit(updated.Rows, "team.updated", "User", admin.Id, admin.Email, "Team", teamId, "Succeeded");

        var added = await host.CallAsync((services, http) => UpsertTeamMember.Handle("platform", developer.Id, new UpsertTeamMemberRequest("Member"),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status200OK, added.Status);
        AssertSingleAudit(added.Rows, "team.member_upserted", "User", admin.Id, admin.Email, "Team", teamId, "Succeeded");

        // A team role change (Member -> Owner) is the same upsert action and is audited too.
        var promoted = await host.CallAsync((services, http) => UpsertTeamMember.Handle("platform", developer.Id, new UpsertTeamMemberRequest("Owner"),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status200OK, promoted.Status);
        AssertSingleAudit(promoted.Rows, "team.member_upserted", "User", admin.Id, admin.Email, "Team", teamId, "Succeeded");

        var removed = await host.CallAsync((services, http) => RemoveTeamMember.Handle("platform", developer.Id,
            admin.Principal, Users(services), Db(services), Audit(services), http));
        Assert.Equal(StatusCodes.Status204NoContent, removed.Status);
        AssertSingleAudit(removed.Rows, "team.member_removed", "User", admin.Id, admin.Email, "Team", teamId, "Succeeded");
    }

    [Fact]
    public async Task WebhookSettingsAndKeyRewrapAreAudited() {
        await using var host = await AuditHost.StartAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");
        var teamId = await host.CreateTeamAsync(admin, "platform");
        var oldOnly = Protector("old", ("old", Key(1)));

        var set = await host.CallAsync((services, http) => TeamWebhookSettingsEndpoints.Set("platform",
            new SetTeamWebhookRequest("https://example.com/hook", Convert.ToBase64String(Key(2))),
            admin.Principal, Users(services), Db(services), Audit(services), oldOnly, http));
        Assert.Equal(StatusCodes.Status200OK, set.Status);
        AssertSingleAudit(set.Rows, "team.webhook_updated", "User", admin.Id, admin.Email, "TeamWebhook", teamId, "Succeeded");

        var rotating = Protector("new", ("old", Key(1)), ("new", Key(3)));
        var rewrap = await host.CallAsync((services, http) => TeamWebhookSettingsEndpoints.Rewrap(
            admin.Principal, Users(services), Db(services), Audit(services), rotating, http));
        Assert.Equal(StatusCodes.Status200OK, rewrap.Status);
        AssertSingleAudit(rewrap.Rows, "team.webhook_keys_rewrapped", "User", admin.Id, admin.Email, "TeamWebhook", null, "Succeeded");
    }

    // ---------- Projects, environments, repositories, API keys ----------

    [Fact]
    public async Task ProjectEnvironmentRepositoryAndApiKeyChangesAreAudited() {
        await using var host = await AuditHost.StartAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");
        var teamId = await host.CreateTeamAsync(admin, "platform");

        var created = await host.CallAsync((services, http) => CreateProject.Handle(new CreateProjectRequest(teamId, "Circuit", "circuit", null),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status201Created, created.Status);
        var projectId = await host.WithDbAsync(db => db.Projects.Where(project => project.Slug == "circuit").Select(project => project.Id).SingleAsync(Token));
        AssertSingleAudit(created.Rows, "project.created", "User", admin.Id, admin.Email, "Project", projectId, "Succeeded");

        var updated = await host.CallAsync((services, http) => UpdateProject.Handle("circuit", new UpdateProjectRequest(null, "Payments circuit", null),
            admin.Principal, Users(services), Db(services), Audit(services), http));
        Assert.Equal(StatusCodes.Status200OK, updated.Status);
        AssertSingleAudit(updated.Rows, "project.updated", "User", admin.Id, admin.Email, "Project", projectId, "Succeeded");

        var repository = await host.CallAsync((services, http) => UpsertRepository.Handle("circuit",
            new UpsertRepositoryRequest("GitHub", "acme/circuit", "main", "https://github.com/acme/circuit"),
            admin.Principal, Users(services), Db(services), Audit(services), http));
        Assert.Equal(StatusCodes.Status200OK, repository.Status);
        var repositoryId = await host.WithDbAsync(db => db.Repositories.Where(item => item.ProjectId == projectId).Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(repository.Rows, "repository.upserted", "User", admin.Id, admin.Email, "Repository", repositoryId, "Succeeded");

        var environmentId = await host.WithDbAsync(db => db.Environments
            .Where(item => item.ProjectId == projectId && item.Tier == EnvironmentTier.Production).Select(item => item.Id).SingleAsync(Token));
        // Target change (docs/17 "environment.target_changed") is audited as environment.updated.
        var environment = await host.CallAsync((services, http) => UpdateEnvironment.Handle("circuit", environmentId,
            new UpdateEnvironmentRequest(null, null, new UpdateEnvironmentTargetRequest(null, true, null, null, null, null, null, null)),
            admin.Principal, Users(services), Db(services), Audit(services), http));
        Assert.Equal(StatusCodes.Status200OK, environment.Status);
        AssertSingleAudit(environment.Rows, "environment.updated", "User", admin.Id, admin.Email, "Environment", environmentId, "Succeeded");

        var key = await host.CallAsync((services, http) => CreateApiKey.Handle("circuit",
            new CreateApiKeyRequest("ci", ArtifactScopes, DateTimeOffset.UtcNow.AddDays(30)),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status201Created, key.Status);
        var keyId = await host.WithDbAsync(db => db.ApiKeys.Where(item => item.ProjectId == projectId).Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(key.Rows, "apikey.created", "User", admin.Id, admin.Email, "ApiKey", keyId, "Succeeded");

        var revoked = await host.CallAsync((services, http) => RevokeApiKey.Handle("circuit", keyId,
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status204NoContent, revoked.Status);
        AssertSingleAudit(revoked.Rows, "apikey.revoked", "User", admin.Id, admin.Email, "ApiKey", keyId, "Succeeded");
    }

    // ---------- Artifacts, scans, findings ----------

    [Fact]
    public async Task ArtifactScanAndFindingChangesAreAudited() {
        await using var host = await AuditHost.StartAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");
        var world = await host.SeedWorldAsync();

        var created = await host.CallAsync((services, http) => CreateArtifact.Handle(world.ProjectSlug,
            new CreateArtifactRequest("2.0.0", new string('c', 40), "main", "registry.example/circuit:2.0.0", null, null, null, null, "Passed", "Passed", null),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, new MemoryIdempotencyStore(), http));
        Assert.Equal(StatusCodes.Status201Created, created.Status);
        var artifactId = await host.WithDbAsync(db => db.Artifacts.Where(item => item.Version == "2.0.0").Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(created.Rows, "artifact.created", "User", admin.Id, admin.Email, "Artifact", artifactId, "Succeeded");

        var backfilled = await host.CallAsync((services, http) => BackfillArtifactDigest.Handle(artifactId,
            new BackfillArtifactDigestRequest("sha256:" + new string('d', 64)),
            admin.Principal, Users(services), Db(services), Audit(services), http));
        Assert.Equal(StatusCodes.Status200OK, backfilled.Status);
        AssertSingleAudit(backfilled.Rows, "artifact.digest_backfilled", "User", admin.Id, admin.Email, "Artifact", artifactId, "Succeeded");

        var uploaded = await host.UploadScanAsync(artifactId, admin.Principal);
        Assert.Equal(StatusCodes.Status201Created, uploaded.Status);
        var scanId = await host.WithDbAsync(db => db.SecurityScans.Where(item => item.ArtifactId == artifactId).Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(uploaded.Rows, "scan.uploaded", "User", admin.Id, admin.Email, "SecurityScan", scanId, "Succeeded");

        var suppressed = await host.CallAsync((services, http) => SuppressFinding.Handle(world.ProjectSlug,
            new SuppressFindingRequest(SarifFingerprint, "Accepted risk", null),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status200OK, suppressed.Status);
        var suppressionId = await host.WithDbAsync(db => db.FindingSuppressions.Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(suppressed.Rows, "finding.suppressed", "User", admin.Id, admin.Email, "FindingSuppression", suppressionId, "Succeeded");
    }

    [Fact]
    public async Task ApiKeyActionsAreAttributedToTheApiKeyActorType() {
        await using var host = await AuditHost.StartAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");
        var world = await host.SeedWorldAsync();
        var issued = await host.CallAsync((services, http) => CreateApiKey.Handle(world.ProjectSlug,
            new CreateApiKeyRequest("ci", ArtifactScopes, DateTimeOffset.UtcNow.AddDays(30)),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status201Created, issued.Status);
        var keyId = await host.WithDbAsync(db => db.ApiKeys.Select(item => item.Id).SingleAsync(Token));
        // Claims exactly as ApiKeyAuthenticationHandler issues them.
        var key = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("actor_type", "apiKey"),
            new Claim("sub", $"apiKey:{keyId}"),
            new Claim("project", world.ProjectId.ToString()),
        ], ApiKeyAuthenticationDefaults.Scheme));

        var created = await host.CallAsync((services, http) => CreateArtifact.Handle(world.ProjectSlug,
            new CreateArtifactRequest("3.0.0", new string('e', 40), "main", "registry.example/circuit:3.0.0", null, null, null, null, "Passed", "Passed", null),
            key, Users(services), Db(services), Audit(services), TimeProvider.System, new MemoryIdempotencyStore(), http),
            http => http.Request.Headers["Idempotency-Key"] = "ci-run-1");

        Assert.Equal(StatusCodes.Status201Created, created.Status);
        var artifactId = await host.WithDbAsync(db => db.Artifacts.Where(item => item.Version == "3.0.0").Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(created.Rows, "artifact.created", "ApiKey", keyId, "api-key", "Artifact", artifactId, "Succeeded");

        var backfilled = await host.CallAsync((services, http) => BackfillArtifactDigest.Handle(artifactId,
            new BackfillArtifactDigestRequest("sha256:" + new string('f', 64)), key, Users(services), Db(services), Audit(services), http));
        Assert.Equal(StatusCodes.Status200OK, backfilled.Status);
        AssertSingleAudit(backfilled.Rows, "artifact.digest_backfilled", "ApiKey", keyId, "api-key", "Artifact", artifactId, "Succeeded");

        var uploaded = await host.UploadScanAsync(artifactId, key);
        Assert.Equal(StatusCodes.Status201Created, uploaded.Status);
        var scanId = await host.WithDbAsync(db => db.SecurityScans.Where(item => item.ArtifactId == artifactId).Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(uploaded.Rows, "scan.uploaded", "ApiKey", keyId, "api-key", "SecurityScan", scanId, "Succeeded");
    }

    // ---------- Policies ----------

    [Fact]
    public async Task PolicyLifecycleIsAudited() {
        await using var host = await AuditHost.StartAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");

        var created = await host.CallAsync((services, http) => CreatePolicy.Handle(
            new CreatePolicyRequest("Staging gate", null, "Global", null, StagingTiers, DenyFailedTestsRules),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status201Created, created.Status);
        var policyId = await host.WithDbAsync(db => db.Policies.Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(created.Rows, "policy.created", "User", admin.Id, admin.Email, "Policy", policyId, "Succeeded");

        var replaced = await host.CallAsync((services, http) => PolicyPhase1Endpoints.Replace(policyId,
            new CreatePolicyRequest("Staging gate v2", "Tests must pass", "Global", null, StagingTiers, DenyFailedTestsRules),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status200OK, replaced.Status);
        AssertSingleAudit(replaced.Rows, "policy.updated", "User", admin.Id, admin.Email, "Policy", policyId, "Succeeded");

        var disabled = await host.CallAsync((services, http) => SetPolicyEnabled.Handle(policyId, new SetPolicyEnabledRequest(false),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status200OK, disabled.Status);
        AssertSingleAudit(disabled.Rows, "policy.disabled", "User", admin.Id, admin.Email, "Policy", policyId, "Succeeded");

        var enabled = await host.CallAsync((services, http) => SetPolicyEnabled.Handle(policyId, new SetPolicyEnabledRequest(true),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status200OK, enabled.Status);
        AssertSingleAudit(enabled.Rows, "policy.enabled", "User", admin.Id, admin.Email, "Policy", policyId, "Succeeded");

        var archived = await host.CallAsync((services, http) => ArchivePolicy.Handle(policyId,
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.InRange(archived.Status, 200, 299);
        AssertSingleAudit(archived.Rows, "policy.archived", "User", admin.Id, admin.Email, "Policy", policyId, "Succeeded");
    }

    [Fact]
    public async Task PolicySimulationIsReadOnlyAndWritesNoAudit() {
        await using var host = await AuditHost.StartAsync();
        var world = await host.SeedWorldAsync();

        var simulated = await host.CallAsync((services, http) => PolicyPhase1Endpoints.Simulate(
            new SimulatePolicyRequest(world.ArtifactId, world.EnvironmentId), Db(services), TimeProvider.System, http));

        Assert.Equal(StatusCodes.Status200OK, simulated.Status);
        Assert.Empty(simulated.Rows);
        Assert.Equal(0, await host.WithDbAsync(db => db.PolicyEvaluations.CountAsync(Token)));
    }

    // ---------- AI ----------

    [Fact]
    public async Task AiChatIsAuditedWhenCompletedAndWhenBlocked() {
        await using var host = await AuditHost.StartAsync();
        var user = await host.CreateUserAsync("dev@example.test", "Developer");

        // No AI policy: the gateway blocks (503 PolicyUnavailable) and still writes a Denied audit row.
        var blocked = await host.CallAsync((services, http) => AiEndpoints.Chat(new AiChatRequest("audit-model", null, HelloMessages),
            user.Principal, Users(services), Db(services), Gateway(services), http));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, blocked.Status);
        var blockedRequestId = await host.WithDbAsync(db => db.AiRequests.Select(item => item.Id).SingleAsync(Token));
        // The gateway records the user id but a fixed "AI user" display label.
        AssertSingleAudit(blocked.Rows, "ai.request", "User", user.Id, "AI user", "AiRequest", blockedRequestId, "Denied");

        await host.WithDbAsync(async db => {
            var model = AiModel.Create("audit-model");
            model.SetEnabled(true);
            var policy = AiPolicy.Create("global", PolicyScope.Global, null, user.Id, DateTimeOffset.UtcNow);
            policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Allow, [], false, false, true, user.Id, DateTimeOffset.UtcNow);
            db.AddRange(model, policy);
            await db.SaveChangesAsync(Token);
        });

        var completed = await host.CallAsync((services, http) => AiEndpoints.Chat(new AiChatRequest("audit-model", null, HelloMessages),
            user.Principal, Users(services), Db(services), Gateway(services), http));
        Assert.Equal(StatusCodes.Status200OK, completed.Status);
        var completedRequestId = await host.WithDbAsync(db => db.AiRequests.Where(item => item.Id != blockedRequestId).Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(completed.Rows, "ai.request", "User", user.Id, "AI user", "AiRequest", completedRequestId, "Succeeded");
    }

    [Fact]
    public async Task AiFindingExplanationAndDeploymentSummaryAreAudited() {
        await using var host = await AuditHost.StartAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");
        var world = await host.SeedWorldAsync();
        Assert.Equal(StatusCodes.Status201Created, (await host.UploadScanAsync(world.ArtifactId, admin.Principal)).Status);
        var findingId = await host.WithDbAsync(db => db.SecurityFindings.Select(item => item.Id).SingleAsync(Token));

        var explained = await host.CallAsync((services, http) => AiEndpoints.ExplainFinding(findingId, new AiFeatureRequest("audit-model"),
            admin.Principal, Users(services), Db(services), Gateway(services), http));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, explained.Status);
        var explainId = await host.WithDbAsync(db => db.AiRequests.Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(explained.Rows, "ai.request", "User", admin.Id, "AI user", "AiRequest", explainId, "Denied");

        var deploymentId = await host.SeedDeploymentAsync(world, admin.Id, awaitingApproval: true);
        var summarized = await host.CallAsync((services, http) => AiEndpoints.SummarizeDeployment(deploymentId, new AiFeatureRequest("audit-model"),
            admin.Principal, Users(services), Db(services), Gateway(services), http));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, summarized.Status);
        var summaryId = await host.WithDbAsync(db => db.AiRequests.Where(item => item.Id != explainId).Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(summarized.Rows, "ai.request", "User", admin.Id, "AI user", "AiRequest", summaryId, "Denied");
    }

    [Fact]
    public async Task AiPolicySaveIsAudited() {
        await using var host = await AuditHost.StartAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");

        var saved = await host.CallAsync((services, http) => AiEndpoints.SavePolicy(
            new AiPolicyRequest(null, "global", PolicyScope.Global, null, [], 20, 500, 16000, SensitiveDataAction.Redact, [], true, false, true),
            admin.Principal, Users(services), Db(services), Audit(services), http));

        Assert.Equal(StatusCodes.Status200OK, saved.Status);
        var policyId = await host.WithDbAsync(db => db.AiPolicies.Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(saved.Rows, "ai.policy_updated", "User", admin.Id, admin.Email, "AiPolicy", policyId, "Succeeded");
    }

    [Fact]
    public async Task AiModelSyncIsAudited() {
        await using var host = await AuditHost.StartAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");

        var synced = await host.CallAsync((services, http) => AiEndpoints.SyncModels(new StubLlm(),
            admin.Principal, Users(services), Db(services), Audit(services), http));

        Assert.Equal(StatusCodes.Status200OK, synced.Status);
        AssertSingleAudit(synced.Rows, "ai.model_synced", "User", admin.Id, admin.Email, "AiModel", null, "Succeeded");
        Assert.True(await host.WithDbAsync(db => db.AiModels.AnyAsync(model => model.Name == "llama3", Token)));
    }

    // ---------- Deployments (API) ----------

    [Fact]
    public async Task DeploymentRequestApprovalCancelAndRollbackAreAudited() {
        await using var host = await AuditHost.StartAsync();
        var requester = await host.CreateUserAsync("dev@example.test", "Developer");
        var colleague = await host.CreateUserAsync("dev2@example.test", "Developer");
        var approver = await host.CreateUserAsync("approver@example.test", "Approver");
        var world = await host.SeedWorldAsync(withPredecessor: true);
        await host.JoinAsync(world.TeamId, requester.Id, approver.Id, colleague.Id);

        // Request.
        var requested = await host.CallAsync((services, http) => DeploymentEndpoints.Request(
            new RequestDeploymentRequest(world.ArtifactId, world.EnvironmentId, null, "ship it"),
            requester.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, new MemoryIdempotencyStore(), http));
        Assert.Equal(StatusCodes.Status202Accepted, requested.Status);
        var deploymentId = await host.WithDbAsync(db => db.Deployments.Where(item => item.Reason == "ship it").Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(requested.Rows, "deployment.requested", "User", requester.Id, requester.Email, "Deployment", deploymentId, "Accepted");

        // Denied decision: the requester may not approve their own deployment.
        await host.RequireApprovalAsync(deploymentId);
        var selfApproval = await host.CallAsync((services, http) => DeploymentEndpoints.Decide(deploymentId, new DeploymentDecisionRequest("Approved", null),
            requester.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status403Forbidden, selfApproval.Status);
        AssertSingleAudit(selfApproval.Rows, "deployment.decision_denied", "User", requester.Id, requester.Email, "Deployment", deploymentId, "Denied");

        // Approve.
        var approved = await host.CallAsync((services, http) => DeploymentEndpoints.Decide(deploymentId, new DeploymentDecisionRequest("Approved", "LGTM"),
            approver.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status204NoContent, approved.Status);
        AssertSingleAudit(approved.Rows, "deployment.approved", "User", approver.Id, approver.Email, "Deployment", deploymentId, "Approved");

        // Reject.
        var toReject = await host.SeedDeploymentAsync(world, requester.Id, awaitingApproval: true);
        var rejected = await host.CallAsync((services, http) => DeploymentEndpoints.Decide(toReject, new DeploymentDecisionRequest("Rejected", "Not today"),
            approver.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status204NoContent, rejected.Status);
        AssertSingleAudit(rejected.Rows, "deployment.rejected", "User", approver.Id, approver.Email, "Deployment", toReject, "Rejected");

        // Denied cancel: a team member who is neither requester nor Owner.
        var toCancel = await host.SeedDeploymentAsync(world, requester.Id, awaitingApproval: false);
        var cancelDenied = await host.CallAsync((services, http) => DeploymentEndpoints.Cancel(toCancel,
            colleague.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status403Forbidden, cancelDenied.Status);
        AssertSingleAudit(cancelDenied.Rows, "deployment.cancel_denied", "User", colleague.Id, colleague.Email, "Deployment", toCancel, "Denied");

        // Cancel by the requester.
        var cancelled = await host.CallAsync((services, http) => DeploymentEndpoints.Cancel(toCancel,
            requester.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status204NoContent, cancelled.Status);
        AssertSingleAudit(cancelled.Rows, "deployment.cancelled", "User", requester.Id, requester.Email, "Deployment", toCancel, "Cancelled");

        // Rollback of a succeeded deployment enqueues (and audits) a new deployment request.
        var source = await host.SeedSucceededDeploymentAsync(world, requester.Id);
        var rolledBack = await host.CallAsync((services, http) => DeploymentEndpoints.Rollback(source,
            requester.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, new MemoryIdempotencyStore(), http));
        Assert.Equal(StatusCodes.Status202Accepted, rolledBack.Status);
        var rollbackId = await host.WithDbAsync(db => db.Deployments.Where(item => item.Reason == $"rollback of {source:D}").Select(item => item.Id).SingleAsync(Token));
        AssertSingleAudit(rolledBack.Rows, "deployment.requested", "User", requester.Id, requester.Email, "Deployment", rollbackId, "Accepted");
    }

    // ---------- Worker ----------

    [Fact]
    public async Task WorkerEvaluationAndNoopExecutionAreAudited() {
        await using var host = await AuditHost.StartAsync();
        var requester = await host.CreateUserAsync("dev@example.test", "Developer");
        var world = await host.SeedWorldAsync();
        var deploymentId = await host.SeedDeploymentAsync(world, requester.Id, awaitingApproval: false);
        var correlation = await host.WithDbAsync(db => db.Deployments.Where(item => item.Id == deploymentId).Select(item => item.CorrelationId).SingleAsync(Token));

        var evaluated = await host.WorkerAsync((db, audit) => DeploymentProcessor.EvaluateAsync(db, audit, deploymentId, TimeProvider.System, Token));
        var evaluation = AssertSingleAudit(evaluated, "deployment.evaluated", "System", null, "policy-engine", "Deployment", deploymentId, "Allow");
        Assert.Equal(correlation, evaluation.CorrelationId);

        var executed = await host.WorkerAsync((db, audit) => DeploymentProcessor.ExecuteAsync(db, audit, deploymentId, TimeProvider.System, Token));
        var success = AssertSingleAudit(executed, "deployment.succeeded", "System", null, "noop-executor", "Deployment", deploymentId, "Succeeded");
        Assert.Equal(correlation, success.CorrelationId);
    }

    [Fact]
    public async Task WorkerPolicyDenialIsAudited() {
        await using var host = await AuditHost.StartAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");
        var world = await host.SeedWorldAsync(testStatus: CheckStatus.Failed);
        var policy = await host.CallAsync((services, http) => CreatePolicy.Handle(
            new CreatePolicyRequest("Staging gate", null, "Global", null, StagingTiers, DenyFailedTestsRules),
            admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
        Assert.Equal(StatusCodes.Status201Created, policy.Status);
        var deploymentId = await host.SeedDeploymentAsync(world, admin.Id, awaitingApproval: false);

        var evaluated = await host.WorkerAsync((db, audit) => DeploymentProcessor.EvaluateAsync(db, audit, deploymentId, TimeProvider.System, Token));

        AssertSingleAudit(evaluated, "deployment.evaluated", "System", null, "policy-engine", "Deployment", deploymentId, "Deny");
        Assert.Equal(DeploymentStatus.Denied, await host.WithDbAsync(db => db.Deployments.Where(item => item.Id == deploymentId).Select(item => item.Status).SingleAsync(Token)));
    }

    [Fact]
    public async Task WorkerDockerExecutionFailureIsAudited() {
        await using var host = await AuditHost.StartAsync();
        var requester = await host.CreateUserAsync("dev@example.test", "Developer");
        var world = await host.SeedWorldAsync();
        var deploymentId = await host.SeedDeploymentAsync(world, requester.Id, awaitingApproval: false);
        Assert.NotEmpty(await host.WorkerAsync((db, audit) => DeploymentProcessor.EvaluateAsync(db, audit, deploymentId, TimeProvider.System, Token)));

        // A docker target without container name/port fails inside the executor before any Docker API call.
        await host.WithDbAsync(async db => {
            var environment = await db.Environments.SingleAsync(item => item.Id == world.EnvironmentId, Token);
            db.Entry(environment).Reference(item => item.Target).TargetEntry!.Property(nameof(DeploymentTarget.Type)).CurrentValue = "docker";
            await db.SaveChangesAsync(Token);
        });

        var executed = await host.WorkerAsync((db, audit) => DeploymentProcessor.ExecuteAsync(db, audit, deploymentId, TimeProvider.System, Token));

        AssertSingleAudit(executed, "deployment.failed", "System", null, "docker-executor", "Deployment", deploymentId, "Failed");
    }

    // ---------- Jobs ----------

    [Fact]
    public async Task DeadLetteredJobRetryIsAudited() {
        await using var host = await AuditHost.StartAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");
        var jobId = await host.SeedDeadLetteredJobAsync();

        var retried = await host.CallAsync((services, http) => AdminJobs.Retry(jobId, admin.Principal, Users(services), Db(services),
            TimeProvider.System, Audit(services), http));

        Assert.Equal(StatusCodes.Status204NoContent, retried.Status);
        var row = AssertSingleAudit(retried.Rows, "job.retried", "User", admin.Id, admin.Email, "Job", jobId, "Pending");
        Assert.Equal("job-correlation", row.CorrelationId);
    }

    [Fact]
    public async Task DeadLetteredJobRetryIsAttributedToTheAdmin() {
        await using var host = await AuditHost.StartAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");
        var jobId = await host.SeedDeadLetteredJobAsync();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        // Without a resolvable signed-in actor the retry is refused and nothing is written.
        var refused = await host.CallAsync((services, http) => AdminJobs.Retry(jobId, anonymous, Users(services), Db(services),
            TimeProvider.System, Audit(services), http));
        Assert.Equal(StatusCodes.Status401Unauthorized, refused.Status);
        Assert.Empty(refused.Rows);
        Assert.Equal(JobStatus.DeadLettered, await host.WithDbAsync(db => db.Jobs.Where(job => job.Id == jobId).Select(job => job.Status).SingleAsync(Token)));

        var retried = await host.CallAsync((services, http) => AdminJobs.Retry(jobId, admin.Principal, Users(services), Db(services),
            TimeProvider.System, Audit(services), http));
        Assert.Equal(StatusCodes.Status204NoContent, retried.Status);
        AssertSingleAudit(retried.Rows, "job.retried", "User", admin.Id, admin.Email, "Job", jobId, "Pending");
    }

    // ---------- Helpers ----------

    private static readonly string[] ArtifactScopes = ["artifacts:write"];
    private static readonly string[] RecordFactoryOnly = [nameof(AuditEvent.Record)];
    private static readonly string[] WriteOnly = [nameof(IAuditWriter.Write)];
    private static readonly string[] GetOnly = ["Get"];
    private static readonly string[] StagingTiers = ["Staging"];
    private static readonly CreatePolicyRuleRequest[] DenyFailedTestsRules = [new("RequireTestsPassed", "Deny", 1, null)];
    private static readonly LlmMessage[] HelloMessages = [new("user", "hello")];
    private static readonly string[] SyncedModelNames = ["llama3"];
    private const string ApproverRuleResults = """[{"Type":"RequireApprovals","RequiredRoles":["Approver"]}]""";
    private const string SarifFingerprint = "fp-audit";
    private const string SarifReport =
        """{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"semgrep"}},"results":[{"ruleId":"rule.audit","level":"error","message":{"text":"rule.audit found"},"partialFingerprints":{"primary":"fp-audit"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"src/app.cs"},"region":{"startLine":7}}}]}]}]}""";

    private static AegisOpsDbContext Db(IServiceProvider services) => services.GetRequiredService<AegisOpsDbContext>();
    private static UserManager<User> Users(IServiceProvider services) => services.GetRequiredService<UserManager<User>>();
    private static IAuditWriter Audit(IServiceProvider services) => services.GetRequiredService<IAuditWriter>();
    private static AiGateway Gateway(IServiceProvider services) =>
        new(Db(services), new StubLlm(), new AllowLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, Audit(services));

    private static AuditEvent AssertSingleAudit(IReadOnlyList<AuditEvent> rows, string action, string actorType, Guid? actorId,
        string actorDisplay, string resourceType, Guid? resourceId, string outcome) {
        var row = Assert.Single(rows);
        Assert.Equal((action, actorType, actorId, actorDisplay, resourceType, resourceId, outcome),
            (row.Action, row.ActorType, row.ActorId, row.ActorDisplay, row.ResourceType, row.ResourceId, row.Outcome));
        return row;
    }

    private static string RefreshCookieOf(HttpContext http) {
        var header = http.Response.Headers.SetCookie.Select(value => value ?? string.Empty)
            .Single(value => value.StartsWith(RefreshCookie.Name + "=", StringComparison.Ordinal));
        return header.Split(';')[0];
    }

    private static TeamWebhookSecretProtector Protector(string currentKeyId, params (string Id, byte[] Key)[] keys) {
        var values = new Dictionary<string, string?> {
            ["WebhookSecrets:Enabled"] = "true",
            ["WebhookSecrets:CurrentKeyId"] = currentKeyId,
        };
        foreach (var (id, key) in keys) values[$"WebhookSecrets:Keys:{id}"] = Convert.ToBase64String(key);
        return new TeamWebhookSecretProtector(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private static byte[] Key(byte value) => Enumerable.Repeat(value, 32).ToArray();

    private static string RepoRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            if (File.Exists(Path.Combine(directory.FullName, "AegisOps.slnx"))) return directory.FullName;
        }
        throw new InvalidOperationException("Repository root (AegisOps.slnx) was not found.");
    }

    private sealed record Actor(Guid Id, string Email, string Bearer, ClaimsPrincipal Principal);
    private sealed record Call(int Status, IReadOnlyList<AuditEvent> Rows, HttpContext Http);
    private sealed record World(Guid TeamId, Guid ProjectId, string ProjectSlug, Guid EnvironmentId, Guid ArtifactId, Guid PredecessorArtifactId);

    private sealed class AuditHost : IAsyncDisposable {
        public const string Password = "Correct-Horse-Battery-9!";
        public const string ProbePath = "/probe/users-manage";
        private static readonly JwtOptions Jwt = new() {
            Issuer = "aegisops-audit-tests",
            Audience = "aegisops-audit-tests",
            SigningKey = "audit-coverage-tests-signing-key-0123456789",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7,
        };

        private readonly WebApplication app;
        private readonly SqliteConnection connection;
        private readonly HttpClient client;
        private readonly string reports;

        private AuditHost(WebApplication app, SqliteConnection connection, string reports) {
            this.app = app;
            this.connection = connection;
            this.reports = reports;
            client = app.GetTestClient();
        }

        public static async Task<AuditHost> StartAsync(bool failingAudit = false) {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(Token);
            var reports = Path.Combine(Path.GetTempPath(), "aegisops-audit-" + Guid.NewGuid().ToString("N"));

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Jwt:Issuer"] = Jwt.Issuer,
                ["Jwt:Audience"] = Jwt.Audience,
                ["Jwt:SigningKey"] = Jwt.SigningKey,
                ["Jwt:AccessTokenMinutes"] = Jwt.AccessTokenMinutes.ToString(CultureInfo.InvariantCulture),
                ["Jwt:RefreshTokenDays"] = Jwt.RefreshTokenDays.ToString(CultureInfo.InvariantCulture),
                ["Storage:ReportsPath"] = reports,
            });
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection)
                .UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteModelCustomizer>());
            builder.Services.AddIdentityCore<User>().AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AegisOpsDbContext>();
            if (failingAudit) builder.Services.AddScoped<IAuditWriter, ThrowingAuditWriter>();
            else builder.Services.AddScoped<IAuditWriter, EfAuditWriter>();
            builder.Services.AddJwtAuthentication(builder.Configuration);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            // Real authentication (JWT or API key) and permission policy in front of a probe route.
            app.MapGet(ProbePath, () => Results.NoContent()).RequireAuthorization(Permissions.UsersManage);
            await app.StartAsync(Token);

            var host = new AuditHost(app, connection, reports);
            await host.WithDbAsync(db => db.Database.EnsureCreatedAsync(Token));
            return host;
        }

        public async Task<Actor> CreateUserAsync(string email, string role) {
            await using var scope = app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync(role))
                Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() })).Succeeded);
            var user = User.Create(email, email.Split('@')[0], DateTimeOffset.UtcNow);
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);

            // Principal = the claims of a real, validated access token (as JwtBearer would produce with MapInboundClaims=false).
            var bearer = JwtAccessTokenFactory.Create(user, [role], Jwt, DateTimeOffset.UtcNow);
            var validated = await new JsonWebTokenHandler().ValidateTokenAsync(bearer, new TokenValidationParameters {
                ValidIssuer = Jwt.Issuer,
                ValidAudience = Jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Jwt.SigningKey)),
                NameClaimType = JwtRegisteredClaimNames.Name,
                RoleClaimType = "role",
            });
            Assert.True(validated.IsValid);
            return new Actor(user.Id, email, bearer, new ClaimsPrincipal(validated.ClaimsIdentity));
        }

        public async Task<string> LoginCookieAsync(Actor user) {
            var login = await CallAsync((services, http) => Login.Handle(new LoginRequest(user.Email, Password),
                Users(services), services.GetRequiredService<IOptions<JwtOptions>>(), TimeProvider.System, Db(services), Audit(services), http));
            Assert.Equal(StatusCodes.Status200OK, login.Status);
            return RefreshCookieOf(login.Http);
        }

        public async Task<Guid> CreateTeamAsync(Actor admin, string slug) {
            var created = await CallAsync((services, http) => CreateTeam.Handle(new CreateTeamRequest(slug, slug, null),
                admin.Principal, Users(services), Db(services), Audit(services), TimeProvider.System, http));
            Assert.Equal(StatusCodes.Status201Created, created.Status);
            return await WithDbAsync(db => db.Teams.Where(team => team.Slug == slug).Select(team => team.Id).SingleAsync(Token));
        }

        public async Task<World> SeedWorldAsync(bool withPredecessor = false, CheckStatus testStatus = CheckStatus.Passed) {
            var now = DateTimeOffset.UtcNow;
            var creator = await CreateUserAsync($"ci-{Guid.NewGuid():N}@example.test", "Developer");
            var team = Team.Create("Platform", "platform", now);
            var project = Project.Create(team.Id, "Circuit", "circuit", now);
            var environment = ProjectEnvironment.Create(project.Id, "Staging", EnvironmentTier.Staging, 1, DeploymentTarget.Noop());
            var artifact = Artifact.Create(project.Id, "1.0.0", new string('a', 40), "main", "registry.example/circuit:1.0.0",
                CheckStatus.Passed, testStatus, now, createdById: creator.Id);
            var predecessor = Artifact.Create(project.Id, "0.9.0", new string('b', 40), "main", "registry.example/circuit:0.9.0",
                CheckStatus.Passed, CheckStatus.Passed, now.AddMinutes(-5), createdById: creator.Id);
            await WithDbAsync(async db => {
                db.AddRange(team, project, environment, artifact);
                if (withPredecessor) db.Add(predecessor);
                await db.SaveChangesAsync(Token);
            });
            return new World(team.Id, project.Id, project.Slug, environment.Id, artifact.Id, predecessor.Id);
        }

        public Task JoinAsync(Guid teamId, params Guid[] userIds) => WithDbAsync(async db => {
            foreach (var userId in userIds) db.TeamMembers.Add(TeamMember.Create(teamId, userId, TeamRole.Member, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Token);
        });

        public async Task<Guid> SeedDeploymentAsync(World world, Guid requesterId, bool awaitingApproval) {
            var deployment = Deployment.Request(world.ProjectId, world.EnvironmentId, world.ArtifactId, DateTimeOffset.UtcNow,
                Guid.NewGuid().ToString("N"), requesterId, null, "seeded", null);
            await WithDbAsync(async db => {
                db.Deployments.Add(deployment);
                await db.SaveChangesAsync(Token);
            });
            if (awaitingApproval) await RequireApprovalAsync(deployment.Id);
            return deployment.Id;
        }

        public Task RequireApprovalAsync(Guid deploymentId) => WithDbAsync(async db => {
            var now = DateTimeOffset.UtcNow;
            var deployment = await db.Deployments.SingleAsync(item => item.Id == deploymentId, Token);
            var evaluation = PolicyEvaluation.Create(deployment.Id, PolicyDecision.RequireApproval, 1, now, ApproverRuleResults);
            deployment.BeginEvaluation();
            deployment.ApplyDecision(new PolicyOutcome(PolicyDecision.RequireApproval, evaluation.Id, 1, "Approval required."), now);
            db.PolicyEvaluations.Add(evaluation);
            await db.SaveChangesAsync(Token);
        });

        public async Task<Guid> SeedSucceededDeploymentAsync(World world, Guid requesterId) {
            var now = DateTimeOffset.UtcNow;
            var source = Deployment.Request(world.ProjectId, world.EnvironmentId, world.ArtifactId, now, "source", requesterId, null,
                "seeded success", world.PredecessorArtifactId);
            source.BeginEvaluation();
            source.ApplyDecision(new PolicyOutcome(PolicyDecision.Allow, Guid.NewGuid(), 0, null), now);
            source.Start(now);
            source.Succeed(now);
            await WithDbAsync(async db => {
                db.Deployments.Add(source);
                var environment = await db.Environments.SingleAsync(item => item.Id == world.EnvironmentId, Token);
                environment.RecordDeployment(world.ArtifactId, source.Id);
                await db.SaveChangesAsync(Token);
            });
            return source.Id;
        }

        public async Task<Guid> SeedDeadLetteredJobAsync() {
            var now = DateTimeOffset.UtcNow;
            var job = Job.Enqueue(JobType.EvaluateDeployment, $$"""{"deploymentId":"{{Guid.NewGuid()}}"}""", now, "job-correlation");
            while (job.Status != JobStatus.DeadLettered) {
                job.MarkRunning(now, "worker-1");
                job.Fail("boom", now);
            }
            await WithDbAsync(async db => {
                db.Jobs.Add(job);
                await db.SaveChangesAsync(Token);
            });
            return job.Id;
        }

        public Task<Call> UploadScanAsync(Guid artifactId, ClaimsPrincipal principal) => CallAsync(
            (services, http) => UploadScan.Handle(artifactId, http.Request, principal, Users(services), Db(services), Audit(services),
                TimeProvider.System, services.GetRequiredService<IWebHostEnvironment>(), services.GetRequiredService<IConfiguration>()),
            http => {
                var bytes = Encoding.UTF8.GetBytes(SarifReport);
                var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "report", "report.sarif");
                var form = new FormCollection(new Dictionary<string, StringValues> {
                    ["scanner"] = "semgrep",
                    ["format"] = "sarif",
                    ["toolVersion"] = "1.0.0",
                }, new FormFileCollection { file });
                http.Request.ContentType = "multipart/form-data; boundary=audit-coverage";
                http.Features.Set<IFormFeature>(new FormFeature(form));
            });

        /// <summary>Runs one handler in its own DI scope and returns its status plus the audit rows it persisted.</summary>
        public async Task<Call> CallAsync(Func<IServiceProvider, HttpContext, Task<IResult>> call, Action<HttpContext>? configure = null) {
            var before = await AuditIdsAsync();
            await using var scope = app.Services.CreateAsyncScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = Token };
            configure?.Invoke(http);
            var result = await call(scope.ServiceProvider, http);
            var status = result switch {
                ForbidHttpResult => StatusCodes.Status403Forbidden,
                IStatusCodeHttpResult withStatus => withStatus.StatusCode ?? StatusCodes.Status200OK,
                _ => StatusCodes.Status200OK,
            };
            return new Call(status, await AuditSinceAsync(before), http);
        }

        /// <summary>Runs a Worker step in its own unit of work (as the job loop does) and returns the audit rows it persisted.</summary>
        public async Task<IReadOnlyList<AuditEvent>> WorkerAsync(Func<AegisOpsDbContext, IAuditWriter, Task> step) {
            var before = await AuditIdsAsync();
            await using (var scope = app.Services.CreateAsyncScope()) {
                var db = Db(scope.ServiceProvider);
                await step(db, Audit(scope.ServiceProvider));
                await db.SaveChangesAsync(Token);
            }
            return await AuditSinceAsync(before);
        }

        public Task<HashSet<Guid>> AuditIdsAsync() =>
            WithDbAsync(async db => (await db.AuditEvents.AsNoTracking().Select(item => item.Id).ToListAsync(Token)).ToHashSet());

        public Task<IReadOnlyList<AuditEvent>> AuditSinceAsync(HashSet<Guid> before) =>
            WithDbAsync<IReadOnlyList<AuditEvent>>(async db =>
                (await db.AuditEvents.AsNoTracking().ToListAsync(Token)).Where(item => !before.Contains(item.Id)).ToList());

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer) {
            var request = new HttpRequestMessage(method, path);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return client.SendAsync(request, Token);
        }

        public async Task WithDbAsync(Func<AegisOpsDbContext, Task> action) {
            await using var scope = app.Services.CreateAsyncScope();
            await action(Db(scope.ServiceProvider));
        }

        public async Task<T> WithDbAsync<T>(Func<AegisOpsDbContext, Task<T>> action) {
            await using var scope = app.Services.CreateAsyncScope();
            return await action(Db(scope.ServiceProvider));
        }

        public async ValueTask DisposeAsync() {
            client.Dispose();
            await app.DisposeAsync();
            await connection.DisposeAsync();
            if (Directory.Exists(reports)) Directory.Delete(reports, recursive: true);
        }
    }

    private sealed class MemoryIdempotencyStore : IIdempotencyStore {
        private readonly Dictionary<(string Principal, string Key), StoredResponse> responses = [];
        public Task<StoredResponse?> FindAsync(string principal, string key, CancellationToken cancellationToken) =>
            Task.FromResult(responses.TryGetValue((principal, key), out var response) ? response : null);
        public Task<string?> TryBeginAsync(string principal, string key, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(Guid.NewGuid().ToString("N"));
        public Task<bool> SaveAsync(string principal, string key, string owner, StoredResponse response, CancellationToken cancellationToken) {
            responses[(principal, key)] = response;
            return Task.FromResult(true);
        }
    }

    private sealed class ThrowingAuditWriter : IAuditWriter {
        public void Write(AuditEvent auditEvent) => throw new InvalidOperationException("Audit store is unavailable.");
    }

    private sealed class StubLlm : ILlmClient {
        public Task<LlmReply> Chat(string model, IReadOnlyList<LlmMessage> messages, CancellationToken ct) =>
            Task.FromResult(new LlmReply("stub reply", 3, 2));
        public Task<IReadOnlyList<string>> ListModels(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(SyncedModelNames);
    }

    private sealed class AllowLimiter : IAiRateLimiter {
        public Task<RateLimitDecision> Check(Guid userId, int minute, int day, CancellationToken ct) =>
            Task.FromResult(new RateLimitDecision(true, minute - 1, day - 1));
    }

    /// <summary>SQLite has no text[] or DateTimeOffset ordering: store arrays as text and order-sensitive timestamps as binary.</summary>
    private sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ApiKey>().Property(key => key.Scopes)
                .HasConversion(new ValueConverter<IReadOnlyList<string>, string>(
                    scopes => string.Join(",", scopes),
                    text => text.Split(',', StringSplitOptions.RemoveEmptyEntries)))
                .HasColumnType("TEXT");
            modelBuilder.Entity<Policy>().Property(policy => policy.AppliesToTiers)
                .HasConversion(new ValueConverter<IReadOnlyList<EnvironmentTier>, string>(
                    tiers => string.Join(",", tiers),
                    text => text.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(tier => Enum.Parse<EnvironmentTier>(tier)).ToArray()))
                .HasColumnType("TEXT");
            modelBuilder.Entity<Artifact>().Property(item => item.CreatedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<Deployment>().Property(item => item.RequestedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<DeploymentEvent>().Property(item => item.Timestamp).HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<PolicyEvaluation>().Property(item => item.EvaluatedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<AiRequest>().Property(item => item.CreatedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
        }
    }
}
