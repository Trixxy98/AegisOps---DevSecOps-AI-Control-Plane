using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AegisOps.Api.Health;

public sealed record SystemDependencyStatus(string Ollama, string Docker, string Worker);

public static class SystemDependencyHealth {
    public static async Task<IResult> Handle(
        HealthCheckService health,
        IHttpClientFactory clients,
        CancellationToken cancellationToken) {
        var api = await health.CheckHealthAsync(check => check.Name == "ollama", cancellationToken);
        var ollama = api.Entries.TryGetValue("ollama", out var entry)
            ? entry.Status.ToString() : "Unknown";
        try {
            using var response = await clients.CreateClient("worker-readiness")
                .GetAsync("health/ready", cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = json.RootElement;
            var worker = root.GetProperty("status").GetString() ?? "Unknown";
            var docker = root.GetProperty("checks").GetProperty("docker")
                .GetProperty("status").GetString() ?? "Unknown";
            return Results.Ok(new SystemDependencyStatus(ollama, docker, worker));
        } catch (Exception) when (!cancellationToken.IsCancellationRequested) {
            return Results.Ok(new SystemDependencyStatus(ollama, "Unknown", "Unreachable"));
        }
    }
}
