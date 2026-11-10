using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AegisOps.Api.Observability;
using AegisOps.Application.Observability;
using AegisOps.Infrastructure.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Display;

namespace AegisOps.Api.Tests;

/// <summary>
/// P7-07: the shared Serilog pipeline (AegisOpsLogging + ApiRequestLogging) writes compact JSON with service,
/// environment, CorrelationId and TraceId/SpanId, logs requests by route template, and never leaks credentials,
/// query strings, bodies, emails or prompts.
/// </summary>
// The request test listens on the process-wide AegisOps ActivitySource, like TracingTests.
[Collection("Tracing")]
public sealed partial class StructuredLoggingTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string QuerySecret = "query-secret-7f3a";
    private const string BearerSecret = "bearer-secret-91c2";
    private const string CookieSecret = "cookie-secret-55d0";
    private const string BodyPassword = "body-password-c4e8";
    private const string BodyEmail = "someone.private@example.com";
    private const string Correlation = "corr-structured-logging-1";
    private static readonly string[] SeedSources = ["DevelopmentUserSeed"];

    [Fact]
    public async Task Request_logs_are_bounded_json_with_route_status_elapsed_correlation_and_trace() {
        var sink = new JsonCaptureSink();
        using var listener = new ActivityListener {
            ShouldListenTo = source => source.Name == AegisOpsTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        await using var app = await StartApiAsync(sink);
        using var client = app.GetTestClient();

        var id = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/widgets/{id}?token={QuerySecret}") {
            Content = new StringContent(
                $$"""{"email":"{{BodyEmail}}","password":"{{BodyPassword}}","prompt":"tell me the secret"}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BearerSecret);
        request.Headers.Add("Cookie", $"session={CookieSecret}");
        request.Headers.Add("X-Correlation-Id", Correlation);
        using var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var health = await client.GetAsync("/health/live", Token);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var missing = await client.GetAsync("/api/v1/nope/" + Guid.NewGuid(), Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var events = sink.Events();
        var completed = Assert.Single(events, e => e.Template() == ApiRequestLogging.MessageTemplate && e.Str("RouteTemplate") == "/api/v1/widgets/{id:guid}");
        Assert.Equal("POST", completed.Str("RequestMethod"));
        Assert.Equal(202, completed.Root.GetProperty("StatusCode").GetInt32());
        Assert.True(completed.Root.GetProperty("Elapsed").GetDouble() >= 0);
        Assert.Equal(Correlation, completed.Str("CorrelationId"));
        Assert.Equal("Anonymous", completed.Str("PrincipalType"));
        Assert.Equal(AegisOpsLogging.ApiServiceName, completed.Str("ServiceName"));
        Assert.Equal("Production", completed.Str("Environment"));
        var traceId = completed.Str("TraceId");
        Assert.Matches("^[0-9a-f]{32}$", traceId);
        Assert.Equal(traceId, completed.Str("@tr"));
        Assert.Matches("^[0-9a-f]{16}$", completed.Str("SpanId"));
        Assert.False(completed.Root.TryGetProperty("RequestPath", out _));
        Assert.False(completed.Root.TryGetProperty("QueryString", out _));

        // An application log written inside the request (source-generated LoggerMessage) shares the
        // request's CorrelationId and trace, and its Password argument is masked.
        var handlerLog = Assert.Single(events, e => e.Str("SourceContext") == typeof(StructuredLoggingTests).FullName);
        Assert.Equal(Correlation, handlerLog.Str("CorrelationId"));
        Assert.Equal(traceId, handlerLog.Str("TraceId"));
        Assert.Equal(SensitivePropertyMasker.Redacted, handlerLog.Str("Password"));
        Assert.Equal(id.ToString(), handlerLog.Str("WidgetId"));

        // Unmatched routes are logged by a fixed placeholder, not their raw path; probes are Verbose (dropped).
        Assert.Single(events, e => e.Template() == ApiRequestLogging.MessageTemplate && e.Str("RouteTemplate") == ApiRequestLogging.UnmatchedRoute
            && e.Root.GetProperty("StatusCode").GetInt32() == 404);
        Assert.DoesNotContain(events, e => e.Str("RouteTemplate")?.StartsWith("/health", StringComparison.Ordinal) == true);

        var raw = sink.Text();
        foreach (var forbidden in new[] { QuerySecret, "token=", BearerSecret, "Bearer", CookieSecret, BodyPassword, BodyEmail, "tell me the secret", id.ToString() + "?" })
            Assert.DoesNotContain(forbidden, raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Masking_policy_redacts_sensitive_property_names_at_every_depth() {
        var sink = new JsonCaptureSink();
        using (var logger = BuildLogger(sink, "Production")) {
            logger.ForContext("SourceContext", "AegisOps.Tests.Masking").Information(
                "Login {Password} {ApiKey} {access_token} {ClientSecret} {Authorization} {UserEmail} {Prompt} {Count} {@Login} {Headers} {@Items}",
                "pw-1", "key-2", "tok-3", "sec-4", "Bearer auth-5", "mail6@example.com", "prompt-7", 42,
                new { Username = "alice", Password = "pw-8", Nested = new { RefreshToken = "tok-9" } },
                new Dictionary<string, string> { ["Cookie"] = "cookie-10", ["Accept"] = "application/json" },
                new[] { new { SigningKey = "sk-11" } });
        }

        var logged = Assert.Single(sink.Events());
        foreach (var name in new[] { "Password", "ApiKey", "access_token", "ClientSecret", "Authorization", "UserEmail", "Prompt" })
            Assert.Equal(SensitivePropertyMasker.Redacted, logged.Str(name));
        Assert.Equal(42, logged.Root.GetProperty("Count").GetInt32());
        var credentials = logged.Root.GetProperty("Login");
        Assert.Equal("alice", credentials.GetProperty("Username").GetString());
        Assert.Equal(SensitivePropertyMasker.Redacted, credentials.GetProperty("Password").GetString());
        Assert.Equal(SensitivePropertyMasker.Redacted, credentials.GetProperty("Nested").GetProperty("RefreshToken").GetString());
        Assert.Equal(SensitivePropertyMasker.Redacted, logged.Root.GetProperty("Headers").GetProperty("Cookie").GetString());
        Assert.Equal("application/json", logged.Root.GetProperty("Headers").GetProperty("Accept").GetString());
        Assert.Equal(SensitivePropertyMasker.Redacted, logged.Root.GetProperty("Items")[0].GetProperty("SigningKey").GetString());

        var raw = sink.Text();
        foreach (var secret in new[] { "pw-1", "key-2", "tok-3", "sec-4", "auth-5", "mail6@", "prompt-7", "pw-8", "tok-9", "cookie-10", "sk-11" })
            Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);

        Assert.True(SensitivePropertyMasker.IsSensitive("X-Api-Key"));
        Assert.True(SensitivePropertyMasker.IsSensitive("connection_string"));
        Assert.False(SensitivePropertyMasker.IsSensitive("DeploymentId"));
    }

    [Fact]
    public void Development_disclosure_exemption_applies_only_to_listed_sources_in_Development() {
        foreach (var (environment, expectVisible) in new[] { ("Development", true), ("Production", false) }) {
            var sink = new JsonCaptureSink();
            using (var logger = BuildLogger(sink, environment, SeedSources)) {
                logger.ForContext("SourceContext", "DevelopmentUserSeed").Information("Seed {Password}", "seed-pw");
                logger.ForContext("SourceContext", "Other").Information("Other {Password}", "other-pw");
            }
            Assert.Equal(expectVisible, sink.Text().Contains("seed-pw", StringComparison.Ordinal));
            Assert.DoesNotContain("other-pw", sink.Text(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Worker_logger_configuration_builds_with_service_name_levels_and_job_correlation() {
        var sink = new JsonCaptureSink();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings {
            EnvironmentName = "Production", DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Serilog:MinimumLevel:Override:AegisOps.Noisy"] = "Error",
        });
        builder.Services.AddAegisOpsSerilog(builder.Configuration, builder.Environment,
            new AegisOpsLoggingOptions(AegisOpsLogging.WorkerServiceName) { Sinks = sinks => sinks.Sink(sink) });
        using var host = builder.Build();
        await host.StartAsync(Token);

        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        var jobs = factory.CreateLogger("AegisOps.Worker.Jobs");
        var framework = factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
        var noisy = factory.CreateLogger("AegisOps.Noisy");
        using (var span = new Activity("Worker.Job").SetIdFormat(ActivityIdFormat.W3C).Start()) {
            span.SetBaggage("correlationId", "job-correlation-1");
            LogJobClaimed(jobs, 7, "worker-key-secret");
        }
        LogFrameworkChatter(framework);
        LogFrameworkWarning(framework);
        LogConfiguredOverride(noisy);
        await host.StopAsync(Token);

        var events = sink.Events();
        var job = Assert.Single(events, e => e.Str("SourceContext") == "AegisOps.Worker.Jobs");
        Assert.Equal(AegisOpsLogging.WorkerServiceName, job.Str("ServiceName"));
        Assert.Equal("Production", job.Str("Environment"));
        Assert.Equal("job-correlation-1", job.Str("CorrelationId"));
        Assert.Matches("^[0-9a-f]{32}$", job.Str("TraceId"));
        Assert.Equal(SensitivePropertyMasker.Redacted, job.Str("ApiKey"));
        Assert.DoesNotContain("worker-key-secret", sink.Text(), StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Template() == "framework chatter");
        Assert.Contains(events, e => e.Template() == "framework warning");
        Assert.DoesNotContain(events, e => e.Template() == "configured override");
    }

    [Fact]
    public void Console_is_compact_json_outside_Development_and_readable_in_Development() {
        Assert.IsType<CompactJsonFormatter>(AegisOpsLogging.ConsoleFormatter("Production"));
        Assert.IsType<CompactJsonFormatter>(AegisOpsLogging.ConsoleFormatter("Staging"));
        Assert.IsType<MessageTemplateTextFormatter>(AegisOpsLogging.ConsoleFormatter("Development"));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Widget {WidgetId} accepted (Password {Password})")]
    private static partial void LogWidgetAccepted(ILogger logger, Guid widgetId, string password);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} claimed with {ApiKey}")]
    private static partial void LogJobClaimed(ILogger logger, int jobId, string apiKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "framework chatter")]
    private static partial void LogFrameworkChatter(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "framework warning")]
    private static partial void LogFrameworkWarning(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "configured override")]
    private static partial void LogConfiguredOverride(ILogger logger);

    private sealed record WidgetBody(string Email, string Password, string Prompt);

    private static async Task<WebApplication> StartApiAsync(JsonCaptureSink sink) {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAegisOpsSerilog(builder.Configuration, builder.Environment,
            new AegisOpsLoggingOptions(AegisOpsLogging.ApiServiceName) { Sinks = sinks => sinks.Sink(sink) });
        var app = builder.Build();
        app.UseApiRequestTracing();
        app.UseApiRequestLogging();
        app.MapGet("/health/live", () => Results.Ok());
        app.MapPost("/api/v1/widgets/{id:guid}", (Guid id, WidgetBody body, ILogger<StructuredLoggingTests> logger) => {
            LogWidgetAccepted(logger, id, body.Password);
            return Results.Accepted();
        });
        await app.StartAsync(Token);
        return app;
    }

    private static Logger BuildLogger(JsonCaptureSink sink, string environment, string[]? disclosureSources = null) {
        var configuration = new ConfigurationBuilder().Build();
        return AegisOpsLogging.Configure(new Serilog.LoggerConfiguration(), configuration, environment,
            new AegisOpsLoggingOptions(AegisOpsLogging.ApiServiceName) {
                Sinks = sinks => sinks.Sink(sink),
                DevelopmentDisclosureSources = disclosureSources ?? [],
            }).CreateLogger();
    }

    /// <summary>Formats each event with the production <see cref="CompactJsonFormatter"/> into memory.</summary>
    private sealed class JsonCaptureSink : ILogEventSink {
        private readonly ITextFormatter _formatter = AegisOpsLogging.ConsoleFormatter("Production");
        private readonly StringBuilder _buffer = new();
        private readonly Lock _gate = new();

        public void Emit(LogEvent logEvent) {
            lock (_gate) {
                using var writer = new StringWriter(_buffer, CultureInfo.InvariantCulture);
                _formatter.Format(logEvent, writer);
            }
        }

        public string Text() {
            lock (_gate) return _buffer.ToString();
        }

        public LoggedEvent[] Events() =>
            Text().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => new LoggedEvent(JsonDocument.Parse(line).RootElement.Clone())).ToArray();
    }

    private sealed record LoggedEvent(JsonElement Root) {
        public string? Str(string name) => Root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        public string? Template() => Str("@mt");
    }
}
