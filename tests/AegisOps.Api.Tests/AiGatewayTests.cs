using AegisOps.Infrastructure.Ai;
using AegisOps.Infrastructure.Audit;
using AegisOps.Domain.Ai;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Persistence;
using AegisOps.Infrastructure.Observability;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Security;
using AegisOps.Api.Ai;
using AegisOps.Api.Identity;
using AegisOps.Application.Authorization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics.Metrics;
using StackExchange.Redis;

namespace AegisOps.Api.Tests;

public sealed class AiGatewayTests {
    [Fact]
    public async Task Gateway_emits_committed_blocked_request_and_sensitive_detection_without_identity_labels() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("metrics-model"); model.SetEnabled(true);
        var policy = AiPolicy.Create("metrics-policy", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Block, [], false, false, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var events = new List<(string Name, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener {
            InstrumentPublished = (instrument, current) => {
                if (instrument.Meter.Name == "AegisOps.Ai") current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
            if (value == 0) return;
            Assert.Equal(1, value);
            events.Add((instrument.Name, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
        });
        listener.Start();
        AiMetrics.InitializeZeroSeries();

        var fake = new FakeLlm();
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));
        var result = await gateway.Chat(fixture.UserId, null, model.Name,
            [new LlmMessage("user", "AKIA1234567890123456")], "metrics", TestContext.Current.CancellationToken);

        Assert.Equal(422, result.StatusCode);
        Assert.Equal(0, fake.Calls);
        Assert.Single(await fixture.Db.AiRequests.ToArrayAsync(TestContext.Current.CancellationToken));
        var request = Assert.Single(events, e => e.Name == "aegisops_ai_requests_total");
        Assert.Equal("Blocked", request.Tags["status"]);
        Assert.Equal("SensitiveData", request.Tags["reason"]);
        Assert.Equal(2, request.Tags.Count);
        var detection = Assert.Single(events, e => e.Name == "aegisops_ai_sensitive_detections_total");
        Assert.Equal("AWS_ACCESS_KEY", detection.Tags["type"]);
        Assert.Equal("Block", detection.Tags["policy_action"]);
        Assert.DoesNotContain("action", detection.Tags);
        Assert.Equal(2, detection.Tags.Count);
    }

    [Fact]
    public async Task Gateway_does_not_attribute_a_sensitive_detection_to_a_missing_policy() {
        await using var fixture = new GatewayFixture();
        var detections = new List<Dictionary<string, object?>>();
        using var listener = new MeterListener {
            InstrumentPublished = (instrument, current) => {
                if (instrument.Meter.Name == "AegisOps.Ai" && instrument.Name == "aegisops_ai_sensitive_detections_total")
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
            if (value != 0) detections.Add(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value));
        });
        listener.Start();
        AiMetrics.InitializeZeroSeries();

        var gateway = new AiGateway(fixture.Db, new FakeLlm(), new FakeLimiter(),
            new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));
        var result = await gateway.Chat(fixture.UserId, null, null,
            [new LlmMessage("user", "contact sample@example.invalid")], "metrics", TestContext.Current.CancellationToken);

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("EMAIL", Assert.Single(detections)["type"]);
        Assert.Equal("NoPolicy", detections[0]["policy_action"]);
        Assert.DoesNotContain("action", detections[0]);
    }

    [Theory]
    [InlineData("Admin", "/policies", "GET", 200)]
    [InlineData("Developer", "/policies", "PUT", 403)]
    [InlineData("Admin", "/policies", "PUT", 200)]
    [InlineData("Security", "/policies", "PUT", 200)]
    [InlineData("Developer", "/models/sync", "POST", 403)]
    [InlineData("Admin", "/models/sync", "POST", 200)]
    public async Task Ai_management_routes_enforce_permission_policies(string role, string path, string method, int expected) {
        await using var fixture = new GatewayFixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, AiTestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options => {
            options.AddPolicy("ai:policies:read", policy => policy.Requirements.Add(new PermissionRequirement(Permissions.AiPoliciesWrite)));
            foreach (var permission in Permissions.All)
                options.AddPolicy(permission, policy => policy.Requirements.Add(new PermissionRequirement(permission)));
        });
        builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        builder.Services.AddSingleton(fixture.Db);
        builder.Services.AddSingleton(fixture.Users);
        builder.Services.AddSingleton<ILlmClient>(new FakeLlm());
        builder.Services.AddSingleton<AiGateway>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/policies", (AegisOpsDbContext db, HttpContext http) => AiEndpoints.Policies(db, http)).RequireAuthorization("ai:policies:read");
        app.MapPut("/policies", (AiPolicyRequest body, ClaimsPrincipal user, UserManager<User> users, AegisOpsDbContext db, HttpContext http) =>
            AiEndpoints.SavePolicy(body, user, users, db, new EfAuditWriter(db), http)).RequireAuthorization(Permissions.AiPoliciesWrite);
        app.MapPost("/models/sync", (ILlmClient llm, ClaimsPrincipal user, UserManager<User> users, AegisOpsDbContext db, HttpContext http) =>
            AiEndpoints.SyncModels(llm, user, users, db, new EfAuditWriter(db), http)).RequireAuthorization(Permissions.ModelsManage);
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-Test-Role", role);
        if (method == "PUT") {
            var scope = role == "Security" ? PolicyScope.Team : PolicyScope.Global;
            var requestTeam = role == "Security" ? Team.Create("HTTP security team", $"ai-{Guid.NewGuid():N}", DateTimeOffset.UtcNow) : null;
            if (requestTeam is not null) {
                fixture.Db.Teams.Add(requestTeam);
                await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
            request.Content = JsonContent.Create(new AiPolicyRequest(null, "policy", scope, requestTeam?.Id, [],
                20, 100, 4000, SensitiveDataAction.Redact, [], true, false, true));
            if (role is "Admin" or "Security") {
                var dbRole = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = role, NormalizedName = role.ToUpperInvariant() };
                fixture.Db.Roles.Add(dbRole);
                fixture.Db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = fixture.UserId, RoleId = dbRole.Id });
                await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
                request.Headers.Add("X-Test-User", fixture.UserId.ToString());
            }
        }
        // Model sync is audited with the signed-in actor, so the caller must resolve to a real user.
        if (path == "/models/sync") request.Headers.Add("X-Test-User", fixture.UserId.ToString());
        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, (int)response.StatusCode);
        if (expected == 200 && path == "/models/sync") {
            Assert.Contains("added", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var audit = Assert.Single(await fixture.Db.AuditEvents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(("ai.model_synced", (Guid?)fixture.UserId), (audit.Action, audit.ActorId));
        }
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Save_policy_rejects_scope_changes_and_unknown_models_without_mutating_existing_policy() {
        await using var fixture = new GatewayFixture();
        var policy = AiPolicy.Create("original", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([], 20, 100, 4000, SensitiveDataAction.Redact, [], true, false, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiPolicies.Add(policy);
        var securityRole = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "Security", NormalizedName = "SECURITY" };
        fixture.Db.Roles.Add(securityRole);
        fixture.Db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = fixture.UserId, RoleId = securityRole.Id });
        var team = Team.Create("Scope test", $"scope-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        fixture.Db.Teams.Add(team);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", fixture.UserId.ToString())], "test"));
        var http = new DefaultHttpContext();

        var changedScope = await AiEndpoints.SavePolicy(new AiPolicyRequest(policy.Id, "renamed", PolicyScope.Team,
            team.Id, [], 20, 100, 4000, SensitiveDataAction.Redact, [], true, false, true),
            principal, fixture.Users, fixture.Db, new EfAuditWriter(fixture.Db), http);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(changedScope).StatusCode);
        Assert.Equal("original", policy.Name);

        var unknownModel = await AiEndpoints.SavePolicy(new AiPolicyRequest(null, "new", PolicyScope.Team, team.Id,
            [Guid.NewGuid()], 20, 100, 4000, SensitiveDataAction.Redact, [], true, false, true),
            principal, fixture.Users, fixture.Db, new EfAuditWriter(fixture.Db), http);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(unknownModel).StatusCode);
        Assert.Equal(1, await fixture.Db.AiPolicies.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_policy_rejects_security_from_global_scope() {
        await using var fixture = new GatewayFixture();
        var securityRole = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "Security", NormalizedName = "SECURITY" };
        fixture.Db.Roles.Add(securityRole);
        fixture.Db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = fixture.UserId, RoleId = securityRole.Id });
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", fixture.UserId.ToString())], "test"));

        var result = await AiEndpoints.SavePolicy(new AiPolicyRequest(null, "global", PolicyScope.Global, null, [],
            20, 100, 4000, SensitiveDataAction.Redact, [], true, false, true),
            principal, fixture.Users, fixture.Db, new EfAuditWriter(fixture.Db), new DefaultHttpContext());

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>(result);
        Assert.Empty(await fixture.Db.AiPolicies.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_policy_rejects_a_second_enabled_global_policy() {
        await using var fixture = new GatewayFixture();
        var adminRole = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "Admin", NormalizedName = "ADMIN" };
        fixture.Db.Roles.Add(adminRole);
        fixture.Db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = fixture.UserId, RoleId = adminRole.Id });
        var existing = AiPolicy.Create("existing", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiPolicies.Add(existing);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", fixture.UserId.ToString())], "test"));

        var result = await AiEndpoints.SavePolicy(new AiPolicyRequest(null, "second", PolicyScope.Global, null, [],
            20, 100, 4000, SensitiveDataAction.Redact, [], true, false, true),
            principal, fixture.Users, fixture.Db, new EfAuditWriter(fixture.Db), new DefaultHttpContext());

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal([existing.Id], (await fixture.Db.AiPolicies.ToListAsync(TestContext.Current.CancellationToken)).Select(policy => policy.Id));
    }

    [Fact]
    public async Task Database_rejects_a_second_enabled_global_ai_policy() {
        await using var fixture = new GatewayFixture();
        fixture.Db.AiPolicies.Add(AiPolicy.Create("first", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Db.AiPolicies.Add(AiPolicy.Create("second", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("", PolicyScope.Global, null, 20, 100, 4000)]
    [InlineData("bad-team", PolicyScope.Team, null, 20, 100, 4000)]
    [InlineData("bad-limit", PolicyScope.Global, null, 0, 100, 4000)]
    [InlineData("bad-pattern", PolicyScope.Global, null, 20, 100, 4000)]
    public async Task Save_policy_rejects_invalid_name_scope_or_controls(string scenario, PolicyScope scope, Guid? scopeId,
        int perMinute, int perDay, int maxChars) {
        await using var fixture = new GatewayFixture();
        var roleName = scenario == "bad-team" ? "Security" : "Admin";
        var role = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = roleName, NormalizedName = roleName.ToUpperInvariant() };
        fixture.Db.Roles.Add(role);
        fixture.Db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = fixture.UserId, RoleId = role.Id });
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", fixture.UserId.ToString())], "test"));
        var patterns = scenario == "bad-pattern" ? new[] { new string('x', 257) } : Array.Empty<string>();

        var result = await AiEndpoints.SavePolicy(new AiPolicyRequest(null, scenario == "" ? "" : "valid", scope, scopeId,
            [], perMinute, perDay, maxChars, SensitiveDataAction.Redact, patterns, true, false, true),
            principal, fixture.Users, fixture.Db, new EfAuditWriter(fixture.Db), new DefaultHttpContext());

        if (scenario == "bad-team") Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        else Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Empty(await fixture.Db.AiPolicies.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_policy_rejects_null_model_or_pattern_arrays_without_throwing() {
        await using var fixture = new GatewayFixture();

        var result = await AiEndpoints.SavePolicy(new AiPolicyRequest(null, "valid", PolicyScope.Global, null,
            null!, 20, 100, 4000, SensitiveDataAction.Redact, null!, true, false, true),
            new ClaimsPrincipal(), null!, fixture.Db, new EfAuditWriter(fixture.Db), new DefaultHttpContext());

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Empty(await fixture.Db.AiPolicies.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Chat_http_endpoint_returns_redacted_provider_response() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("chat-response-redaction"); model.SetEnabled(true);
        var policy = AiPolicy.Create("chat-response-policy", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Allow, [], false, true, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        const string secret = "api_key=abcdefghijklmnopqrstuv";
        var fake = new FakeLlm { Reply = "provider echoed " + secret };
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, AiTestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy(Permissions.AiChat,
            policy => policy.Requirements.Add(new PermissionRequirement(Permissions.AiChat))));
        builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        builder.Services.AddSingleton(fixture.Db);
        builder.Services.AddSingleton(fixture.Users);
        builder.Services.AddSingleton(gateway);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPost("/chat", (AiChatRequest body, ClaimsPrincipal user, UserManager<User> users,
            AegisOpsDbContext db, AiGateway ai, HttpContext http) => AiEndpoints.Chat(body, user, users, db, ai, http))
            .RequireAuthorization(Permissions.AiChat);
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/chat") {
            Content = JsonContent.Create(new AiChatRequest(model.Name, null, [new LlmMessage("user", "hello")]))
        };
        request.Headers.Add("X-Test-Role", "Developer");
        request.Headers.Add("X-Test-User", fixture.UserId.ToString());
        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        var bodyText = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(secret, bodyText);
        Assert.Contains("[REDACTED:GENERIC_API_KEY]", bodyText);

        using var deniedRequest = new HttpRequestMessage(HttpMethod.Post, "/chat") {
            Content = JsonContent.Create(new AiChatRequest(model.Name, null, [new LlmMessage("user", "hello")]))
        };
        deniedRequest.Headers.Add("X-Test-Role", "Guest");
        deniedRequest.Headers.Add("X-Test-User", fixture.UserId.ToString());
        var denied = await app.GetTestClient().SendAsync(deniedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Finding_explanation_uses_governed_gateway_and_records_findings_purpose() {
        await using var fixture = new GatewayFixture();
        var (finding, model, _) = await SeedAiFinding(fixture);
        var fake = new FakeLlm { Reply = "Upgrade the package to the fixed version." };
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", fixture.UserId.ToString())], "test"));
        var http = new DefaultHttpContext();

        var result = await AiEndpoints.ExplainFinding(finding.Id, new AiFeatureRequest(null), principal,
            fixture.Users, fixture.Db, gateway, http);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains("AI-generated", JsonSerializer.Serialize(value));
        Assert.Contains(finding.Title, string.Join("\n", fake.LastMessages.Select(message => message.Content)));
        Assert.Equal(model.Name, fake.LastModel);
        var audit = await fixture.Db.AiRequests.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AiPurpose.FindingsExplanation, audit.Purpose);
        Assert.Equal(fake.Reply, audit.ResponsePreview);
    }

    [Fact]
    public async Task Finding_explanation_does_not_disclose_findings_outside_members_team() {
        await using var fixture = new GatewayFixture();
        var (finding, _, _) = await SeedAiFinding(fixture, addMember: false);
        var fake = new FakeLlm();
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", fixture.UserId.ToString())], "test"));

        var result = await AiEndpoints.ExplainFinding(finding.Id, new AiFeatureRequest(null), principal,
            fixture.Users, fixture.Db, gateway, new DefaultHttpContext());

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(0, fake.Calls);
        Assert.Empty(await fixture.Db.AiRequests.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deployment_summary_uses_governed_gateway_and_records_deployment_purpose() {
        await using var fixture = new GatewayFixture();
        var deployment = await SeedAiDeployment(fixture);
        var fake = new FakeLlm { Reply = "Review the high-severity dependency before approving." };
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", fixture.UserId.ToString())], "test"));

        var result = await AiEndpoints.SummarizeDeployment(deployment.Id, new AiFeatureRequest(null), principal,
            fixture.Users, fixture.Db, gateway, new DefaultHttpContext());
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains("AI-generated", JsonSerializer.Serialize(value));
        var prompt = string.Join("\n", fake.LastMessages.Select(message => message.Content));
        Assert.Contains("1.2.3", prompt);
        Assert.Contains("Outdated vulnerable package", prompt);
        Assert.Equal("finding-explanation-model", fake.LastModel);
        var audit = await fixture.Db.AiRequests.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AiPurpose.DeploymentSummary, audit.Purpose);
    }

    [Fact]
    public async Task Deployment_summary_requires_awaiting_approval_before_calling_model() {
        await using var fixture = new GatewayFixture();
        var deployment = await SeedAiDeployment(fixture, awaitingApproval: false);
        var fake = new FakeLlm();
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", fixture.UserId.ToString())], "test"));

        var result = await AiEndpoints.SummarizeDeployment(deployment.Id, new AiFeatureRequest(null), principal,
            fixture.Users, fixture.Db, gateway, new DefaultHttpContext());

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(0, fake.Calls);
        Assert.Empty(await fixture.Db.AiRequests.ToListAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<(SecurityFinding Finding, AiModel Model, Team Team)> SeedAiFinding(GatewayFixture fixture, bool addMember = true) {
        var now = DateTimeOffset.UtcNow;
        var team = Team.Create("AI finding team", $"ai-finding-{Guid.NewGuid():N}", now);
        var project = Project.Create(team.Id, "AI finding project", $"ai-finding-{Guid.NewGuid():N}", now);
        var artifact = Artifact.Create(project.Id, "1.2.3", new string('a', 40), "main", "registry/app:1.2.3",
            CheckStatus.Passed, CheckStatus.Passed, now, createdById: fixture.UserId);
        var scan = SecurityScan.Begin(artifact.Id, ScannerType.Trivy, ScanSource.CiUploaded, ReportFormat.TrivyJson,
            now, "test", "test-report.json", parseInline: true);
        scan.Complete(ScanSummary.Empty(), now);
        var finding = SecurityFinding.Create(scan.Id, artifact.Id, Severity.High, "CVE-2026-1234",
            "Outdated vulnerable package", "finding-fingerprint", description: "Upgrade to the fixed package release.",
            packageName: "example-library", installedVersion: "1.0.0", fixedVersion: "1.0.1");
        var model = AiModel.Create("finding-explanation-model");
        model.SetEnabled(true);
        var policy = AiPolicy.Create("finding-explanation-policy", PolicyScope.Global, null, fixture.UserId, now);
        policy.Configure([model.Id], 20, 100, 16000, SensitiveDataAction.Redact, [], true, false, true, fixture.UserId, now);
        fixture.Db.Teams.Add(team);
        fixture.Db.Projects.Add(project);
        fixture.Db.Artifacts.Add(artifact);
        fixture.Db.SecurityScans.Add(scan);
        fixture.Db.SecurityFindings.Add(finding);
        if (addMember) fixture.Db.TeamMembers.Add(TeamMember.Create(team.Id, fixture.UserId, TeamRole.Member, now));
        fixture.Db.AiModels.Add(model);
        fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (finding, model, team);
    }

    private static async Task<Deployment> SeedAiDeployment(GatewayFixture fixture, bool awaitingApproval = true) {
        var (finding, _, _) = await SeedAiFinding(fixture);
        var project = await fixture.Db.Projects.SingleAsync(TestContext.Current.CancellationToken);
        var artifact = await fixture.Db.Artifacts.SingleAsync(item => item.Id == finding.ArtifactId, TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var environment = AegisOps.Domain.Organization.Environment.Create(project.Id, "Production", EnvironmentTier.Production, 1);
        var deployment = Deployment.Request(project.Id, environment.Id, artifact.Id, now, "ai-summary-test", fixture.UserId,
            null, "Release 1.2.3 for approval.");
        var role = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "Developer", NormalizedName = "DEVELOPER" };
        fixture.Db.Roles.Add(role);
        fixture.Db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = fixture.UserId, RoleId = role.Id });
        fixture.Db.Environments.Add(environment);
        fixture.Db.Deployments.Add(deployment);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        if (!awaitingApproval) return deployment;

        var evaluation = PolicyEvaluation.Create(deployment.Id, PolicyDecision.RequireApproval, 1, now,
            "[{\"Type\":\"RequireApprovals\",\"Passed\":true},{\"Type\":\"MaxFindings\",\"Passed\":false,\"Message\":\"High severity finding present.\"}]");
        fixture.Db.PolicyEvaluations.Add(evaluation);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        deployment.BeginEvaluation();
        deployment.ApplyDecision(new PolicyOutcome(PolicyDecision.RequireApproval, evaluation.Id, 1, null), now);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return deployment;
    }

    [Fact]
    public async Task Request_history_is_scoped_to_the_authenticated_user_unless_admin_or_security() {
        await using var fixture = new GatewayFixture();
        var otherUser = User.Create("other-ai@example.test", "Other", DateTimeOffset.UtcNow);
        fixture.Db.Users.Add(otherUser);
        fixture.Db.AiRequests.Add(AiRequest.Record(otherUser.Id, null, null, null, AiRequestStatus.Completed,
            null, "private prompt", "private prompt", null, [], 0, null, null, 0, "other", DateTimeOffset.UtcNow));
        fixture.Db.AiRequests.Add(AiRequest.Record(fixture.UserId, null, null, null, AiRequestStatus.Completed,
            null, "own prompt", "own prompt", null, [], 0, null, null, 0, "own", DateTimeOffset.UtcNow));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var userManager = fixture.Users;
        var context = new DefaultHttpContext();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", fixture.UserId.ToString())], "test"));

        var result = await AiEndpoints.Requests(principal, userManager, fixture.Db, context);

        var json = JsonSerializer.Serialize(result, result.GetType());
        Assert.Contains("own", json);
        Assert.DoesNotContain("other", json);
    }

    [Fact]
    public async Task Request_history_is_rejected_for_inactive_users() {
        await using var fixture = new GatewayFixture();
        fixture.Db.Users.Single().Deactived();
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", fixture.UserId.ToString())], "test"));

        var result = await AiEndpoints.Requests(principal, fixture.Users, fixture.Db, new DefaultHttpContext());

        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Chat_endpoint_rejects_principal_without_valid_subject_before_accessing_services() {
        var result = await AiEndpoints.Chat(new AiChatRequest(null, null, []), new ClaimsPrincipal(), null!, null!, null!, new DefaultHttpContext());
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Ollama_client_uses_chat_and_tags_contracts_without_streaming() {
        var requests = new List<HttpRequestMessage>();
        using var client = new HttpClient(new StubHandler(async (request, ct) => {
            requests.Add(request);
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/tags", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { models = new[] { new { name = "llama3.2:3b" } } }) };
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = JsonContent.Create(new { message = new { content = "safe reply" }, prompt_eval_count = 7, eval_count = 9 })
            };
        })) { BaseAddress = new Uri("http://ollama.test/") };
        var llm = new OllamaLlmClient(client);

        var models = await llm.ListModels(TestContext.Current.CancellationToken);
        var reply = await llm.Chat("llama3.2:3b", [new LlmMessage("user", "hello")], TestContext.Current.CancellationToken);

        Assert.Equal(["llama3.2:3b"], models);
        Assert.Equal("safe reply", reply.Content);
        Assert.Equal(7, reply.PromptTokens);
        Assert.Equal(9, reply.CompletionTokens);
        Assert.EndsWith("/api/tags", requests[0].RequestUri!.AbsolutePath);
        Assert.EndsWith("/api/chat", requests[1].RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(await requests[1].Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
    }

    [Theory]
    [InlineData("Redact", 200, "[REDACTED:AWS_ACCESS_KEY]", "Completed")]
    [InlineData("Block", 422, "", "Blocked")]
    public async Task Gateway_enforces_sensitive_policy_and_minimizes_audit(string action, int status, string sent, string recorded) {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("llama3.2:3b"); model.SetEnabled(true);
        var policy = AiPolicy.Create("baseline", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, Enum.Parse<SensitiveDataAction>(action), [], false, false, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var fake = new FakeLlm();
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));
        const string secret = "AKIA1234567890123456";
        var result = await gateway.Chat(fixture.UserId, null, model.Name, [new LlmMessage("user", "hello " + secret)], "correlation", TestContext.Current.CancellationToken);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(recorded == "Completed" ? 1 : 0, fake.Calls);
        if (fake.Calls > 0) Assert.Contains(sent, fake.LastPrompt);
        var row = await fixture.Db.AiRequests.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(recorded, row.Status.ToString());
        Assert.DoesNotContain(secret, row.PromptPreview);
        Assert.Null(row.PromptContent);
        Assert.Equal(64, row.PromptHash.Length);
        Assert.Single(await fixture.Db.AuditEvents.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Gateway_logs_rate_limit_and_disallowed_model_without_calling_llm() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("llama3.2:3b"); model.SetEnabled(true);
        var policy = AiPolicy.Create("baseline", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Redact, [], false, false, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy); await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var fake = new FakeLlm(); var limiter = new FakeLimiter { Allow = false };
        var gateway = new AiGateway(fixture.Db, fake, limiter, new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));
        Assert.Equal(429, (await gateway.Chat(fixture.UserId, null, model.Name, [new LlmMessage("user", "hi")], "a", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(403, (await gateway.Chat(fixture.UserId, null, "other", [new LlmMessage("user", "hi")], "b", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(0, fake.Calls);
        Assert.Equal(2, await fixture.Db.AiRequests.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Gateway_redacts_sensitive_values_from_provider_response_before_returning_and_auditing() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("response-redaction"); model.SetEnabled(true);
        var policy = AiPolicy.Create("response-policy", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Allow, [], false, true, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        const string secret = "AKIA1234567890ABCDEF";
        var fake = new FakeLlm { Reply = "provider echoed " + secret };
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));

        var result = await gateway.Chat(fixture.UserId, null, model.Name, [new LlmMessage("user", "hello")],
            "response-redaction", TestContext.Current.CancellationToken);

        Assert.Equal(200, result.StatusCode);
        Assert.DoesNotContain(secret, result.Content);
        Assert.Contains("[REDACTED:AWS_ACCESS_KEY]", result.Content);
        var row = await fixture.Db.AiRequests.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Contains("[REDACTED:AWS_ACCESS_KEY]", row.ResponsePreview);
        Assert.DoesNotContain(secret, row.ResponsePreview);
        Assert.DoesNotContain(secret, row.ResponseContent);
    }

    [Fact]
    public async Task Stored_prompt_content_is_the_redacted_prompt_never_the_raw_secret() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("content-redaction"); model.SetEnabled(true);
        var policy = AiPolicy.Create("content-policy", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Redact, [], false, true, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        const string secret = "AKIA1234567890ABCDEF";
        var gateway = new AiGateway(fixture.Db, new FakeLlm(), new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));

        var result = await gateway.Chat(fixture.UserId, null, model.Name, [new LlmMessage("user", "deploy failed with key " + secret)],
            "content-redaction", TestContext.Current.CancellationToken);

        Assert.Equal(200, result.StatusCode);
        var row = await fixture.Db.AiRequests.SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(row.PromptContent);
        Assert.Contains("[REDACTED:AWS_ACCESS_KEY]", row.PromptContent);
        Assert.DoesNotContain(secret, row.PromptContent);
    }

    [Fact]
    public async Task Gateway_records_generic_failure_when_provider_throws_without_exposing_exception() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("provider-failure"); model.SetEnabled(true);
        var policy = AiPolicy.Create("provider-failure-policy", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Redact, [], false, false, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var fake = new FakeLlm { Failure = new HttpRequestException("private endpoint detail") };
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));

        var result = await gateway.Chat(fixture.UserId, null, model.Name, [new LlmMessage("user", "hello")],
            "provider-failure", TestContext.Current.CancellationToken);

        Assert.Equal(503, result.StatusCode);
        Assert.Null(result.Content);
        Assert.Equal(AiRequestStatus.Failed, result.Request.Status);
        Assert.Equal("ProviderUnavailable", result.Request.BlockReason);
        Assert.DoesNotContain("private endpoint detail", JsonSerializer.Serialize(result.Request));
    }

    [Fact]
    public async Task Gateway_propagates_provider_cancellation_without_fabricating_failure_audit() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("provider-cancel"); model.SetEnabled(true);
        var policy = AiPolicy.Create("provider-cancel-policy", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Redact, [], false, false, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var fake = new FakeLlm { Failure = new OperationCanceledException() };
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.Chat(fixture.UserId, null, model.Name,
            [new LlmMessage("user", "hello")], "provider-cancel", TestContext.Current.CancellationToken));

        Assert.Empty(await fixture.Db.AiRequests.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Gateway_rejects_null_message_items_as_prompt_errors() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("null-message-item"); model.SetEnabled(true);
        var policy = AiPolicy.Create("null-message-item-policy", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Redact, [], false, false, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var fake = new FakeLlm();
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));

        var result = await gateway.Chat(fixture.UserId, null, model.Name, [null!], "null-message", TestContext.Current.CancellationToken);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("PromptRejected", result.Request.BlockReason);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task Gateway_turns_sensitive_inspection_timeout_into_generic_failure_audit() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("inspection-timeout"); model.SetEnabled(true);
        var policy = AiPolicy.Create("inspection-timeout-policy", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Redact, [], false, false, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var fake = new FakeLlm();
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new TimeoutDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));

        var result = await gateway.Chat(fixture.UserId, null, model.Name, [new LlmMessage("user", "hello")],
            "inspection-timeout", TestContext.Current.CancellationToken);

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("InspectionUnavailable", result.Request.BlockReason);
        Assert.Equal(AiRequestStatus.Failed, result.Request.Status);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task Gateway_does_not_return_success_when_audit_persistence_fails() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("audit-store-failure"); model.SetEnabled(true);
        var policy = AiPolicy.Create("audit-store-failure-policy", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Redact, [], false, false, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await fixture.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_ai_audit BEFORE INSERT ON ai_requests BEGIN SELECT RAISE(ABORT, 'audit unavailable'); END",
            TestContext.Current.CancellationToken);
        var fake = new FakeLlm();
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => gateway.Chat(fixture.UserId, null, model.Name,
            [new LlmMessage("user", "hello")], "audit-store-failure", TestContext.Current.CancellationToken));

        Assert.Equal(1, fake.Calls);
        Assert.Empty(await fixture.Db.AiRequests.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Db.AuditEvents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Gateway_rejects_oversized_prompt_before_sensitive_inspection_and_persists_only_bounded_preview() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("oversized-prompt"); model.SetEnabled(true);
        var policy = AiPolicy.Create("oversized-prompt-policy", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 32, SensitiveDataAction.Redact, [], false, true, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var detector = new TrackingDetector();
        var gateway = new AiGateway(fixture.Db, new FakeLlm(), new FakeLimiter(), detector, TimeProvider.System, new EfAuditWriter(fixture.Db));
        var oversized = new[] { new string('a', 4095) + "🌐" + new string('a', 45_903), new string('b', 50_000) };
        var joinedPrompt = string.Join("\n", oversized);

        var result = await gateway.Chat(fixture.UserId, null, model.Name,
            [new LlmMessage("user", oversized[0]), new LlmMessage("assistant", oversized[1])],
            "oversized-prompt", TestContext.Current.CancellationToken);

        Assert.Equal(400, result.StatusCode);
        Assert.False(detector.WasCalled);
        Assert.Null(result.Request.PromptContent);
        Assert.InRange(result.Request.PromptPreview?.Length ?? 0, 0, 500);
        Assert.Equal(joinedPrompt.Length, result.Request.PromptChars);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joinedPrompt))),
            result.Request.PromptHash);
    }

    [Fact]
    public async Task Gateway_audits_timeout_while_redacting_forwarded_messages() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("message-redaction-timeout"); model.SetEnabled(true);
        var policy = AiPolicy.Create("message-redaction-timeout-policy", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Redact, [], false, false, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var fake = new FakeLlm();
        var detector = new FailOnSecondInspectionDetector();
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), detector, TimeProvider.System, new EfAuditWriter(fixture.Db));

        var result = await gateway.Chat(fixture.UserId, null, model.Name, [new LlmMessage("user", "hello")],
            "message-redaction-timeout", TestContext.Current.CancellationToken);

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("InspectionUnavailable", result.Request.BlockReason);
        Assert.Equal(AiRequestStatus.Failed, result.Request.Status);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task Gateway_reports_response_inspection_timeout_without_returning_or_persisting_reply() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("response-inspection-timeout"); model.SetEnabled(true);
        var policy = AiPolicy.Create("response-inspection-timeout-policy", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Allow, [], false, true, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var fake = new FakeLlm { Reply = "potentially sensitive reply" };
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new FailOnSecondInspectionDetector(), TimeProvider.System, new EfAuditWriter(fixture.Db));

        var result = await gateway.Chat(fixture.UserId, null, model.Name, [new LlmMessage("user", "hello")],
            "response-inspection-timeout", TestContext.Current.CancellationToken);

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("InspectionUnavailable", result.Request.BlockReason);
        Assert.Equal(AiRequestStatus.Failed, result.Request.Status);
        Assert.Null(result.Content);
        Assert.Null(result.Request.ResponseContent);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Redis_rate_limiter_atomically_enforces_both_windows_without_consuming_denied_calls() {
        var connection = System.Environment.GetEnvironmentVariable("AEGISOPS_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(connection)) Assert.Skip("Set AEGISOPS_TEST_REDIS to run the Redis integration test.");
        using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
        var userId = Guid.NewGuid();
        var limiter = new RedisAiRateLimiter(redis, TimeProvider.System);
        try {
            var decisions = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
                limiter.Check(userId, 3, 5, TestContext.Current.CancellationToken)));

            Assert.Equal(3, decisions.Count(decision => decision.Allowed));
            var denied = decisions.Where(decision => !decision.Allowed).ToArray();
            Assert.Equal(13, denied.Length);
            Assert.All(denied, decision => {
                Assert.Equal(0, decision.RemainingMinute);
                Assert.Equal(2, decision.RemainingDay);
            });
        } finally {
            var database = redis.GetDatabase();
            await database.KeyDeleteAsync($"ai:rl:m:{userId:N}");
            await database.KeyDeleteAsync($"ai:rl:d:{userId:N}");
        }
    }

    private sealed class FakeLlm : ILlmClient {
        public int Calls;
        public string LastPrompt = "";
        public string LastModel = "";
        public IReadOnlyList<LlmMessage> LastMessages = [];
        public string Reply = "response";
        public Exception? Failure;
        public Task<LlmReply> Chat(string model, IReadOnlyList<LlmMessage> messages, CancellationToken ct) {
            if (Failure is not null) return Task.FromException<LlmReply>(Failure);
            Calls++; LastPrompt = messages[0].Content; LastModel = model; LastMessages = messages;
            return Task.FromResult(new LlmReply(Reply, 2, 3));
        }
        public Task<IReadOnlyList<string>> ListModels(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["llama3.2:3b"]);
    }
    private sealed class FakeLimiter : IAiRateLimiter {
        public bool Allow = true;
        public Task<RateLimitDecision> Check(Guid userId, int minute, int day, CancellationToken ct) =>
            Task.FromResult(new RateLimitDecision(Allow, Allow ? 19 : 0, Allow ? 499 : 0));
    }
    private sealed class FailOnSecondInspectionDetector : ISensitiveDataDetector {
        private int calls;
        public SensitiveInspection Inspect(string input, bool redactPii = false) {
            if (++calls == 2) throw new System.Text.RegularExpressions.RegexMatchTimeoutException();
            return new SensitiveInspection(input, [], 0);
        }
    }
    private sealed class TimeoutDetector : ISensitiveDataDetector {
        public SensitiveInspection Inspect(string input, bool redactPii = false) =>
            throw new System.Text.RegularExpressions.RegexMatchTimeoutException();
    }
    private sealed class TrackingDetector : ISensitiveDataDetector {
        public bool WasCalled { get; private set; }
        public SensitiveInspection Inspect(string input, bool redactPii = false) {
            WasCalled = true;
            return new SensitiveInspection(input, [], 0);
        }
    }
    private sealed class AiTestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder) {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (string.IsNullOrWhiteSpace(role)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new("role", role) };
            if (Request.Headers.TryGetValue("X-Test-User", out var userId)) claims.Add(new Claim("sub", userId.ToString()));
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class GatewayFixture : IAsyncDisposable {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly ServiceProvider services;
        public AegisOpsDbContext Db { get; }
        public UserManager<User> Users { get; }
        public Guid UserId { get; } = Guid.NewGuid();
        public GatewayFixture() {
            connection.Open();
            services = new ServiceCollection().AddLogging()
                .AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection)
                    .UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteCustomizer>())
                .AddIdentityCore<User>().AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AegisOpsDbContext>()
                .Services.BuildServiceProvider();
            Db = services.GetRequiredService<AegisOpsDbContext>();
            Users = services.GetRequiredService<UserManager<User>>();
            Db.Database.EnsureCreated();
            Db.Users.Add(User.Create($"ai-{UserId:N}@example.test", "AI tester", DateTimeOffset.UtcNow));
            // Use the generated Identity user key for FK integrity.
            UserId = Db.Users.Local.Single().Id;
            Db.SaveChanges();
        }
        public async ValueTask DisposeAsync() { await services.DisposeAsync(); await connection.DisposeAsync(); }
    }

    private sealed class SqliteCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<AegisOps.Domain.Identity.ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<AegisOps.Domain.Policy.Policy>().Ignore(policy => policy.AppliesToTiers);
            modelBuilder.Entity<AiRequest>().Property(item => item.CreatedAt)
                .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<Deployment>().Property(item => item.RequestedAt)
                .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<DeploymentEvent>().Property(item => item.Timestamp)
                .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
        }
    }

    [Theory]
    [InlineData("AKIA1234567890123456", "AWS_ACCESS_KEY")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", "GITHUB_TOKEN")]
    [InlineData("my id 900101-14-1234", "MY_NRIC")]
    [InlineData("contact +60123456789", "PHONE_MY")]
    [InlineData("server 192.168.1.12", "IPV4_PRIVATE")]
    [InlineData("4111 1111 1111 1111", "CREDIT_CARD")]
    public void Detector_redacts_sensitive_spans(string text, string type) {
        var result = new RegexSensitiveDataDetector().Inspect(text, redactPii: true);
        Assert.Contains(type, result.Types);
        Assert.DoesNotContain(text, result.Redacted);
        Assert.Contains("[REDACTED:" + type + "]", result.Redacted);
    }
}
