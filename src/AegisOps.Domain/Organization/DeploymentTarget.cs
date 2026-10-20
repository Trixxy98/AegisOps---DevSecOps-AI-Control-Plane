namespace AegisOps.Domain.Organization;

public sealed class DeploymentTarget {
    public string Type {get; private set;} = "noop";
    public bool AllowApiKeyProduction {get; private set;}
    public string? ContainerName {get; private set;}
    public string? Network {get; private set;}
    public int HostPort {get; private set;}
    public int ContainerPort {get; private set;}
    public string? HealthPath {get; private set;}
    public int HealthTimeoutSeconds {get; private set;}

    private DeploymentTarget() {
    }

    public static DeploymentTarget Noop(bool allowApiKeyProduction = false) {
        return new DeploymentTarget {
            Type = "noop",
            AllowApiKeyProduction = allowApiKeyProduction,
        };
    }

    public static DeploymentTarget Docker(
        string containerName,
        string? network,
        int hostPort,
        int containerPort,
        string? healthPath,
        int healthTimeoutSeconds,
        bool allowApiKeyProduction = false
    ) {
        if (string.IsNullOrWhiteSpace(containerName)) {
            throw new ArgumentException("Container name is required.", nameof(containerName));
        }

        if (hostPort is < 1 or > 65535) {
            throw new ArgumentException("Host port must be between 1 and 65535.", nameof(hostPort));
        }

        if (containerPort is < 1 or > 65535) {
            throw new ArgumentException("Container port must be between 1 and 65535.", nameof(containerPort));
        }

        return new DeploymentTarget {
            Type = "docker",
            AllowApiKeyProduction = allowApiKeyProduction,
            ContainerName = containerName.Trim(),
            Network = string.IsNullOrWhiteSpace(network) ? null : network.Trim(),
            HostPort = hostPort,
            ContainerPort = containerPort,
            HealthPath = string.IsNullOrWhiteSpace(healthPath) ? "/health" : healthPath.Trim(),
            HealthTimeoutSeconds = healthTimeoutSeconds < 1 ? 60 : healthTimeoutSeconds,
        };
    }
}