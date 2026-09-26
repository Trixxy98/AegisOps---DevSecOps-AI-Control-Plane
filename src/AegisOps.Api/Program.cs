using AegisOps.Api.Identity;
using AegisOps.Infrastructure.Identity;


var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddIdentityStore(builder.Configuration);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.Urls.Add("http://localhost:8080");

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapPost("/api/v1/auth/login", Login.Handle);
app.MapPost("/api/v1/auth/refresh", Refresh.Handle);
app.MapPost("/api/v1/auth/logout", Logout.Handle).RequireAuthorization();
app.MapGet("/api/v1/auth/me", CurrentUser.Handle).RequireAuthorization();


if (app.Environment.IsDevelopment()) {
    await DevelopmentUserSeed.SeedAsync(app.Services);
}

app.Run();