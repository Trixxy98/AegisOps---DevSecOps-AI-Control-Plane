namespace AegisOps.Domain.Deploy;

public enum DeploymentStatus {
    Requested = 1,
    Evaluating = 2,
    Denied = 3,
    AwaitingApproval = 4,
    Rejected = 5,
    Approved = 6,
    Deploying = 7,
    Succeeded = 8,
    Failed = 9,
    Cancelled = 10,
}
