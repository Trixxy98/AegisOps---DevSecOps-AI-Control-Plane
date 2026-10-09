using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Identity;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddIdentityStore(builder.Configuration);
builder.Services.AddHostedService<DeploymentJobService>();

var app = builder.Build();
app.Urls.Add("http://localhost:8081");

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.Run();
