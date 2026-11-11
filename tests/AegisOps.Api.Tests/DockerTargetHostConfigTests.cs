using Docker.DotNet.Models;
using AegisOps.Infrastructure.Docker;

namespace AegisOps.Api.Tests;

public sealed class DockerTargetHostConfigTests {
    [Fact]
    public void Target_containers_restart_unless_stopped_and_publish_only_the_target_port() {
        var config = DockerDeploymentExecutor.TargetHostConfig("8080/tcp", 8102);

        // Comes back after a Docker Desktop or host restart; a backup the executor stopped stays stopped.
        Assert.Equal(RestartPolicyKind.UnlessStopped, config.RestartPolicy.Name);
        var binding = Assert.Single(Assert.Single(config.PortBindings).Value);
        Assert.Equal("8102", binding.HostPort);
        Assert.Equal("8080/tcp", Assert.Single(config.PortBindings).Key);
    }
}
