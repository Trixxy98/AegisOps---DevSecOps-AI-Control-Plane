using AegisOps.Api.Identity;
using AegisOps.Infrastructure.Identity;
using AegisOps.Application.Authorization;
using AegisOps.Api.Organization;
using AegisOps.Api.Deploy;
using AegisOps.Infrastructure.Deploy;



var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddIdentityStore(builder.Configuration);
builder.Services.AddAuthRateLimiting();
builder.Services.AddHostedService<DeploymentJobService>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Urls.Add("http://localhost:8080");

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapPost("/api/v1/auth/login", Login.Handle).RequireRateLimiting(AuthRateLimitingExtensions.PolicyName);
app.MapPost("/api/v1/auth/refresh", Refresh.Handle).RequireRateLimiting(AuthRateLimitingExtensions.PolicyName);
app.MapPost("/api/v1/auth/logout", Logout.Handle).RequireAuthorization();
app.MapGet("/api/v1/auth/me", CurrentUser.Handle).RequireAuthorization();
app.MapPost("/api/v1/auth/change-password", ChangePassword.Handle).RequireAuthorization();
app.MapGet("/api/v1/admin/ping", () => Results.NoContent())
    .RequireAuthorization(Permissions.UsersManage);
app.MapGet("/api/v1/admin/users", ListUsers.Handle)
    .RequireAuthorization(Permissions.UsersManage);
app.MapGet("/api/v1/teams", ListTeams.Handle)
    .RequireAuthorization(Permissions.TeamsRead);
app.MapPost("/api/v1/teams", CreateTeam.Handle)
    .RequireAuthorization(Permissions.UsersManage);
app.MapGet("/api/v1/teams/{slug}", GetTeam.Handle)
    .RequireAuthorization(Permissions.TeamsRead);
app.MapPatch("/api/v1/teams/{slug}", UpdateTeam.Handle)
    .RequireAuthorization(Permissions.TeamsWrite);
app.MapPut("/api/v1/teams/{slug}/members/{userId:guid}", UpsertTeamMember.Handle)
    .RequireAuthorization(Permissions.TeamsWrite);
app.MapDelete("/api/v1/teams/{slug}/members/{userId:guid}", RemoveTeamMember.Handle)
    .RequireAuthorization(Permissions.TeamsWrite);
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
app.MapGet("/api/v1/deployments/{id:guid}", DeploymentEndpoints.Get)
    .RequireAuthorization(Permissions.DeploymentsRead);
app.MapGet("/api/v1/deployments/{id:guid}/events", DeploymentEndpoints.Events)
    .RequireAuthorization(Permissions.DeploymentsRead);
app.MapGet("/api/v1/deployments/{id:guid}/evaluation", DeploymentEndpoints.Evaluation)
    .RequireAuthorization(Permissions.DeploymentsRead);
app.MapPost("/api/v1/deployments/{id:guid}/approvals", DeploymentEndpoints.Decide)
    .RequireAuthorization(Permissions.DeploymentsApprove);
app.MapPost("/api/v1/deployments/{id:guid}/cancel", DeploymentEndpoints.Cancel)
    .RequireAuthorization(Permissions.DeploymentsCancel);
app.MapGet("/api/v1/audit", PolicyPhase1Endpoints.Audit)
    .RequireAuthorization(Permissions.AuditRead);

if (app.Environment.IsDevelopment()) {
    await DevelopmentUserSeed.SeedAsync(app.Services);
}

app.Run();