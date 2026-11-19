using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisOps.Api.Deploy;
using AegisOps.Api.Integrations;
using AegisOps.Application.Audit;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.GitHub;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectEnvironment = AegisOps.Domain.Organization.Environment;

namespace AegisOps.Api.Tests;

/// <summary>
/// Roadmap v1.2 item 1: the GitHub App custom deployment protection rule (docs/github-deployment-gate.md). The webhook
/// endpoint runs in a TestServer host over SQLite; the Worker steps (mapping, evaluation, execution, callback) are called
/// directly as the job loop calls them. GitHub is an HttpMessageHandler stub: no test reaches a real GitHub endpoint, and
/// the App's RSA key is generated at runtime (no key material is committed).
/// </summary>
public sealed class GitHubDeploymentGateTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Secret = "test-webhook-secret-0123456789-abcdefghij";
    private const string Sha = "9f2c0a1e9f2c0a1e9f2c0a1e9f2c0a1e9f2c0a1e";
    private const string Repo = "Trixxy-Test/circuit-service";
    private const long RunId = 4242;
    private static readonly string[] ApproverRoles = ["Approver"];

    // ---------- Webhook: signature, events, replay ----------

    [Fact]
    public async Task ValidSignatureIsAcceptedAndQueuesADurableJob() {
        await using var host = await GateHost.StartAsync();
        var body = Payload();

        using var response = await host.PostAsync(body, Sign(body), "deployment_protection_rule", "delivery-1");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var gate = await host.WithDbAsync(db => db.GitHubDeploymentGates.SingleAsync(Token));
        Assert.Equal(GitHubGateStatus.Received, gate.Status);
        Assert.Equal(Repo, gate.RepositoryFullName);
        Assert.Equal(RunId, gate.RunId);
        Assert.Equal("staging", gate.EnvironmentName);
        Assert.Equal(Sha, gate.CommitSha);
        Assert.Equal(98765, gate.InstallationId);
        Assert.Equal($"https://github.com/{Repo}/actions/runs/{RunId}", gate.RunUrl);
        var job = await host.WithDbAsync(db => db.Jobs.SingleAsync(Token));
        Assert.Equal(JobType.GitHubGateEvaluate, job.Type);
        Assert.Contains(gate.Id.ToString("D"), job.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("deployment_callback_url", job.Payload, StringComparison.Ordinal);
        var audit = await host.WithDbAsync(db => db.AuditEvents.SingleAsync(Token));
        Assert.Equal("github.webhook_received", audit.Action);
        Assert.Equal("github-app", audit.ActorDisplay);
        Assert.Equal(gate.Id, audit.ResourceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha1=abc")]
    public async Task MissingOrInvalidSignatureIsRejectedBeforeParsing(string? signature) {
        await using var host = await GateHost.StartAsync();
        // Not even valid JSON: the signature check must come first.
        var body = "{ not json";

        using var response = await host.PostAsync(body, signature, "deployment_protection_rule", "delivery-1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(await host.WithDbAsync(db => db.GitHubDeploymentGates.AnyAsync(Token)));
        Assert.False(await host.WithDbAsync(db => db.Jobs.AnyAsync(Token)));
        var audit = await host.WithDbAsync(db => db.AuditEvents.SingleAsync(Token));
        Assert.Equal("github.webhook_rejected", audit.Action);
        Assert.Equal("Denied", audit.Outcome);
        Assert.Equal("Anonymous", audit.ActorType);
    }

    [Fact]
    public async Task BodyFromAnotherSecretIsRejectedAndRejectionAuditsAreThrottled() {
        await using var host = await GateHost.StartAsync();
        var body = Payload();
        var forged = GitHubWebhookSignature.Compute(Encoding.UTF8.GetBytes("another-secret-0123456789-0123456789"), Encoding.UTF8.GetBytes(body));

        for (var attempt = 0; attempt < 5; attempt++) {
            using var response = await host.PostAsync(body, forged, "deployment_protection_rule", $"delivery-{attempt}");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Equal(1, await host.WithDbAsync(db => db.AuditEvents.CountAsync(Token)));
        Assert.False(await host.WithDbAsync(db => db.GitHubDeploymentGates.AnyAsync(Token)));
    }

    [Fact]
    public async Task ReplayedDeliveryIsANoOp() {
        await using var host = await GateHost.StartAsync();
        var body = Payload();

        using var first = await host.PostAsync(body, Sign(body), "deployment_protection_rule", "delivery-1");
        using var second = await host.PostAsync(body, Sign(body), "deployment_protection_rule", "delivery-1");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(1, await host.WithDbAsync(db => db.GitHubDeploymentGates.CountAsync(Token)));
        Assert.Equal(1, await host.WithDbAsync(db => db.Jobs.CountAsync(Token)));
        Assert.Equal(1, await host.WithDbAsync(db => db.AuditEvents.CountAsync(Token)));
    }

    [Fact]
    public async Task PingOtherEventsAndOtherActionsAreAcknowledgedWithoutWork() {
        await using var host = await GateHost.StartAsync();
        const string ping = """{"zen":"Keep it logically awesome.","hook_id":1}""";
        using var pinged = await host.PostAsync(ping, Sign(ping), "ping", "delivery-ping");
        using var push = await host.PostAsync(ping, Sign(ping), "push", "delivery-push");
        var other = Payload(action: "completed");
        using var completed = await host.PostAsync(other, Sign(other), "deployment_protection_rule", "delivery-other");

        Assert.Equal(HttpStatusCode.NoContent, pinged.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, push.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        Assert.False(await host.WithDbAsync(db => db.GitHubDeploymentGates.AnyAsync(Token)));
        Assert.False(await host.WithDbAsync(db => db.AuditEvents.AnyAsync(Token)));
    }

    [Fact]
    public async Task MalformedSignedPayloadsAndOversizedBodiesAreRefused() {
        await using var host = await GateHost.StartAsync();
        var noCallback = Payload(callbackUrl: "https://api.github.com/repos/a/b/issues/1");
        using var badCallback = await host.PostAsync(noCallback, Sign(noCallback), "deployment_protection_rule", "delivery-1");
        var body = Payload();
        using var noDelivery = await host.PostAsync(body, Sign(body), "deployment_protection_rule", null);
        var huge = new string(' ', GitHubWebhook.MaxBodyBytes + 1);
        using var tooLarge = await host.PostAsync(huge, Sign(huge), "deployment_protection_rule", "delivery-2");

        Assert.Equal(HttpStatusCode.BadRequest, badCallback.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noDelivery.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.False(await host.WithDbAsync(db => db.GitHubDeploymentGates.AnyAsync(Token)));
    }

    [Fact]
    public async Task DisabledIntegrationAnswersNotFound() {
        await using var host = await GateHost.StartAsync(enabled: false);
        var body = Payload();
        using var response = await host.PostAsync(body, Sign(body), "deployment_protection_rule", "delivery-1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- Mapping failures: rejected on GitHub with an explanation ----------

    public static TheoryData<string, string> MappingFailures() => new() {
        { "no-repository", $"repository {Repo} is not connected to an AegisOps project" },
        { "no-environment", "project circuit-service has no environment named staging" },
        { "not-github-target", "environment Staging of project circuit-service uses the noop target" },
        { "no-artifact", "AegisOps: no artifact registered for commit 9f2c0a1 in project circuit-service. Register it from CI before the deploy job." },
    };

    [Theory]
    [MemberData(nameof(MappingFailures))]
    public async Task EachMappingFailureRejectsOnGitHubWithAClearComment(string failure, string expected) {
        await using var host = await GateHost.StartAsync();
        await host.SeedWorldAsync(
            repository: failure != "no-repository",
            environment: failure != "no-environment",
            gitHubTarget: failure != "not-github-target",
            artifact: failure != "no-artifact");
        var gateId = await host.ReceiveAsync();

        await host.WorkerAsync((db, audit) => GitHubGateProcessor.EvaluateAsync(db, audit, gateId, TimeProvider.System, Token));

        var gate = await host.GateAsync(gateId);
        Assert.Equal(GitHubGateStatus.DecisionPending, gate.Status);
        Assert.Equal(GitHubDeploymentGate.Rejected, gate.Decision);
        Assert.Null(gate.DeploymentId);
        Assert.False(await host.WithDbAsync(db => db.Deployments.AnyAsync(Token)));
        Assert.True(await host.WithDbAsync(db => db.Jobs.AnyAsync(job => job.Type == JobType.GitHubGateCallback, Token)));
        Assert.Contains(await host.AuditActionsAsync(), action => action == "github.gate_unmapped");

        var github = new GitHubStub();
        await host.CallbackAsync(gateId, github);

        var review = Assert.Single(github.Reviews);
        Assert.Equal($"/repos/{Repo}/actions/runs/{RunId}/deployment_protection_rule", review.Path);
        Assert.Equal("Bearer ghs_installation_token_1", review.Authorization);
        Assert.Equal("rejected", review.State);
        Assert.Equal("staging", review.Environment);
        Assert.Contains(expected, review.Comment, StringComparison.Ordinal);
        Assert.Equal(GitHubGateStatus.Reported, (await host.GateAsync(gateId)).Status);
    }

    // ---------- Decisions ----------

    [Fact]
    public async Task AllowApprovesOnGitHubAndSucceeds() {
        await using var host = await GateHost.StartAsync(publicBaseUrl: "https://aegisops.example.test/");
        await host.SeedWorldAsync();
        var gateId = await host.ReceiveAsync();
        await host.WorkerAsync((db, audit) => GitHubGateProcessor.EvaluateAsync(db, audit, gateId, TimeProvider.System, Token));
        var deploymentId = (await host.GateAsync(gateId)).DeploymentId!.Value;
        var deployment = await host.DeploymentAsync(deploymentId);
        Assert.Null(deployment.RequestedById);
        Assert.Null(deployment.RequestedByApiKeyId);
        Assert.Contains($"actions/runs/{RunId}", deployment.Reason, StringComparison.Ordinal);

        await host.WorkerAsync((db, audit) => DeploymentProcessor.EvaluateAsync(db, audit, deploymentId, TimeProvider.System, Token));
        Assert.Equal(DeploymentStatus.Approved, (await host.DeploymentAsync(deploymentId)).Status);
        await host.WorkerAsync((db, audit) => DeploymentProcessor.ExecuteAsync(db, audit, deploymentId, TimeProvider.System, Token));

        // Deploying until GitHub acknowledges; the callback job is queued with the approval.
        Assert.Equal(DeploymentStatus.Deploying, (await host.DeploymentAsync(deploymentId)).Status);
        Assert.Equal(GitHubDeploymentGate.Approved, (await host.GateAsync(gateId)).Decision);
        var github = new GitHubStub();
        await host.CallbackAsync(gateId, github);

        var review = Assert.Single(github.Reviews);
        Assert.Equal("approved", review.State);
        Assert.Contains("AegisOps approved this deployment", review.Comment, StringComparison.Ordinal);
        Assert.Contains("Staging gate v1", review.Comment, StringComparison.Ordinal);
        Assert.Contains("https://aegisops.example.test/", review.Comment, StringComparison.Ordinal);
        Assert.Equal(DeploymentStatus.Succeeded, (await host.DeploymentAsync(deploymentId)).Status);
        var events = await host.EventsAsync(deploymentId);
        Assert.Contains(events, item => item.Type == DeploymentEventType.Succeeded && item.Message.StartsWith("Released to GitHub", StringComparison.Ordinal));
        var actions = await host.AuditActionsAsync();
        Assert.Contains("github.gate_reported", actions);
        Assert.Contains("deployment.succeeded", actions);
        Assert.Equal(deploymentId, await host.WithDbAsync(db => db.Environments.Select(item => item.LastDeploymentId).SingleAsync(Token)));
    }

    [Fact]
    public async Task RequireApprovalWaitsThenAnAegisOpsApprovalApprovesOnGitHub() {
        await using var host = await GateHost.StartAsync();
        await host.SeedWorldAsync(requireApproval: true);
        var gateId = await host.ReceiveAsync();
        await host.WorkerAsync((db, audit) => GitHubGateProcessor.EvaluateAsync(db, audit, gateId, TimeProvider.System, Token));
        var deploymentId = (await host.GateAsync(gateId)).DeploymentId!.Value;
        await host.WorkerAsync((db, audit) => DeploymentProcessor.EvaluateAsync(db, audit, deploymentId, TimeProvider.System, Token));

        Assert.Equal(DeploymentStatus.AwaitingApproval, (await host.DeploymentAsync(deploymentId)).Status);
        Assert.False(await host.WithDbAsync(db => db.Jobs.AnyAsync(job => job.Type == JobType.GitHubGateCallback, Token)));
        Assert.Equal(GitHubGateStatus.Linked, (await host.GateAsync(gateId)).Status);

        // Any human approver passes approver != requester: the GitHub App is not a user.
        var approver = await host.CreateUserAsync("approver@example.test", "Approver");
        var decided = await host.CallAsync((services, http) => DeploymentEndpoints.Decide(deploymentId, new DeploymentDecisionRequest("Approved", "LGTM"),
            approver, services.GetRequiredService<UserManager<User>>(), Db(services), Audit(services), TimeProvider.System, http));
        Assert.IsType<NoContent>(decided);
        await host.WorkerAsync((db, audit) => DeploymentProcessor.EvaluateAsync(db, audit, deploymentId, TimeProvider.System, Token));
        await host.WorkerAsync((db, audit) => DeploymentProcessor.ExecuteAsync(db, audit, deploymentId, TimeProvider.System, Token));
        var github = new GitHubStub();
        await host.CallbackAsync(gateId, github);

        var review = Assert.Single(github.Reviews);
        Assert.Equal("approved", review.State);
        Assert.Contains("after 1 approval(s)", review.Comment, StringComparison.Ordinal);
        Assert.Equal(DeploymentStatus.Succeeded, (await host.DeploymentAsync(deploymentId)).Status);
    }

    [Fact]
    public async Task PolicyDenyRejectsOnGitHubWithRuleByRuleReasons() {
        await using var host = await GateHost.StartAsync();
        await host.SeedWorldAsync(denyFailedTests: true, testStatus: CheckStatus.Failed);
        var gateId = await host.ReceiveAsync();
        await host.WorkerAsync((db, audit) => GitHubGateProcessor.EvaluateAsync(db, audit, gateId, TimeProvider.System, Token));
        var deploymentId = (await host.GateAsync(gateId)).DeploymentId!.Value;

        await host.WorkerAsync((db, audit) => DeploymentProcessor.EvaluateAsync(db, audit, deploymentId, TimeProvider.System, Token));

        Assert.Equal(DeploymentStatus.Denied, (await host.DeploymentAsync(deploymentId)).Status);
        Assert.Equal(GitHubDeploymentGate.Rejected, (await host.GateAsync(gateId)).Decision);
        var github = new GitHubStub();
        await host.CallbackAsync(gateId, github);
        var review = Assert.Single(github.Reviews);
        Assert.Equal("rejected", review.State);
        Assert.Contains("denied this deployment by policy", review.Comment, StringComparison.Ordinal);
        Assert.Contains("Failed rules:", review.Comment, StringComparison.Ordinal);
        Assert.Contains("RequireTestsPassed [Deny] (Staging gate v1)", review.Comment, StringComparison.Ordinal);
        Assert.Equal(DeploymentStatus.Denied, (await host.DeploymentAsync(deploymentId)).Status);
    }

    [Fact]
    public async Task ApproverRejectionAndCancellationRejectOnGitHub() {
        await using var host = await GateHost.StartAsync();
        await host.SeedWorldAsync(requireApproval: true);
        var approver = await host.CreateUserAsync("approver@example.test", "Approver");
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");

        async Task<(Guid Gate, Guid Deployment)> AwaitingAsync(string delivery) {
            var gateId = await host.ReceiveAsync(delivery);
            await host.WorkerAsync((db, audit) => GitHubGateProcessor.EvaluateAsync(db, audit, gateId, TimeProvider.System, Token));
            var deploymentId = (await host.GateAsync(gateId)).DeploymentId!.Value;
            await host.WorkerAsync((db, audit) => DeploymentProcessor.EvaluateAsync(db, audit, deploymentId, TimeProvider.System, Token));
            return (gateId, deploymentId);
        }

        var rejected = await AwaitingAsync("delivery-reject");
        Assert.IsType<NoContent>(await host.CallAsync((services, http) => DeploymentEndpoints.Decide(rejected.Deployment,
            new DeploymentDecisionRequest("Rejected", "Not during the freeze"), approver, services.GetRequiredService<UserManager<User>>(),
            Db(services), Audit(services), TimeProvider.System, http)));
        var cancelled = await AwaitingAsync("delivery-cancel");
        Assert.IsType<NoContent>(await host.CallAsync((services, http) => DeploymentEndpoints.Cancel(cancelled.Deployment, admin,
            services.GetRequiredService<UserManager<User>>(), Db(services), Audit(services), TimeProvider.System, http)));

        var github = new GitHubStub();
        await host.CallbackAsync(rejected.Gate, github);
        await host.CallbackAsync(cancelled.Gate, github);

        Assert.Equal(2, github.Reviews.Count);
        Assert.All(github.Reviews, review => Assert.Equal("rejected", review.State));
        Assert.Contains("rejected this deployment (an approver declined it). Not during the freeze", github.Reviews[0].Comment, StringComparison.Ordinal);
        Assert.Contains("cancelled this deployment", github.Reviews[1].Comment, StringComparison.Ordinal);
        // Exactly one callback job per gate, even though both transitions saved several times.
        Assert.Equal(2, await host.WithDbAsync(db => db.Jobs.CountAsync(job => job.Type == JobType.GitHubGateCallback, Token)));
    }

    [Fact]
    public async Task ManualDeploymentRequestsToAGitHubTargetAreRefused() {
        await using var host = await GateHost.StartAsync();
        var world = await host.SeedWorldAsync();
        var admin = await host.CreateUserAsync("admin@example.test", "Admin");

        var result = await host.CallAsync((services, http) => DeploymentEndpoints.Request(
            new RequestDeploymentRequest(world.ArtifactId, world.EnvironmentId, null, null), admin,
            services.GetRequiredService<UserManager<User>>(), Db(services), Audit(services), TimeProvider.System, new NoIdempotency(), http));

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ProblemHttpResult>(result).StatusCode);
        Assert.False(await host.WithDbAsync(db => db.Deployments.AnyAsync(Token)));
    }

    // ---------- Callback: retries, idempotency, refusals ----------

    [Fact]
    public async Task CallbackRetriesOnServerErrorsAndNeverPostsTwice() {
        await using var host = await GateHost.StartAsync();
        await host.SeedWorldAsync();
        var gateId = await host.ReceiveAsync();
        await host.WorkerAsync((db, audit) => GitHubGateProcessor.EvaluateAsync(db, audit, gateId, TimeProvider.System, Token));
        var deploymentId = (await host.GateAsync(gateId)).DeploymentId!.Value;
        await host.WorkerAsync((db, audit) => DeploymentProcessor.EvaluateAsync(db, audit, deploymentId, TimeProvider.System, Token));
        await host.WorkerAsync((db, audit) => DeploymentProcessor.ExecuteAsync(db, audit, deploymentId, TimeProvider.System, Token));
        var github = new GitHubStub(HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.NoContent);

        // A transient failure throws: the job loop then calls Job.Fail, which schedules a retry with backoff.
        var first = await Assert.ThrowsAsync<GitHubTransientException>(() => host.CallbackAsync(gateId, github));
        Assert.Equal("GitHub review call failed (HTTP 502).", first.Message);
        var job = Job.Enqueue(JobType.GitHubGateCallback, "{}", DateTimeOffset.UnixEpoch, "c");
        job.MarkRunning(DateTimeOffset.UnixEpoch, "worker");
        job.Fail(first.Message, DateTimeOffset.UnixEpoch);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.True(job.ScheduledAt > DateTimeOffset.UnixEpoch);
        Assert.Equal(GitHubGateStatus.DecisionPending, (await host.GateAsync(gateId)).Status);
        Assert.Equal(DeploymentStatus.Deploying, (await host.DeploymentAsync(deploymentId)).Status);

        await Assert.ThrowsAsync<GitHubTransientException>(() => host.CallbackAsync(gateId, github));
        await host.CallbackAsync(gateId, github);
        // A re-run after success (lost lease, reaped job) finds the gate reported and does not post again.
        await host.CallbackAsync(gateId, github);

        Assert.Equal(3, github.Reviews.Count);
        Assert.Equal(1, github.TokenRequests);
        var gate = await host.GateAsync(gateId);
        Assert.Equal(GitHubGateStatus.Reported, gate.Status);
        Assert.Equal(204, gate.CallbackStatusCode);
        Assert.Equal(DeploymentStatus.Succeeded, (await host.DeploymentAsync(deploymentId)).Status);
        Assert.Equal(1, (await host.EventsAsync(deploymentId)).Count(item => item.Type == DeploymentEventType.Succeeded));
    }

    [Fact]
    public async Task FinalRefusalFailsTheDeploymentWithoutASecondReview() {
        await using var host = await GateHost.StartAsync();
        await host.SeedWorldAsync();
        var gateId = await host.ReceiveAsync();
        await host.WorkerAsync((db, audit) => GitHubGateProcessor.EvaluateAsync(db, audit, gateId, TimeProvider.System, Token));
        var deploymentId = (await host.GateAsync(gateId)).DeploymentId!.Value;
        await host.WorkerAsync((db, audit) => DeploymentProcessor.EvaluateAsync(db, audit, deploymentId, TimeProvider.System, Token));
        await host.WorkerAsync((db, audit) => DeploymentProcessor.ExecuteAsync(db, audit, deploymentId, TimeProvider.System, Token));
        var github = new GitHubStub(HttpStatusCode.UnprocessableEntity);

        await host.CallbackAsync(gateId, github);

        var deployment = await host.DeploymentAsync(deploymentId);
        Assert.Equal(DeploymentStatus.Failed, deployment.Status);
        Assert.Contains("HTTP 422", deployment.FailureReason, StringComparison.Ordinal);
        Assert.Equal(GitHubGateStatus.CallbackFailed, (await host.GateAsync(gateId)).Status);
        // The Failed transition does not queue a contradicting "rejected" review.
        Assert.Equal(1, await host.WithDbAsync(db => db.Jobs.CountAsync(job => job.Type == JobType.GitHubGateCallback, Token)));
        Assert.Single(github.Reviews);
    }

    [Fact]
    public async Task UnauthorizedReviewDropsTheCachedTokenAndRetries() {
        var time = new MutableTime(DateTimeOffset.Parse("2026-11-19T10:00:00Z", CultureInfo.InvariantCulture));
        var github = new GitHubStub(HttpStatusCode.Unauthorized, HttpStatusCode.NoContent) { Time = time };
        using var settings = Settings();
        var client = new GitHubAppClient(settings, () => github.Client, time);
        var gate = GitHubDeploymentGate.Receive("d", time.GetUtcNow(), 98765, Repo, RunId, "staging", Sha, null, "https://api.github.com/x", "https://github.com/x");
        gate.Decide(GitHubDeploymentGate.Rejected, "no", time.GetUtcNow());

        await Assert.ThrowsAsync<GitHubTransientException>(() => client.ReviewAsync(gate, "rejected", "no", Token));
        var result = await client.ReviewAsync(gate, "rejected", "no", Token);

        Assert.True(result.Accepted);
        Assert.Equal(2, github.TokenRequests);
        Assert.Equal("Bearer ghs_installation_token_2", github.Reviews[1].Authorization);
    }

    // ---------- App JWT and installation tokens ----------

    [Fact]
    public void AppJwtIsRs256WithIatExpAndIssuer() {
        var time = new MutableTime(DateTimeOffset.Parse("2026-11-19T10:00:00Z", CultureInfo.InvariantCulture));
        using var rsa = RSA.Create(2048);
        using var settings = Settings(rsa);
        var client = new GitHubAppClient(settings, () => throw new InvalidOperationException("no HTTP"), time);

        var jwt = client.CreateAppJwt();

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
        using var claims = JsonDocument.Parse(FromBase64Url(parts[1]));
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        Assert.Equal(now - 60, claims.RootElement.GetProperty("iat").GetInt64());
        var exp = claims.RootElement.GetProperty("exp").GetInt64();
        Assert.True(exp > now && exp - (now - 60) <= 600, "exp must be at most 10 minutes after iat.");
        Assert.Equal("123456", claims.RootElement.GetProperty("iss").GetString());
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task InstallationTokensAreCachedUntilShortlyBeforeTheyExpire() {
        var time = new MutableTime(DateTimeOffset.Parse("2026-11-19T10:00:00Z", CultureInfo.InvariantCulture));
        var github = new GitHubStub { TokenLifetime = TimeSpan.FromHours(1), Time = time };
        using var settings = Settings();
        var client = new GitHubAppClient(settings, () => github.Client, time);

        var first = await client.GetInstallationTokenAsync(98765, Token);
        time.Advance(TimeSpan.FromMinutes(50));
        var cached = await client.GetInstallationTokenAsync(98765, Token);
        var otherInstallation = await client.GetInstallationTokenAsync(11111, Token);
        time.Advance(TimeSpan.FromMinutes(6)); // inside the 5-minute refresh margin
        var refreshed = await client.GetInstallationTokenAsync(98765, Token);

        Assert.Equal(first, cached);
        Assert.NotEqual(first, otherInstallation);
        Assert.NotEqual(first, refreshed);
        Assert.Equal(3, github.TokenRequests);
        var exchange = github.TokenCalls[0];
        Assert.Equal("/app/installations/98765/access_tokens", exchange.Path);
        Assert.StartsWith("Bearer ey", exchange.Authorization, StringComparison.Ordinal);
        Assert.Contains("\"deployments\":\"write\"", exchange.Body, StringComparison.Ordinal);
        Assert.Equal("2022-11-28", exchange.ApiVersion);
    }

    // ---------- Configuration ----------

    [Fact]
    public void ConfigurationIsDisabledByDefaultAndValidatedWhenEnabled() {
        using var disabled = GitHubAppSettings.Load(Config([]), requireWebhookSecret: true, requirePrivateKey: true);
        Assert.False(disabled.Enabled);
        Assert.False(disabled.CanVerifyWebhooks);

        using var rsa = RSA.Create(2048);
        var pemBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportRSAPrivateKeyPem()));
        using var valid = GitHubAppSettings.Load(Config(new() {
            ["GitHubApp:Enabled"] = "true",
            ["GitHubApp:AppId"] = "123456",
            ["GitHubApp:PrivateKeyBase64"] = pemBase64,
            ["GitHubApp:WebhookSecret"] = Secret,
        }), requireWebhookSecret: true, requirePrivateKey: true);
        Assert.True(valid.CanVerifyWebhooks);
        Assert.True(valid.CanCallGitHub);
        Assert.Equal(GitHubAppSettings.DefaultApiBaseUrl, valid.ApiBaseUrl);

        // The API needs only the secret; the Worker needs only the App ID and key.
        using var apiOnly = GitHubAppSettings.Load(Config(new() { ["GitHubApp:Enabled"] = "true", ["GitHubApp:WebhookSecret"] = Secret }),
            requireWebhookSecret: true, requirePrivateKey: false);
        Assert.False(apiOnly.CanCallGitHub);

        void Invalid(Dictionary<string, string?> values, bool worker, string expected) {
            values["GitHubApp:Enabled"] = "true";
            var error = Assert.Throws<InvalidOperationException>(() => GitHubAppSettings.Load(Config(values), !worker, worker));
            Assert.Contains(expected, error.Message, StringComparison.Ordinal);
            foreach (var value in values.Values.Where(value => value is { Length: > 8 } && value != "true"))
                Assert.DoesNotContain(value!, error.Message, StringComparison.Ordinal);
        }

        Invalid(new() { ["GitHubApp:WebhookSecret"] = "too-short-secret" }, worker: false, "GitHubApp:WebhookSecret must be at least 32 characters");
        Invalid([], worker: false, "GitHubApp:WebhookSecret (GITHUB_APP_WEBHOOK_SECRET) is missing");
        Invalid(new() { ["GitHubApp:AppId"] = "123456" }, worker: true, "GitHubApp:PrivateKey or GitHubApp:PrivateKeyBase64");
        Invalid(new() { ["GitHubApp:AppId"] = "123456", ["GitHubApp:PrivateKeyBase64"] = "not base64!!" }, worker: true, "GitHubApp:PrivateKeyBase64 is not valid base64");
        Invalid(new() { ["GitHubApp:AppId"] = "123456", ["GitHubApp:PrivateKey"] = "not a PEM document" },
            worker: true, "GitHubApp:PrivateKey is not a valid RSA private key");
        Invalid(new() { ["GitHubApp:PrivateKeyBase64"] = pemBase64 }, worker: true, "GitHubApp:AppId (GITHUB_APP_ID) is missing");
        Invalid(new() { ["GitHubApp:WebhookSecret"] = Secret, ["GitHubApp:ApiBaseUrl"] = "http://api.github.com" }, worker: false, "GitHubApp:ApiBaseUrl must be an absolute https URL");
    }

    [Fact]
    public void SignatureHelperMatchesGitHubsFormat() {
        var body = Encoding.UTF8.GetBytes("Hello, World!");
        // Example from GitHub's "Validating webhook deliveries" documentation (secret "It's a Secret to Everybody").
        const string expected = "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17";
        var secret = Encoding.UTF8.GetBytes("It's a Secret to Everybody");
        Assert.Equal(expected, GitHubWebhookSignature.Compute(secret, body));
        Assert.True(GitHubWebhookSignature.Verify(secret, body, expected));
        Assert.False(GitHubWebhookSignature.Verify(secret, body, expected.ToUpperInvariant()));
        Assert.False(GitHubWebhookSignature.Verify(secret, body, expected[..^2]));
        Assert.False(GitHubWebhookSignature.Verify(secret, "Hello, World?"u8, expected));
    }

    [Fact]
    public void CommentsAreTruncatedToGitHubsLimit() {
        var comment = GitHubDeploymentGate.Truncate(new string('x', 5000));
        Assert.Equal(GitHubDeploymentGate.MaxCommentLength, comment!.Length);
        Assert.EndsWith("…", comment, StringComparison.Ordinal);
    }

    // ---------- Helpers ----------

    private static string Sign(string body) => GitHubWebhookSignature.Compute(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body));

    private static string Payload(string action = "requested", string? callbackUrl = null) => JsonSerializer.Serialize(new Dictionary<string, object?> {
        ["action"] = action,
        ["environment"] = "staging",
        ["event"] = "push",
        ["sha"] = Sha,
        ["ref"] = "refs/heads/main",
        ["deployment_callback_url"] = callbackUrl ?? $"https://api.github.com/repos/{Repo}/actions/runs/{RunId}/deployment_protection_rule",
        ["deployment"] = new Dictionary<string, object?> { ["sha"] = Sha, ["ref"] = "main", ["environment"] = "staging" },
        ["repository"] = new Dictionary<string, object?> { ["full_name"] = Repo, ["html_url"] = $"https://github.com/{Repo}" },
        ["installation"] = new Dictionary<string, object?> { ["id"] = 98765 },
    });

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static GitHubAppSettings Settings(RSA? key = null, string? publicBaseUrl = null, bool enabled = true) {
        using var generated = key is null ? RSA.Create(2048) : null;
        var pem = (key ?? generated!).ExportRSAPrivateKeyPem();
        return GitHubAppSettings.Load(Config(new() {
            ["GitHubApp:Enabled"] = enabled ? "true" : "false",
            ["GitHubApp:AppId"] = "123456",
            ["GitHubApp:PrivateKeyBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)),
            ["GitHubApp:WebhookSecret"] = Secret,
            ["GitHubApp:PublicBaseUrl"] = publicBaseUrl,
        }), requireWebhookSecret: true, requirePrivateKey: true);
    }

    private static byte[] FromBase64Url(string value) {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    private static AegisOpsDbContext Db(IServiceProvider services) => services.GetRequiredService<AegisOpsDbContext>();
    private static IAuditWriter Audit(IServiceProvider services) => services.GetRequiredService<IAuditWriter>();

    private sealed record World(Guid ProjectId, Guid EnvironmentId, Guid ArtifactId);

    private sealed record Review(string Path, string? Authorization, string? State, string? Environment, string Comment);

    private sealed record TokenCall(string Path, string? Authorization, string Body, string? ApiVersion);

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan by) => current += by;
    }

    /// <summary>Stands in for api.github.com: installation tokens and protection-rule reviews (statuses in order, then 204).</summary>
    private sealed class GitHubStub : HttpMessageHandler {
        private readonly Queue<HttpStatusCode> reviewStatuses;
        private int tokens;

        public GitHubStub(params HttpStatusCode[] reviewStatuses) {
            this.reviewStatuses = new Queue<HttpStatusCode>(reviewStatuses);
            Client = new HttpClient(this);
        }

        public HttpClient Client { get; }
        public List<Review> Reviews { get; } = [];
        public List<TokenCall> TokenCalls { get; } = [];
        public int TokenRequests => TokenCalls.Count;
        public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromHours(1);
        public TimeProvider Time { get; init; } = TimeProvider.System;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri.AbsolutePath;
            var authorization = request.Headers.Authorization?.ToString();
            if (path.EndsWith("/access_tokens", StringComparison.Ordinal)) {
                TokenCalls.Add(new TokenCall(path, authorization, body,
                    request.Headers.TryGetValues("X-GitHub-Api-Version", out var versions) ? versions.Single() : null));
                tokens++;
                var expires = Time.GetUtcNow().Add(TokenLifetime).ToString("O", CultureInfo.InvariantCulture);
                return new HttpResponseMessage(HttpStatusCode.Created) {
                    Content = new StringContent($$"""{"token":"ghs_installation_token_{{tokens}}","expires_at":"{{expires}}"}""", Encoding.UTF8, "application/json"),
                };
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Reviews.Add(new Review(path, authorization, root.TryGetProperty("state", out var state) ? state.GetString() : null,
                root.GetProperty("environment_name").GetString(), root.GetProperty("comment").GetString() ?? ""));
            Assert.True(Reviews[^1].Comment.Length <= GitHubDeploymentGate.MaxCommentLength);
            return new HttpResponseMessage(reviewStatuses.Count > 0 ? reviewStatuses.Dequeue() : HttpStatusCode.NoContent);
        }

        protected override void Dispose(bool disposing) {
            if (disposing) Client.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class NoIdempotency : AegisOps.Application.Idempotency.IIdempotencyStore {
        public Task<AegisOps.Application.Idempotency.StoredResponse?> FindAsync(string principal, string key, CancellationToken cancellationToken) =>
            Task.FromResult<AegisOps.Application.Idempotency.StoredResponse?>(null);
        public Task<string?> TryBeginAsync(string principal, string key, CancellationToken cancellationToken) => Task.FromResult<string?>("owner");
        public Task<bool> SaveAsync(string principal, string key, string owner, AegisOps.Application.Idempotency.StoredResponse response,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class GateHost : IAsyncDisposable {
        private readonly WebApplication app;
        private readonly SqliteConnection connection;
        private readonly HttpClient client;
        private readonly GitHubAppSettings settings;

        private GateHost(WebApplication app, SqliteConnection connection, GitHubAppSettings settings) {
            this.app = app;
            this.connection = connection;
            this.settings = settings;
            client = app.GetTestClient();
        }

        public static async Task<GateHost> StartAsync(bool enabled = true, string? publicBaseUrl = null) {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(Token);
            var settings = Settings(publicBaseUrl: publicBaseUrl, enabled: enabled);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection)
                .UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteModelCustomizer>()
                .EnableServiceProviderCaching(false));
            builder.Services.AddIdentityCore<User>(IdentityStoreExtensions.ConfigureIdentity).AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<AegisOpsDbContext>();
            builder.Services.AddScoped<IAuditWriter, EfAuditWriter>();
            builder.Services.AddSingleton(settings);
            builder.Services.AddSingleton<GitHubWebhookRejectionThrottle>();
            var app = builder.Build();
            app.MapPost(GitHubWebhook.Route, GitHubWebhook.Handle);
            await app.StartAsync(Token);
            var host = new GateHost(app, connection, settings);
            await host.WithDbAsync(db => db.Database.EnsureCreatedAsync(Token));
            return host;
        }

        public async Task<HttpResponseMessage> PostAsync(string body, string? signature, string eventName, string? delivery) {
            using var request = new HttpRequestMessage(HttpMethod.Post, GitHubWebhook.Route) {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (signature is not null) request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
            request.Headers.Add("X-GitHub-Event", eventName);
            if (delivery is not null) request.Headers.Add("X-GitHub-Delivery", delivery);
            return await client.SendAsync(request, Token);
        }

        /// <summary>Delivers a signed request and returns its gate id.</summary>
        public async Task<Guid> ReceiveAsync(string delivery = "delivery-1") {
            var body = Payload();
            using var response = await PostAsync(body, Sign(body), "deployment_protection_rule", delivery);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return await WithDbAsync(db => db.GitHubDeploymentGates.Where(gate => gate.DeliveryId == delivery).Select(gate => gate.Id).SingleAsync(Token));
        }

        public async Task<World> SeedWorldAsync(bool repository = true, bool environment = true, bool gitHubTarget = true, bool artifact = true,
            bool requireApproval = false, bool denyFailedTests = false, CheckStatus testStatus = CheckStatus.Passed) {
            var now = DateTimeOffset.UtcNow;
            var author = await CreateUserAsync($"author-{Guid.NewGuid():N}@example.test", "Admin");
            var authorId = Guid.Parse(author.FindFirst("sub")!.Value, CultureInfo.InvariantCulture);
            var team = Team.Create("Platform", "platform", now);
            var project = Project.Create(team.Id, "Circuit Service", "circuit-service", now);
            var staging = ProjectEnvironment.Create(project.Id, "Staging", EnvironmentTier.Staging, 1,
                gitHubTarget ? DeploymentTarget.GitHub() : DeploymentTarget.Noop());
            var registered = Artifact.Create(project.Id, "1.0.0", Sha, "main", "registry.example/circuit:1.0.0",
                CheckStatus.Passed, testStatus, now, createdById: authorId);
            var policy = Policy.Create("Staging gate", PolicyScope.Global, [EnvironmentTier.Staging], authorId, now);
            await WithDbAsync(async db => {
                db.AddRange(team, project, policy);
                // GitHub repository names are case-insensitive: the stored name differs in case from the payload's.
                if (repository) db.Add(Repository.Create(project.Id, RepositoryProvider.GitHub, Repo.ToLowerInvariant(), "main", $"https://github.com/{Repo}"));
                if (environment) db.Add(staging);
                if (artifact) db.Add(registered);
                if (requireApproval)
                    db.Add(PolicyRule.Create(policy.Id, PolicyRuleType.RequireApprovals, RuleEffect.RequireApproval, 1, """{"count":1,"roles":["Approver"]}"""));
                if (denyFailedTests)
                    db.Add(PolicyRule.Create(policy.Id, PolicyRuleType.RequireTestsPassed, RuleEffect.Deny, 2));
                if (!requireApproval && !denyFailedTests)
                    db.Add(PolicyRule.Create(policy.Id, PolicyRuleType.RequireTestsPassed, RuleEffect.Deny, 1));
                await db.SaveChangesAsync(Token);
                // Approvers are team members (docs/09 ApproveDeploymentHandler).
                pendingTeam = team.Id;
            });
            return new World(project.Id, staging.Id, registered.Id);
        }

        private Guid? pendingTeam;

        public async Task<ClaimsPrincipal> CreateUserAsync(string email, string role) {
            await using var scope = app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync(role))
                Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() })).Succeeded);
            var user = User.Create(email, email.Split('@')[0], DateTimeOffset.UtcNow);
            Assert.True((await users.CreateAsync(user, "Correct-Horse-Battery-9!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            if (pendingTeam is Guid teamId && role != "Admin") {
                var db = Db(scope.ServiceProvider);
                db.TeamMembers.Add(TeamMember.Create(teamId, user.Id, TeamRole.Member, DateTimeOffset.UtcNow));
                await db.SaveChangesAsync(Token);
            }
            return new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.Id.ToString("D")), new Claim("role", role)], "test"));
        }

        public async Task<IResult> CallAsync(Func<IServiceProvider, HttpContext, Task<IResult>> call) {
            await using var scope = app.Services.CreateAsyncScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = Token };
            return await call(scope.ServiceProvider, http);
        }

        /// <summary>Runs a Worker step in its own unit of work, as the job loop does.</summary>
        public async Task WorkerAsync(Func<AegisOpsDbContext, IAuditWriter, Task> step) {
            await using var scope = app.Services.CreateAsyncScope();
            var db = Db(scope.ServiceProvider);
            await step(db, Audit(scope.ServiceProvider));
            await db.SaveChangesAsync(Token);
        }

        private readonly Dictionary<GitHubStub, GitHubAppClient> gitHubClients = [];

        /// <summary>One App client per stub, like the Worker's singleton, so installation tokens stay cached across calls.</summary>
        public Task CallbackAsync(Guid gateId, GitHubStub github) {
            if (!gitHubClients.TryGetValue(github, out var gitHubClient))
                gitHubClients[github] = gitHubClient = new GitHubAppClient(settings, () => github.Client, TimeProvider.System);
            return WorkerAsync((db, audit) => GitHubGateProcessor.CallbackAsync(db, audit, gitHubClient, gateId, TimeProvider.System, Token));
        }

        public Task<GitHubDeploymentGate> GateAsync(Guid id) => WithDbAsync(db => db.GitHubDeploymentGates.AsNoTracking().SingleAsync(item => item.Id == id, Token));
        public Task<Deployment> DeploymentAsync(Guid id) => WithDbAsync(db => db.Deployments.AsNoTracking().SingleAsync(item => item.Id == id, Token));
        public Task<List<DeploymentEvent>> EventsAsync(Guid id) =>
            WithDbAsync(db => db.DeploymentEvents.AsNoTracking().Where(item => item.DeploymentId == id).OrderBy(item => item.Sequence).ToListAsync(Token));
        public Task<List<string>> AuditActionsAsync() => WithDbAsync(db => db.AuditEvents.AsNoTracking().Select(item => item.Action).ToListAsync(Token));

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
            settings.Dispose();
        }
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
            modelBuilder.Entity<Deployment>().Property(item => item.RequestedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<DeploymentEvent>().Property(item => item.Timestamp).HasConversion(new DateTimeOffsetToBinaryConverter());
        }
    }
}
