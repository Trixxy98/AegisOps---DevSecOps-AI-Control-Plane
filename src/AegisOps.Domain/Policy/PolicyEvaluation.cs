namespace AegisOps.Domain.Policy;

public sealed class PolicyEvaluation {
    public Guid Id {get; private set;}
    public Guid? DeploymentId {get; private set;}
    public PolicyDecision Decision {get; private set;}
    public int ApprovalsRequired {get; private set;}
    public DateTimeOffset EvaluatedAt {get; private set;}
    public string RuleResults {get; private set;}

    private PolicyEvaluation() {
        RuleResults = "[]";
    }

    public static PolicyEvaluation Create(
        Guid? deploymentId,
        PolicyDecision decision,
        int approvalsRequired,
        DateTimeOffset evaluatedAt,
        string ruleResults
    ) {
        if (!Enum.IsDefined(decision)) {
            throw new ArgumentException("Decision is invalid.", nameof(decision));
        }

        return new PolicyEvaluation {
            Id = Guid.CreateVersion7(),
            DeploymentId = deploymentId,
            Decision = decision,
            ApprovalsRequired = approvalsRequired,
            EvaluatedAt = evaluatedAt,
            RuleResults = string.IsNullOrWhiteSpace(ruleResults) ? "[]" : ruleResults,
        };
    }
}
