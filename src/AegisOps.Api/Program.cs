var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.Urls.Add("http://localhost:8080");

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.Run();