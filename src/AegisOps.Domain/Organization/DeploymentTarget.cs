namespace AegisOps.Domain.Organization;

public sealed class DeploymentTarget {
    public string Type {get; private set;} = "noop";
    public bool AllowApiKeyProduction {get; private set;}

    private DeploymentTarget() {

    }

    public static DeploymentTarget Noop(bool allowApiKeyProduction = false) {
        return new DeploymentTarget {
            Type = "noop",
            AllowApiKeyProduction = allowApiKeyProduction,
        };
    }
}