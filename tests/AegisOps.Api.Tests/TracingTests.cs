using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AegisOps.Application.Observability;
using AegisOps.Infrastructure.Observability;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Deploy;
using AegisOps.Application.PolicyEngine;
using AegisOps.Application.Scanning;
using AegisOps.Infrastructure.Ai;
using AegisOps.Infrastructure.Docker;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Security;
using System.Net;
using AegisOps.Api.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace AegisOps.Api.Tests;

// ActivityListeners are process-wide: run alone so spans from parallel tests (e.g. policy
// simulation endpoints) cannot make Assert.Single flaky or race the collectors.
[CollectionDefinition("Tracing", DisableParallelization = true)]
public sealed class TracingTestGroup;

[Collection("Tracing")]
public sealed class TracingTests {
    [Fact]
    public void Correlation_headers_are_bounded_and_untrusted_values_never_enter_baggage() {
        using var listener = Listen();
        using var parent = AegisOpsTracing.Source.StartActivity("http.request");
        Assert.NotNull(parent);
        var correlation = AegisOpsTracing.SetCorrelation(parent, "safe-id_1");
        Assert.Equal("safe-id_1", correlation);
        Assert.Equal(correlation, parent.GetBaggageItem("correlationId"));
        Assert.NotEqual("bad\nsecret", AegisOpsTracing.SetCorrelation(parent, "bad\nsecret"));
        var replacement = AegisOpsTracing.SetCorrelation(parent, new string('x', 65));
        Assert.NotEqual(new string('x', 65), replacement);
        Assert.Equal(replacement, parent.GetBaggageItem("correlationId"));
        Assert.True(AegisOpsTracing.IsSafeCorrelation(replacement));
    }

    [Fact]
    public void Policy_evaluation_emits_bounded_decision() {
        var seen = new ConcurrentQueue<Activity>();
        using var listener = Listen(seen);
        var context = new PolicyContext("main", CheckStatus.Passed, CheckStatus.Passed, null,
            EnvironmentTier.Development, Guid.NewGuid(), Guid.NewGuid(), null, 0, DateTimeOffset.UtcNow, []);
        var result = PolicyEngine.Evaluate(context, []);
        Assert.Equal("Allow", result.Decision.ToString());
        Assert.Equal("Allow", Assert.Single(seen, activity => activity.DisplayName == "PolicyEngine.Evaluate").GetTagItem("decision"));
    }

    [Fact]
    public async Task Ollama_span_records_only_bounded_token_counts_not_model_or_message() {
        var seen = new ConcurrentQueue<Activity>();
        using var listener = Listen(seen);
        using var http = new HttpClient(new ReplyHandler()) { BaseAddress = new Uri("http://localhost/") };
        var reply = await new OllamaLlmClient(http).Chat("sensitive-model", [new LlmMessage("user", "private prompt")], CancellationToken.None);
        Assert.Equal(3, reply.PromptTokens);
        var span = Assert.Single(seen, activity => activity.DisplayName == "Ollama.Chat");
        Assert.Equal(3, span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.DoesNotContain(span.Tags, tag => tag.Value?.Contains("private prompt", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Scanner_parser_records_only_bounded_type_not_report_content() {
        var seen = new ConcurrentQueue<Activity>();
        using var listener = Listen(seen);
        var findings = SarifReportParser.Parse(ScannerType.Semgrep,
            Encoding.UTF8.GetBytes("""{"runs":[],"metadata":"private-scan-payload"}"""));
        Assert.Empty(findings);
        var span = Assert.Single(seen, activity => activity.DisplayName == "Scanner.ParseReport");
        Assert.Equal("Semgrep", span.GetTagItem("scanner.type"));
        Assert.DoesNotContain(span.Tags, tag => tag.Value?.Contains("private-scan-payload", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Docker_executor_rejects_non_docker_target_with_a_bounded_span() {
        var seen = new ConcurrentQueue<Activity>();
        using var listener = Listen(seen);
        var artifact = Artifact.Create(Guid.NewGuid(), "1.0.0", new string('a', 40), "main",
            "example/image:1", CheckStatus.Passed, CheckStatus.Passed, DateTimeOffset.UtcNow,
            createdById: Guid.NewGuid());
        var result = await DockerDeploymentExecutor.ExecuteAsync(DeploymentTarget.Noop(), artifact,
            "site-service", "Development", Guid.NewGuid(),
            (_, _, _) => throw new InvalidOperationException("No Docker event expected."), CancellationToken.None);
        Assert.False(result.Succeeded);
        var span = Assert.Single(seen, activity => activity.DisplayName == "Docker.Execute");
        Assert.Equal("Failed", span.GetTagItem("outcome"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.DoesNotContain(span.Tags, tag => tag.Key.Contains("image", StringComparison.OrdinalIgnoreCase)
            || tag.Key.Contains("container", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Api_request_span_is_root_or_remote_child_and_ignores_caller_baggage() {
        var seen = new ConcurrentQueue<Activity>();
        using var listener = Listen(seen);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseApiRequestTracing();
        app.MapGet("/api/ping", (HttpContext context) => Results.Text(context.Items["correlationId"] as string));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        using var plain = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        plain.Headers.Add("X-Correlation-Id", "safe-id_1");
        plain.Headers.Add("baggage", "leak=secret");
        using var plainResponse = await client.SendAsync(plain, TestContext.Current.CancellationToken);
        Assert.Equal("safe-id_1", await plainResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = Assert.Single(seen, activity => activity.DisplayName == "Api.Request");
        // ASP.NET's request Activity is never exported, so it must not become the parent.
        Assert.Equal(default, root.ParentSpanId);
        Assert.Null(root.GetBaggageItem("leak"));
        Assert.Equal("safe-id_1", root.GetBaggageItem("correlationId"));

        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        using var traced = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        traced.Headers.Add("traceparent", $"00-{traceId}-{spanId}-01");
        using var tracedResponse = await client.SendAsync(traced, TestContext.Current.CancellationToken);
        var child = Assert.Single(seen, activity => activity.DisplayName == "Api.Request" && activity != root);
        Assert.Equal(traceId, child.TraceId);
        Assert.Equal(spanId, child.ParentSpanId);
    }

    [Fact]
    public async Task Failed_docker_step_span_has_error_status_without_exception_details() {
        var seen = new ConcurrentQueue<Activity>();
        using var listener = Listen(seen);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DockerDeploymentExecutor.TraceStepAsync<bool>("Docker.PullImage",
            () => throw new InvalidOperationException("pull registry.example/secret-image failed")));
        var span = Assert.Single(seen, activity => activity.DisplayName == "Docker.PullImage");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.StatusDescription);
        Assert.Empty(span.Events);
        Assert.Empty(span.Tags);
    }

    [Fact]
    public async Task Unhealthy_docker_step_marks_span_error_without_details() {
        var seen = new ConcurrentQueue<Activity>();
        using var listener = Listen(seen);
        var healthy = await DockerDeploymentExecutor.TraceStepAsync("Docker.HealthCheck", () => Task.FromResult(false), result => result);
        Assert.False(healthy);
        var span = Assert.Single(seen, activity => activity.DisplayName == "Docker.HealthCheck");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.StatusDescription);
        Assert.Empty(span.Events);
    }

    [Fact]
    public async Task Failed_ollama_call_marks_span_error_without_details() {
        var seen = new ConcurrentQueue<Activity>();
        using var listener = Listen(seen);
        using var http = new HttpClient(new StatusHandler(HttpStatusCode.BadGateway)) { BaseAddress = new Uri("http://localhost/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => new OllamaLlmClient(http)
            .Chat("sensitive-model", [new LlmMessage("user", "private prompt")], CancellationToken.None));
        var span = Assert.Single(seen, activity => activity.DisplayName == "Ollama.Chat");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.StatusDescription);
        Assert.Empty(span.Tags);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity>? seen = null) {
        var listener = new ActivityListener {
            ShouldListenTo = source => source.Name == AegisOpsTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => seen?.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class ReplyHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""{"message":{"content":"reply"},"prompt_eval_count":3,"eval_count":2}"""),
            });
    }
    [Fact]
    public void Durable_job_links_to_request_without_exposing_payload_or_legacy_failures() {
        var seen = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener {
            ShouldListenTo = source => source.Name == "AegisOps",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = seen.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        using (var request = AegisOpsTracing.Source.StartActivity("RequestDeployment")) {
            Assert.NotNull(request);
            var payload = JobTraceContext.DeploymentPayload(Guid.NewGuid(), "safe-correlation");
            var job = Job.Enqueue(JobType.EvaluateDeployment, payload, DateTimeOffset.UtcNow, "safe-correlation");
            using var worker = JobTraceContext.StartJob(job);
            Assert.NotNull(worker);
            Assert.Single(worker.Links);
            Assert.Equal(request.TraceId, worker.Links.Single().Context.TraceId);
            Assert.Equal(request.SpanId, worker.Links.Single().Context.SpanId);
            Assert.Equal("safe-correlation", worker.GetBaggageItem("correlationId"));
            Assert.Equal("safe-correlation", JsonDocument.Parse(payload).RootElement.GetProperty("correlationId").GetString());
            Assert.DoesNotContain(worker.Tags, tag => tag.Key.Contains("deployment", StringComparison.OrdinalIgnoreCase));
        }
        using var legacy = JobTraceContext.StartJob(Job.Enqueue(JobType.ParseScanReport, "{\"scanId\":\"00000000-0000-0000-0000-000000000001\"}", DateTimeOffset.UtcNow, "old"));
        Assert.NotNull(legacy);
        Assert.Empty(legacy.Links);
        using var malformed = JobTraceContext.StartJob(Job.Enqueue(JobType.ParseScanReport, "{bad", DateTimeOffset.UtcNow, "old"));
        Assert.NotNull(malformed);
        Assert.Empty(malformed.Links);
    }
}
