namespace AegisOps.Domain.Deploy;

public sealed class Deployment {
    public Guid Id {get; private set;}
    public Guid ProjectId {get; private set;}
    public Guid EnvironmentId {get; private set;}
    public Guid ArtifactId {get; private set;}
    public DeploymentStatus Status {get; private set;}
    public Guid? RequestedById {get; private set;}
    public Guid? RequestedByApiKeyId {get; private set;}
    public DateTimeOffset RequestedAt {get; private set;}
    public string? Reason {get; private set;}
    public Guid? PolicyEvaluationId {get; private set;}
    public int ApprovalsRequired {get; private set;}
    public int ApprovalsReceived {get; private set;}
    public DateTimeOffset? StartedAt {get; private set;}
    public DateTimeOffset? CompletedAt {get; private set;}
    public string? FailureReason {get; private set;}
    public string CorrelationId {get; private set;}

    private Deployment() {
        CorrelationId = string.Empty;
    }

    public static Deployment Request(
        Guid projectId,
        Guid environmentId,
        Guid artifactId,
        DateTimeOffset requestedAt,
        string correlationId,
        Guid? requestedById,
        Guid? requestedByApiKeyId,
        string? reason
    ) {
        if (projectId == Guid.Empty || environmentId == Guid.Empty || artifactId == Guid.Empty) {
            throw new ArgumentException("Project, environment, and artifact are required.");
        }

        if (requestedById is null && requestedByApiKeyId is null) {
            throw new ArgumentException("A user or an API key must request the deployment.");
        }

        if (string.IsNullOrWhiteSpace(correlationId)) {
            throw new ArgumentException("Correlation ID is required.", nameof(correlationId));
        }

        return new Deployment {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            EnvironmentId = environmentId,
            ArtifactId = artifactId,
            Status = DeploymentStatus.Requested,
            RequestedById = requestedById,
            RequestedByApiKeyId = requestedByApiKeyId,
            RequestedAt = requestedAt,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            CorrelationId = correlationId.Trim(),
        };
    }

    public void BeginEvaluation() => Move(DeploymentStatus.Evaluating, DeploymentStatus.Requested);

    public void ApplyDecision(PolicyOutcome outcome, DateTimeOffset at) {
        if (Status is not DeploymentStatus.Evaluating and not DeploymentStatus.Approved and not DeploymentStatus.AwaitingApproval) {
            throw new InvalidOperationException($"Cannot apply a decision from {Status}.");
        }

        PolicyEvaluationId = outcome.EvaluationId;
        ApprovalsRequired = outcome.ApprovalsRequired;
        switch (outcome.Decision) {
            case Policy.PolicyDecision.Deny:
                Status = DeploymentStatus.Denied;
                FailureReason = outcome.Message;
                CompletedAt = at;
                break;
            case Policy.PolicyDecision.RequireApproval:
                Status = DeploymentStatus.AwaitingApproval;
                break;
            case Policy.PolicyDecision.Allow:
                Status = DeploymentStatus.Approved;
                break;
            default:
                throw new InvalidOperationException("Policy decision is invalid.");
        }
    }

    public void ReceiveApproval() {
        if (Status != DeploymentStatus.AwaitingApproval) {
            throw new InvalidOperationException("Deployment is not awaiting approval.");
        }

        ApprovalsReceived++;
    }

    public void MarkApproved() => Move(DeploymentStatus.Approved, DeploymentStatus.AwaitingApproval);

    public void Reject(string? comment, DateTimeOffset at) {
        Move(DeploymentStatus.Rejected, DeploymentStatus.AwaitingApproval);
        FailureReason = string.IsNullOrWhiteSpace(comment) ? "Rejected." : comment.Trim();
        CompletedAt = at;
    }

    public void Cancel(DateTimeOffset at) {
        if (Status is not DeploymentStatus.Requested and not DeploymentStatus.AwaitingApproval) {
            throw new InvalidOperationException("Only a requested or awaiting deployment can be cancelled.");
        }

        Status = DeploymentStatus.Cancelled;
        CompletedAt = at;
    }

    public void Start(DateTimeOffset at) {
        Move(DeploymentStatus.Deploying, DeploymentStatus.Approved);
        StartedAt = at;
    }

    public void Succeed(DateTimeOffset at) {
        Move(DeploymentStatus.Succeeded, DeploymentStatus.Deploying);
        CompletedAt = at;
        FailureReason = null;
    }

    public void Fail(string reason, DateTimeOffset at) {
        if (Status != DeploymentStatus.Deploying) {
            throw new InvalidOperationException("Only a deploying release can fail.");
        }

        Status = DeploymentStatus.Failed;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? "Deployment failed." : reason.Trim();
        CompletedAt = at;
    }

    public bool IsTerminal => Status is DeploymentStatus.Denied
        or DeploymentStatus.Rejected
        or DeploymentStatus.Cancelled
        or DeploymentStatus.Succeeded
        or DeploymentStatus.Failed;

    private void Move(DeploymentStatus next, DeploymentStatus expected) {
        if (Status != expected) {
            throw new InvalidOperationException($"Cannot move from {Status} to {next}.");
        }

        Status = next;
    }
}

public sealed record PolicyOutcome(
    AegisOps.Domain.Policy.PolicyDecision Decision,
    Guid EvaluationId,
    int ApprovalsRequired,
    string? Message
);
