using AegisOps.Application.Audit;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Health;
using AegisOps.Infrastructure.Observability;
using OpenTelemetry.Metrics;
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
builder.Services.AddHostedService<DeploymentJobService>();
builder.Services.AddHostedService<JobQueueDepthService>();
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
    .AddMeter(JobMetrics.MeterName)
    .AddView("aegisops_jobs_duration_seconds", new ExplicitBucketHistogramConfiguration {
        Boundaries = JobMetrics.DurationBuckets.ToArray(),
    })
    .AddPrometheusExporter());

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapPrometheusScrapingEndpoint();
app.MapHealthChecks("/health/ready", new HealthCheckOptions {
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = ReadinessResponse.WriteAsync,
});

app.Run();
