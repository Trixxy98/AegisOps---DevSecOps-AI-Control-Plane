using AegisOps.Api.Identity;
using AegisOps.Infrastructure.Identity;
using AegisOps.Application.Authorization;
using AegisOps.Api.Organization;



var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddIdentityStore(builder.Configuration);
builder.Services.AddAuthRateLimiting();

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

if (app.Environment.IsDevelopment()) {
    await DevelopmentUserSeed.SeedAsync(app.Services);
}

app.Run();