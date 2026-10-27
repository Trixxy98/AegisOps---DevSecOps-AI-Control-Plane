using Docker.DotNet;

namespace AegisOps.Infrastructure.Docker;

public static class DockerClientFactory {
    public static DockerClient Create() {
        return new DockerClientConfiguration(Socket()).CreateClient();
    }

    public static Uri Socket() {
        var host = System.Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(host)) {
            return new Uri(host);
        }

        var mac = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".docker", "run", "docker.sock");
        return File.Exists(mac) ? new Uri("unix://" + mac) : new Uri("unix:///var/run/docker.sock");
    }

    public static bool CanInspect(Uri endpoint) =>
        !string.Equals(endpoint.Scheme, "unix", StringComparison.OrdinalIgnoreCase)
        || File.Exists(endpoint.LocalPath);
}
