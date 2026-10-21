using Docker.DotNet.Models;

namespace AegisOps.Infrastructure.Docker;

public sealed record ContainerRuntime(
    string Name,
    string Id,
    string State,
    string Image,
    IReadOnlyList<string> Ports,
    IReadOnlyDictionary<string, string> Labels
);

public static class DockerRuntimeInspector {
    public static async Task<IReadOnlyList<ContainerRuntime>> ListAsync(
        string projectSlug,
        string environmentName,
        string? containerName,
        CancellationToken cancellationToken
    ) {
        using var client = DockerClientFactory.Create();
        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, cancellationToken);
        return containers
            .Where(container => Matches(container, projectSlug, environmentName, containerName))
            .Select(container => new ContainerRuntime(
                container.Names.Select(name => name.TrimStart('/')).FirstOrDefault() ?? container.ID,
                container.ID,
                container.State,
                container.Image,
                container.Ports.Select(port => $"{port.IP}:{port.PublicPort}->{port.PrivatePort}/{port.Type}").ToArray(),
                container.Labels ?? new Dictionary<string, string>()
            ))
            .ToArray();
    }

    private static bool Matches(ContainerListResponse container, string projectSlug, string environmentName, string? containerName) {
        var labels = container.Labels ?? new Dictionary<string, string>();
        if (labels.TryGetValue("aegisops.project", out var project)
            && labels.TryGetValue("aegisops.environment", out var environment)
            && string.Equals(project, projectSlug, StringComparison.OrdinalIgnoreCase)
            && string.Equals(environment, environmentName, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return !string.IsNullOrWhiteSpace(containerName)
            && container.Names.Any(name => string.Equals(name.TrimStart('/'), containerName, StringComparison.Ordinal));
    }
}
