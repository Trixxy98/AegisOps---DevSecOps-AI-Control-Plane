namespace AegisOps.Domain.Deploy;

public sealed class Approval {
    public Guid Id {get; private set;}
    public Guid DeploymentId {get; private set;}
    public Guid ApproverId {get; private set;}
    public ApprovalDecision Decision {get; private set;}
    public string? Comment {get; private set;}
    public DateTimeOffset DecidedAt {get; private set;}

    private Approval() {
    }

    public static Approval Create(
        Guid deploymentId,
        Guid approverId,
        ApprovalDecision decision,
        DateTimeOffset decidedAt,
        string? comment
    ) {
        if (deploymentId == Guid.Empty || approverId == Guid.Empty) {
            throw new ArgumentException("Deployment and approver are required.");
        }

        if (!Enum.IsDefined(decision)) {
            throw new ArgumentException("Approval decision is invalid.", nameof(decision));
        }

        return new Approval {
            Id = Guid.CreateVersion7(),
            DeploymentId = deploymentId,
            ApproverId = approverId,
            Decision = decision,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            DecidedAt = decidedAt,
        };
    }
}
