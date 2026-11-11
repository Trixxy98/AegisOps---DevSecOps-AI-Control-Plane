using System.Text.RegularExpressions;

namespace AegisOps.Domain.Organization;

public sealed partial class DeploymentTarget {
    /// <summary>Docker container names the executor may create or replace (docs/17 §4).</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{2,62}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ContainerNamePattern();

    /// <summary>Target networks are the dedicated `aegisops-<name>` networks (docs/13, docs/14). This excludes the
    /// control-plane network `aegisops` (PostgreSQL, Redis, API, Ollama) and Docker's host/bridge/none networks.</summary>
    [GeneratedRegex("^aegisops-[a-z0-9][a-z0-9-]{0,40}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NetworkPattern();

    public static bool IsAllowedNetwork(string? network) => network is null || NetworkPattern().IsMatch(network);

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

        if (!ContainerNamePattern().IsMatch(containerName.Trim())) {
            throw new ArgumentException("Container name must be 3-63 lowercase letters, digits or hyphens.", nameof(containerName));
        }

        var normalizedNetwork = string.IsNullOrWhiteSpace(network) ? null : network.Trim();
        if (!IsAllowedNetwork(normalizedNetwork)) {
            throw new ArgumentException("Network must be a dedicated target network named aegisops-<name> (for example aegisops-staging).", nameof(network));
        }

        // Privileged ports and the control-plane ports on the host are not deployment targets.
        if (hostPort is < 1024 or > 65535) {
            throw new ArgumentException("Host port must be between 1024 and 65535.", nameof(hostPort));
        }

        if (healthPath is not null && healthPath.Trim().Length > 0 &&
            (!healthPath.Trim().StartsWith('/') || healthPath.Trim().Length > 200 || healthPath.Trim().Any(char.IsWhiteSpace))) {
            throw new ArgumentException("Health path must start with '/' and contain no spaces.", nameof(healthPath));
        }

        if (containerPort is < 1 or > 65535) {
            throw new ArgumentException("Container port must be between 1 and 65535.", nameof(containerPort));
        }

        return new DeploymentTarget {
            Type = "docker",
            AllowApiKeyProduction = allowApiKeyProduction,
            ContainerName = containerName.Trim(),
            Network = normalizedNetwork,
            HostPort = hostPort,
            ContainerPort = containerPort,
            HealthPath = string.IsNullOrWhiteSpace(healthPath) ? "/health" : healthPath.Trim(),
            HealthTimeoutSeconds = healthTimeoutSeconds < 1 ? 60 : healthTimeoutSeconds,
        };
    }
}