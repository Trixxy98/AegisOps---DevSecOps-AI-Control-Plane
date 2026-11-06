using AegisOps.Infrastructure.Docker;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AegisOps.Infrastructure.Health;

public sealed class OllamaReadinessCheck(HttpClient client, IConfiguration configuration) : IHealthCheck {
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) {
        try {
            var url = configuration["Ai:Ollama:BaseUrl"] ?? "http://ollama:11434/";
            using var response = await client.GetAsync(new Uri(new Uri(url.TrimEnd('/') + "/"), "api/tags"), cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded("Ollama is unavailable.");
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            return HealthCheckResult.Degraded("Ollama is unavailable.", exception);
        }
    }
}

public sealed class DockerReadinessCheck : IHealthCheck {
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) {
        try {
            if (!DockerClientFactory.CanInspect(DockerClientFactory.Socket()))
                return HealthCheckResult.Degraded("Docker socket is unavailable.");
            using var client = DockerClientFactory.Create();
            await client.Containers.ListContainersAsync(new ContainersListParameters(), cancellationToken);
            return HealthCheckResult.Healthy();
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            return HealthCheckResult.Degraded("Docker daemon is unavailable.", exception);
        }
    }
}
