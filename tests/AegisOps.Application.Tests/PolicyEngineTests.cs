using AegisOps.Application.PolicyEngine;
using Engine = AegisOps.Application.PolicyEngine.PolicyEngine;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;

namespace AegisOps.Application.Tests;

public sealed class PolicyEngineTests {
    private static readonly Guid PolicyId = Guid.CreateVersion7();

    [Fact]
    public void Failed_tests_deny() {
        var result = Engine.Evaluate(Context(test: CheckStatus.Failed), [Policy(Rule(PolicyRuleType.RequireTestsPassed, RuleEffect.Deny, "{}"))]);
        Assert.Equal(PolicyDecision.Deny, result.Decision);
    }

    [Fact]
    public void Missing_digest_denies() {
        var result = Engine.Evaluate(Context(digest: null), [Policy(Rule(PolicyRuleType.RequireImageDigest, RuleEffect.Deny, "{}"))]);
        Assert.Equal(PolicyDecision.Deny, result.Decision);
    }

    [Fact]
    public void Disallowed_branch_denies() {
        var result = Engine.Evaluate(
            Context(branch: "feature"),
            [Policy(Rule(PolicyRuleType.AllowedBranches, RuleEffect.Deny, """{"patterns":["main"]}"""))]
        );
        Assert.Equal(PolicyDecision.Deny, result.Decision);
    }

    [Fact]
    public void Two_approvals_are_required_until_quorum() {
        var waiting = Engine.Evaluate(Context(approvals: 0), [Policy(Rule(PolicyRuleType.RequireApprovals, RuleEffect.RequireApproval, """{"count":2}"""))]);
        var allowed = Engine.Evaluate(Context(approvals: 2), [Policy(Rule(PolicyRuleType.RequireApprovals, RuleEffect.RequireApproval, """{"count":2}"""))]);
        Assert.Equal(PolicyDecision.RequireApproval, waiting.Decision);
        Assert.Equal(2, waiting.ApprovalsRequired);
        Assert.Equal(PolicyDecision.Allow, allowed.Decision);
    }

    [Fact]
    public void Friday_evening_window_denies() {
        var fridayEvening = new DateTimeOffset(2026, 10, 9, 9, 30, 0, TimeSpan.Zero);
        var result = Engine.Evaluate(
            Context(now: fridayEvening),
            [Policy(Rule(PolicyRuleType.DeploymentWindow, RuleEffect.Deny, """{"timezone":"Asia/Kuala_Lumpur","allowed":[{"days":["Mon","Tue","Wed","Thu"],"from":"09:00","to":"18:00"},{"days":["Fri"],"from":"09:00","to":"15:00"}]}"""))]
        );
        Assert.Equal(PolicyDecision.Deny, result.Decision);
    }

    private static PolicyContext Context(
        string branch = "main",
        CheckStatus test = CheckStatus.Passed,
        string? digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        int approvals = 0,
        DateTimeOffset? now = null
    ) {
        return new PolicyContext(branch, CheckStatus.Passed, test, digest, EnvironmentTier.Production, Guid.CreateVersion7(), Guid.CreateVersion7(), null, approvals, now ?? new DateTimeOffset(2026, 10, 6, 6, 0, 0, TimeSpan.Zero));
    }

    private static ApplicablePolicy Policy(ApplicableRule rule) {
        return new ApplicablePolicy(PolicyId, "Production", 1, PolicyScope.Global, null, [EnvironmentTier.Production], true, [rule]);
    }

    private static ApplicableRule Rule(PolicyRuleType type, RuleEffect effect, string parameters) {
        return new ApplicableRule(PolicyId, "Production", 1, Guid.CreateVersion7(), type, effect, parameters, 0);
    }
}
