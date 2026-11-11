using AegisOps.Domain.Organization;

namespace AegisOps.Application.Tests;

/// <summary>docs/17 §4: Docker targets cannot reach the control plane or the host through their settings.</summary>
public sealed class DeploymentTargetTests {
    [Theory]
    [InlineData("aegisops-staging")]
    [InlineData("aegisops-production")]
    [InlineData(null)]
    public void Dedicated_target_networks_are_accepted(string? network) {
        var target = DeploymentTarget.Docker("circuit-service-staging", network, 8102, 8080, "/health", 60);
        Assert.Equal(network, target.Network);
    }

    [Theory]
    [InlineData("aegisops")] // control plane: PostgreSQL, Redis, API, Ollama
    [InlineData("host")]
    [InlineData("bridge")]
    [InlineData("none")]
    [InlineData("aegisops_default")]
    [InlineData("Aegisops-staging")]
    [InlineData("aegisops-")]
    public void Control_plane_and_docker_builtin_networks_are_rejected(string network) {
        var error = Assert.Throws<ArgumentException>(() =>
            DeploymentTarget.Docker("circuit-service-staging", network, 8102, 8080, "/health", 60));
        Assert.Equal("network", error.ParamName);
        Assert.False(DeploymentTarget.IsAllowedNetwork(network));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("-leading-hyphen")]
    [InlineData("Upper-Case")]
    [InlineData("name with space")]
    [InlineData("name/../escape")]
    [InlineData("under_score")]
    public void Container_names_must_match_the_documented_pattern(string name) {
        var error = Assert.Throws<ArgumentException>(() => DeploymentTarget.Docker(name, "aegisops-staging", 8102, 8080, "/health", 60));
        Assert.Equal("containerName", error.ParamName);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(1023)]
    [InlineData(70000)]
    public void Privileged_and_invalid_host_ports_are_rejected(int port) {
        var error = Assert.Throws<ArgumentException>(() => DeploymentTarget.Docker("circuit-service-staging", "aegisops-staging", port, 8080, "/health", 60));
        Assert.Equal("hostPort", error.ParamName);
    }

    [Theory]
    [InlineData("health")]
    [InlineData("/he alth")]
    public void Health_path_must_be_an_absolute_path_without_spaces(string path) {
        var error = Assert.Throws<ArgumentException>(() => DeploymentTarget.Docker("circuit-service-staging", "aegisops-staging", 8102, 8080, path, 60));
        Assert.Equal("healthPath", error.ParamName);
    }
}
