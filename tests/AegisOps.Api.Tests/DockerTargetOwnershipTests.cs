using AegisOps.Infrastructure.Docker;
using Docker.DotNet.Models;

namespace AegisOps.Api.Tests;

public class DockerTargetOwnershipTests {
    [Fact]
    public void ExecutorOnlyReplacesContainersLabeledForSameTarget() {
        var container = new ContainerListResponse {
            Labels = new Dictionary<string, string> {
                ["aegisops.project"] = "site-service",
                ["aegisops.environment"] = "Staging",
                ["aegisops.deployment"] = Guid.NewGuid().ToString(),
            },
        };

        Assert.True(DockerDeploymentExecutor.OwnedBy(container, "site-service", "Staging"));
        Assert.False(DockerDeploymentExecutor.OwnedBy(container, "site-service", "Production"));
        Assert.False(DockerDeploymentExecutor.OwnedBy(container, "circuit-service", "Staging"));
        container.Labels.Remove("aegisops.deployment");
        Assert.False(DockerDeploymentExecutor.OwnedBy(container, "site-service", "Staging"));
        container.Labels.Clear();
        Assert.False(DockerDeploymentExecutor.OwnedBy(container, "site-service", "Staging"));
    }
}
