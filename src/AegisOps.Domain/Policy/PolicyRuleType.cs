namespace AegisOps.Domain.Policy;
public enum PolicyRuleType {
    RequireTestsPassed = 1,
    RequireScan = 2,
    MaxFindings = 3,
    RequireApprovals = 4,
    AllowedBranches = 5,
    DeploymentWindow = 6,
    RequireImageDigest = 7,
    RequirePriorEnvironment = 8,
}