using AegisOps.Application.Audit;
using AegisOps.Application.Observability;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Health;
using AegisOps.Infrastructure.Observability;
using AegisOps.Worker.Observability;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDependencyReadiness()
    .AddCheck<DockerReadinessCheck>("docker", failureStatus: HealthStatus.Degraded,
        tags: ["ready"], timeout: TimeSpan.FromSeconds(3))
    .AddCheck<WorkerPollReadinessCheck>("worker_poll", tags: ["ready"]);
builder.Services.AddSingleton<WorkerPollHeartbeat>();
builder.Services.AddIdentityStore(builder.Configuration);
builder.Services.AddScoped<IAuditWriter, EfAuditWriter>();
var redis = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redis)) builder.Services.AddSingleton<IConnectionMultiplexer>(_ => {
    var options = ConfigurationOptions.Parse(redis);
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddHostedService<JobMetricsSeedService>();
builder.Services.AddHostedService<DeploymentJobService>();
builder.Services.AddHostedService<JobQueueDepthService>();
builder.Services.AddHostedService<JobCleanupService>();
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
    .AddMeter(JobMetrics.MeterName)
    .AddView("aegisops_jobs_duration_seconds", new ExplicitBucketHistogramConfiguration {
        Boundaries = JobMetrics.DurationBuckets.ToArray(),
    })
    .AddPrometheusExporter());
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("aegisops-worker"))
    .WithTracing(traces => {
        // Worker spans must be exported even when linked to an unsampled API request.
        traces.AddSource(AegisOpsTracing.SourceName).SetSampler(new AlwaysOnSampler());
        if (builder.Environment.IsDevelopment()) traces.AddConsoleExporter();
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) traces.AddOtlpExporter();
    });

var app = builder.Build();
// Start tracing before the deployment job hosted service can claim queued work.
_ = app.Services.GetRequiredService<TracerProvider>();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapPrometheusScrapingEndpoint();
app.MapHealthChecks("/health/ready", new HealthCheckOptions {
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = ReadinessResponse.WriteAsync,
});

app.Run();
