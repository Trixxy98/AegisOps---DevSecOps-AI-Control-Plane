using System.Net;
using System.Text.Json;
using AegisOps.Api.Health;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AegisOps.Api.Tests;

public sealed class SystemDependencyHealthTests {
    [Fact]
    public async Task Degraded_ollama_and_worker_docker_are_returned_to_ui() {
        using var services = new ServiceCollection().AddLogging()
            .AddHealthChecks().AddCheck("ollama", () => HealthCheckResult.Degraded("Ollama is unavailable."))
            .Services.BuildServiceProvider();
        using var client = new HttpClient(new StubHandler()) { BaseAddress = new Uri("http://worker:8081/") };
        var result = await SystemDependencyHealth.Handle(
            services.GetRequiredService<HealthCheckService>(), new StubFactory(client),
            TestContext.Current.CancellationToken);
        var response = Assert.IsType<SystemDependencyStatus>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal("Degraded", response.Ollama);
        Assert.Equal("Degraded", response.Docker);
        Assert.Equal("Healthy", response.Worker);
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Assert.Equal("http://worker:8081/health/ready", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(new {
                    status = "Healthy", checks = new { docker = new { status = "Degraded" } },
                })),
            });
        }
    }
}
