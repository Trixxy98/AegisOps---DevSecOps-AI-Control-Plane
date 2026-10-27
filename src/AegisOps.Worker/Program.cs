using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Identity;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddIdentityStore(builder.Configuration);
var redis = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redis)) builder.Services.AddSingleton<IConnectionMultiplexer>(_ => {
    var options = ConfigurationOptions.Parse(redis);
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddHostedService<DeploymentJobService>();

var app = builder.Build();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.Run();
