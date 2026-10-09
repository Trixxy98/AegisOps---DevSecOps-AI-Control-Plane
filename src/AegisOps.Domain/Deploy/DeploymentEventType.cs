namespace AegisOps.Domain.Deploy;

public enum DeploymentEventType {
    Requested = 1,
    PolicyEvaluated = 2,
    ApprovalRequested = 3,
    ApprovalReceived = 4,
    Approved = 5,
    Rejected = 6,
    Denied = 7,
    DeployStarted = 8,
    Succeeded = 9,
    Failed = 10,
    Cancelled = 11,
}
