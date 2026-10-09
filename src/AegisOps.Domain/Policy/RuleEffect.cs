namespace AegisOps.Domain.Policy;

public enum RuleEffect {
    Deny = 1,
    RequireApproval = 2,
    Warn = 3,
}