namespace AegisOps.Domain.Organization;

public sealed class Environment {
    public Guid Id {get; private set;}
    public Guid ProjectId {get; private set;}
    public string Name {get; private set;}
    public EnvironmentTier Tier {get; private set;}
    public int Order {get; private set;}
    public DeploymentTarget Target {get; private set;} = DeploymentTarget.Noop();
    public Guid? CurrentArtifactId {get; private set;}
    public Guid? LastDeploymentId {get; private set;}

    private Environment() {
        Name = string.Empty;
    }

    public static Environment Create(
        Guid projectId,
        string name,
        EnvironmentTier tier,
        int order,
        DeploymentTarget? target = null
    ) {
        if (projectId == Guid.Empty) {
            throw new ArgumentException("Project ID is required.", nameof(projectId));
        }

        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (!Enum.IsDefined(tier)) {
            throw new ArgumentException("Environment tier is invalid.", nameof(tier));
        }

        if (order < 0) {
            throw new ArgumentException("Order must not be negative.", nameof(order));
        }

        return new Environment {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Name = name.Trim(),
            Tier = tier,
            Order = order,
            Target = target ?? DeploymentTarget.Noop(),
        };
    }

    public void Rename(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        Name = name.Trim();
    }

    public void Reorder(int order) {
        if (order < 0) {
            throw new ArgumentException("Order must not be negative.", nameof(order));
        }

        Order = order;
    }

    public void Retarget(DeploymentTarget target) {
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public void RecordDeployment(Guid artifactId, Guid deploymentId) {
        if (artifactId == Guid.Empty) {
            throw new ArgumentException("Artifact ID is required.", nameof(artifactId));
        }

        if (deploymentId == Guid.Empty) {
            throw new ArgumentException("Deployment ID is required.", nameof(deploymentId));
        }

        CurrentArtifactId = artifactId;
        LastDeploymentId = deploymentId;
    }
}