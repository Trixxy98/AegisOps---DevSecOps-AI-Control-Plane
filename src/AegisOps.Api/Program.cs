using AegisOps.Application.Audit;
using AegisOps.Infrastructure.Audit;
using AegisOps.Api.Identity;
using AegisOps.Api.Health;
using AegisOps.Api.Observability;
using AegisOps.Infrastructure.Identity;
using AegisOps.Application.Authorization;
using AegisOps.Application.Idempotency;
using AegisOps.Api.Organization;
using AegisOps.Api.Deploy;
using AegisOps.Api.Jobs;
using AegisOps.Api.Ai;
using AegisOps.Infrastructure.Ai;
using AegisOps.Domain.Ai;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Redis;
using AegisOps.Infrastructure.Persistence;
using AegisOps.Infrastructure.Health;
using AegisOps.Infrastructure.Observability;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using AegisOps.Application.Observability;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using AegisOps.Api.Hosting;

// One-shot operator commands (`migrate`, `bootstrap-admin`) run instead of the web host.
if (AdminCommands.IsCommand(args)) {
    return await AdminCommands.RunAsync(args, Console.Out, Console.Error);
}

var builder = WebApplication.CreateBuilder(args);
ProductionSafety.ThrowIfUnsafe(builder.Configuration, builder.Environment);
builder.Services.AddAegisOpsSerilog(builder.Configuration, builder.Environment, new AegisOpsLoggingOptions(AegisOpsLogging.ApiServiceName) {
    DevelopmentDisclosureSources = [nameof(DevelopmentUserSeed)],
});
builder.Services.AddDependencyReadiness()
    .AddCheck<OllamaReadinessCheck>("ollama", failureStatus: HealthStatus.Degraded,
        tags: ["ready"], timeout: TimeSpan.FromSeconds(3));
builder.Services.AddHttpClient<OllamaReadinessCheck>(client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHttpClient("worker-readiness", client => {
    client.BaseAddress = new Uri(builder.Configuration["Worker:HealthBaseUrl"] ?? "http://worker:8081/");
    client.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddIdentityStore(builder.Configuration);
builder.Services.AddScoped<IAuditWriter, EfAuditWriter>();
builder.Services.AddSingleton<TeamWebhookSecretProtector>();
builder.Services.AddSingleton<EnvironmentSecretProtector>();
builder.Services.AddSingleton<ISensitiveDataDetector, RegexSensitiveDataDetector>();
builder.Services.AddSingleton<IAiRateLimiter>(services => new RedisAiRateLimiter(
    services.GetService<IConnectionMultiplexer>(), services.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<AiGateway>();
builder.Services.AddHttpClient<ILlmClient, OllamaLlmClient>((services, client) => {
    var configuration = services.GetRequiredService<IConfiguration>();
    var baseUrl = configuration["Ai:Ollama:BaseUrl"] ?? "http://ollama:11434/";
    if (!baseUrl.EndsWith('/')) baseUrl += "/";
    client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Ai:Ollama:TimeoutSeconds", 120), 1, 600));
});
builder.Services.AddSignalR();
builder.Services.AddApiMetrics();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("aegisops-api"))
    .WithTracing(traces => {
        // ASP.NET can supply an unsampled ambient parent; still export bounded AegisOps spans.
        traces.AddSource(AegisOpsTracing.SourceName).SetSampler(new AlwaysOnSampler());
        if (builder.Environment.IsDevelopment()) traces.AddConsoleExporter();
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) traces.AddOtlpExporter();
    });
builder.Services.AddSingleton<DeploymentConnections>();
builder.Services.AddAuthRateLimiting();
builder.Services.AddAegisOpsForwardedHeaders(builder.Configuration);
builder.Services.AddApiProblemDetails();
var redis = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redis)) {
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ => {
        var options = ConfigurationOptions.Parse(redis);
        options.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(options);
    });
    builder.Services.AddHostedService<DeploymentUpdateListener>();
}

builder.Services.AddSingleton<IIdempotencyStore>(services =>
    new RedisIdempotencyStore(services.GetService<IConnectionMultiplexer>()));
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options => {
    options.MultipartBodyLengthLimit = UploadScan.MaxBytes;
});

var app = builder.Build();
// First: logging, metrics, authentication and the auth rate limiter must see the real client (docs/17).
app.UseAegisOpsForwardedHeaders(app.Configuration);
app.UseApiExceptionHandler();
// Activate the listener before the first authenticated request creates an Activity.
_ = app.Services.GetRequiredService<TracerProvider>();
app.UseApiMetrics();
app.UseApiRequestTracing();
app.UseApiRequestLogging();
app.UseAuthentication();
app.UsePasswordChangeGate();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions {
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = ReadinessResponse.WriteAsync,
});
app.MapHub<OpsHub>("/hubs/ops", options => options.CloseOnAuthenticationExpiration = true);
app.MapPost("/api/v1/auth/login", Login.Handle).RequireRateLimiting(AuthRateLimitingExtensions.PolicyName);
app.MapPost("/api/v1/auth/refresh", Refresh.Handle).RequireRateLimiting(AuthRateLimitingExtensions.PolicyName);
app.MapPost("/api/v1/auth/logout", Logout.Handle).RequireAuthorization();
app.MapGet("/api/v1/auth/me", CurrentUser.Handle).RequireAuthorization();
app.MapGet("/api/v1/system/dependencies", SystemDependencyHealth.Handle).RequireAuthorization();
app.MapPost("/api/v1/auth/change-password", ChangePassword.Handle).RequireAuthorization();
app.MapPost("/api/v1/ai/chat", AiEndpoints.Chat).RequireAuthorization(Permissions.AiChat);
app.MapGet("/api/v1/ai/models", AiEndpoints.Models).RequireAuthorization(Permissions.AiChat);
app.MapGet("/api/v1/ai/requests", AiEndpoints.Requests).RequireAuthorization(Permissions.AiRequestsRead);
app.MapGet("/api/v1/ai/policies", AiEndpoints.Policies).RequireAuthorization("ai:policies:read");
app.MapPut("/api/v1/ai/policies", AiEndpoints.SavePolicy).RequireAuthorization(Permissions.AiPoliciesWrite);
app.MapPost("/api/v1/admin/ai/models/sync", AiEndpoints.SyncModels).RequireAuthorization(Permissions.ModelsManage);
app.MapGet("/api/v1/admin/ping", () => Results.NoContent())
    .RequireAuthorization(Permissions.UsersManage);
app.MapGet("/api/v1/admin/users", ListUsers.Handle)
    .RequireAuthorization(Permissions.UsersManage);
app.MapPost("/api/v1/admin/users/{id:guid}/reset-password", ResetUserPassword.Handle)
    .RequireAuthorization(Permissions.UsersManage);
app.MapGet("/api/v1/teams", ListTeams.Handle)
    .RequireAuthorization(Permissions.TeamsRead);
app.MapPost("/api/v1/teams", CreateTeam.Handle)
    .RequireAuthorization(Permissions.UsersManage);
app.MapGet("/api/v1/teams/{slug}", GetTeam.Handle)
    .RequireAuthorization(Permissions.TeamsRead);
app.MapPatch("/api/v1/teams/{slug}", UpdateTeam.Handle)
    .RequireAuthorization(Permissions.TeamsWrite); // handler: Admin or Owner of this team (docs/09)
app.MapGet("/api/v1/teams/{slug}/webhook", TeamWebhookSettingsEndpoints.Get)
    .RequireAuthorization(Permissions.TeamsWrite); // handler: Admin or Owner of this team (docs/09)
app.MapGet("/api/v1/teams/{slug}/webhook/deliveries", TeamWebhookSettingsEndpoints.Deliveries)
    .RequireAuthorization(Permissions.TeamsWrite); // handler: Admin or Owner of this team (docs/09)
app.MapGet("/api/v1/teams/{slug}/webhook/summary", TeamWebhookSettingsEndpoints.Summary)
    .RequireAuthorization(Permissions.TeamsWrite); // handler: Admin or Owner of this team (docs/09)
app.MapPost("/api/v1/teams/{slug}/webhook/deliveries/{eventId:guid}/replay", TeamWebhookSettingsEndpoints.Replay)
    .RequireAuthorization(Permissions.TeamsWrite); // handler: Admin or Owner of this team (docs/09)
app.MapPut("/api/v1/teams/{slug}/webhook", TeamWebhookSettingsEndpoints.Set)
    .RequireAuthorization(Permissions.TeamsWrite); // handler: Admin or Owner of this team (docs/09)
app.MapPost("/api/v1/admin/webhooks/rewrap", TeamWebhookSettingsEndpoints.Rewrap)
    .RequireAuthorization(Permissions.UsersManage);
app.MapPut("/api/v1/teams/{slug}/members/{userId:guid}", UpsertTeamMember.Handle)
    .RequireAuthorization(Permissions.TeamsWrite); // handler: Admin or Owner of this team (docs/09)
app.MapDelete("/api/v1/teams/{slug}/members/{userId:guid}", RemoveTeamMember.Handle)
    .RequireAuthorization(Permissions.TeamsWrite); // handler: Admin or Owner of this team (docs/09)
app.MapPost("/api/v1/projects", CreateProject.Handle)
    .RequireAuthorization(Permissions.ProjectsWrite);
app.MapGet("/api/v1/projects", ListProjects.Handle)
    .RequireAuthorization(Permissions.ProjectsRead);
app.MapGet("/api/v1/projects/{slug}", GetProject.Handle)
    .RequireAuthorization(Permissions.ProjectsRead);
app.MapPatch("/api/v1/projects/{slug}", UpdateProject.Handle)
    .RequireAuthorization(Permissions.ProjectsWrite);
app.MapPut("/api/v1/projects/{slug}/repository", UpsertRepository.Handle)
    .RequireAuthorization(Permissions.ProjectsWrite);
app.MapPatch("/api/v1/projects/{slug}/environments/{envId:guid}", UpdateEnvironment.Handle)
    .RequireAuthorization(Permissions.EnvironmentsWrite);
app.MapGet("/api/v1/projects/{slug}/environments/{envId:guid}/runtime", GetEnvironmentRuntime.Handle)
    .RequireAuthorization(Permissions.ProjectsRead);
app.MapGet("/api/v1/projects/{slug}/environments/{envId:guid}/variables", EnvironmentVariableEndpoints.List)
    .RequireAuthorization(Permissions.ProjectsRead);
app.MapPut("/api/v1/projects/{slug}/environments/{envId:guid}/variables/{name}", EnvironmentVariableEndpoints.Set)
    .RequireAuthorization(Permissions.EnvironmentsWrite);
app.MapDelete("/api/v1/projects/{slug}/environments/{envId:guid}/variables/{name}", EnvironmentVariableEndpoints.Remove)
    .RequireAuthorization(Permissions.EnvironmentsWrite);
app.MapPost("/api/v1/projects/{slug}/api-keys", CreateApiKey.Handle)
    .RequireAuthorization(Permissions.ApiKeysManage);
app.MapGet("/api/v1/projects/{slug}/api-keys", ListApiKeys.Handle)
    .RequireAuthorization(Permissions.ApiKeysManage);
app.MapDelete("/api/v1/projects/{slug}/api-keys/{keyId:guid}", RevokeApiKey.Handle)
    .RequireAuthorization(Permissions.ApiKeysManage);
app.MapPost("/api/v1/projects/{slug}/artifacts", CreateArtifact.Handle)
    .RequireAuthorization(Permissions.ArtifactsWrite);
app.MapGet("/api/v1/projects/{slug}/artifacts", ListArtifacts.Handle)
    .RequireAuthorization(Permissions.ArtifactsRead);
app.MapGet("/api/v1/artifacts/{id:guid}", GetArtifact.Handle)
    .RequireAuthorization(Permissions.ArtifactsRead);
app.MapPatch("/api/v1/artifacts/{id:guid}", BackfillArtifactDigest.Handle)
    .RequireAuthorization(Permissions.ArtifactsWrite);
app.MapPost("/api/v1/artifacts/{id:guid}/scans", UploadScan.Handle)
    .DisableAntiforgery()
    .RequireAuthorization(Permissions.ScansWrite);
app.MapGet("/api/v1/artifacts/{id:guid}/findings", ListFindings.Handle)
    .RequireAuthorization(Permissions.FindingsRead);
app.MapGet("/api/v1/projects/{slug}/findings", ListProjectFindings.Handle)
    .RequireAuthorization(Permissions.FindingsRead); // handler: team scoping, Admin/Security see all (docs/09)
app.MapPost("/api/v1/findings/{id:guid}/ai-explain", AiEndpoints.ExplainFinding)
    .RequireAuthorization(Permissions.FindingsRead)
    .RequireAuthorization(Permissions.AiChat);
app.MapPost("/api/v1/projects/{slug}/finding-suppressions", SuppressFinding.Handle)
    .RequireAuthorization(Permissions.FindingsSuppress);
app.MapPost("/api/v1/policies", CreatePolicy.Handle)
    .RequireAuthorization(Permissions.PoliciesWrite);
app.MapGet("/api/v1/policies/rule-types", PolicyPhase1Endpoints.RuleTypes)
    .RequireAuthorization(Permissions.PoliciesRead);
app.MapPost("/api/v1/policies/simulate", PolicyPhase1Endpoints.Simulate)
    .RequireAuthorization(Permissions.PoliciesRead);
app.MapGet("/api/v1/policies", ListPolicies.Handle)
    .RequireAuthorization(Permissions.PoliciesRead);
app.MapGet("/api/v1/policies/{id:guid}", GetPolicy.Handle)
    .RequireAuthorization(Permissions.PoliciesRead);
app.MapPatch("/api/v1/policies/{id:guid}", SetPolicyEnabled.Handle)
    .RequireAuthorization(Permissions.PoliciesWrite);
app.MapDelete("/api/v1/policies/{id:guid}", ArchivePolicy.Handle)
    .RequireAuthorization(Permissions.PoliciesWrite);
app.MapPut("/api/v1/policies/{id:guid}", PolicyPhase1Endpoints.Replace)
    .RequireAuthorization(Permissions.PoliciesWrite);
app.MapPost("/api/v1/deployments", DeploymentEndpoints.Request)
    .RequireAuthorization(Permissions.DeploymentsRequest);
app.MapGet("/api/v1/deployments", DeploymentEndpoints.List)
    .RequireAuthorization(Permissions.DeploymentsRead);
app.MapGet("/api/v1/deployments/approval-queue", DeploymentEndpoints.ApprovalQueue)
    .RequireAuthorization(Permissions.DeploymentsApprove);
app.MapGet("/api/v1/deployments/{id:guid}", DeploymentEndpoints.Get)
    .RequireAuthorization(Permissions.DeploymentsRead);
app.MapGet("/api/v1/deployments/{id:guid}/events", DeploymentEndpoints.Events)
    .RequireAuthorization(Permissions.DeploymentsRead);
app.MapGet("/api/v1/deployments/{id:guid}/evaluation", DeploymentEndpoints.Evaluation)
    .RequireAuthorization(Permissions.DeploymentsRead);
app.MapPost("/api/v1/deployments/{id:guid}/ai-summary", AiEndpoints.SummarizeDeployment)
    .RequireAuthorization(Permissions.DeploymentsRead)
    .RequireAuthorization(Permissions.AiChat);
app.MapPost("/api/v1/deployments/{id:guid}/approvals", DeploymentEndpoints.Decide)
    .RequireAuthorization(Permissions.DeploymentsApprove);
app.MapPost("/api/v1/deployments/{id:guid}/cancel", DeploymentEndpoints.Cancel)
    .RequireAuthorization(Permissions.DeploymentsCancel);
app.MapPost("/api/v1/deployments/{id:guid}/rollback", DeploymentEndpoints.Rollback)
    .RequireAuthorization(Permissions.DeploymentsRequest);
app.MapGet("/api/v1/admin/jobs", AdminJobs.List)
    .RequireAuthorization(Permissions.UsersManage);
app.MapGet("/api/v1/admin/jobs/summary", AdminJobs.Summary)
    .RequireAuthorization(Permissions.UsersManage);
app.MapPost("/api/v1/admin/jobs/{id:guid}/retry", AdminJobs.Retry)
    .RequireAuthorization(Permissions.UsersManage);
app.MapGet("/api/v1/audit", PolicyPhase1Endpoints.Audit)
    .RequireAuthorization(Permissions.AuditRead);

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup")) {
    if (!app.Environment.IsDevelopment()) {
        throw new InvalidOperationException(
            "Automatic migrations are only supported in Development. Run the `migrate` command instead (docs/deploy-production.md).");
    }
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>().Database.MigrateAsync();
}

if (app.Environment.IsDevelopment()) {
    await DevelopmentUserSeed.SeedAsync(app.Services);
}

app.Run();
return 0;
