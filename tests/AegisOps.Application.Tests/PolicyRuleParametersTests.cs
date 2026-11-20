using AegisOps.Application.PolicyEngine;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using Engine = AegisOps.Application.PolicyEngine.PolicyEngine;

namespace AegisOps.Application.Tests;

public sealed class PolicyRuleParametersTests {
    [Theory]
    [InlineData(PolicyRuleType.RequireTestsPassed, "{}")]
    [InlineData(PolicyRuleType.RequireTestsPassed, "")]
    [InlineData(PolicyRuleType.RequireImageDigest, "{}")]
    [InlineData(PolicyRuleType.RequireScan, """{"scanners":["Gitleaks","Trivy","Semgrep"],"maxAgeHours":168}""")]
    [InlineData(PolicyRuleType.RequireScan, """{"scanners":["trivy"]}""")]
    [InlineData(PolicyRuleType.RequireScan, """{"Scanners":["Trivy"],"maxAgeHours":null}""")]
    [InlineData(PolicyRuleType.RequireScan, """{"scanners":["Trivy"],"maxAgeHours":8760}""")]
    [InlineData(PolicyRuleType.MaxFindings, """{"severity":"Critical","max":0}""")]
    [InlineData(PolicyRuleType.MaxFindings, """{"scanner":"Trivy","severity":"high","max":3,"includeUnknown":true}""")]
    [InlineData(PolicyRuleType.MaxFindings, """{"scanner":null,"severity":"Info","max":10000,"includeUnknown":false}""")]
    [InlineData(PolicyRuleType.MaxFindings, "{}")]
    [InlineData(PolicyRuleType.RequireApprovals, """{"count":2}""")]
    [InlineData(PolicyRuleType.RequireApprovals, """{"count":1,"roles":["Approver","Security","Admin"]}""")]
    [InlineData(PolicyRuleType.RequireApprovals, """{"count":10,"roles":[]}""")]
    [InlineData(PolicyRuleType.AllowedBranches, """{"patterns":["main","release/*","refs/tags/v*"]}""")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"timezone":"Asia/Kuala_Lumpur","allowed":[{"days":["Mon","Tue","Wed","Thu"],"from":"09:00","to":"18:00"},{"days":["Fri"],"from":"09:00","to":"15:00"}]}""")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"timezone":"UTC","allowed":[{"days":["sat","Sun"],"from":"00:00","to":"23:59:59.9999999"}]}""")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"allowed":[{"days":["Mon"],"from":"8:00","to":"17:30:00"}]}""")]
    [InlineData(PolicyRuleType.RequirePriorEnvironment, """{"tier":"Staging","status":"Succeeded","maxAgeHours":720}""")]
    [InlineData(PolicyRuleType.RequirePriorEnvironment, """{"tier":"development"}""")]
    public void Valid_parameters_pass(PolicyRuleType type, string parameters) {
        Assert.Null(PolicyRuleParameters.Validate(type, parameters));
    }

    [Theory]
    [InlineData(PolicyRuleType.RequireTestsPassed, "[]", "parameters")]
    [InlineData(PolicyRuleType.RequireTestsPassed, "{not json", "parameters")]
    [InlineData(PolicyRuleType.RequireTestsPassed, """{"strict":true}""", "strict")]
    [InlineData(PolicyRuleType.RequireImageDigest, """{"algorithm":"sha256"}""", "algorithm")]
    [InlineData(PolicyRuleType.RequireScan, "{}", "scanners")]
    [InlineData(PolicyRuleType.RequireScan, """{"scanners":[]}""", "scanners")]
    [InlineData(PolicyRuleType.RequireScan, """{"scanners":"Trivy"}""", "scanners")]
    [InlineData(PolicyRuleType.RequireScan, """{"scanners":["Snyk"]}""", "scanners[0]")]
    [InlineData(PolicyRuleType.RequireScan, """{"scanners":["Trivy","trivy"]}""", "scanners[1]")]
    [InlineData(PolicyRuleType.RequireScan, """{"scanners":["Trivy"],"maxAgeHours":0}""", "maxAgeHours")]
    [InlineData(PolicyRuleType.RequireScan, """{"scanners":["Trivy"],"maxAgeHours":8761}""", "maxAgeHours")]
    [InlineData(PolicyRuleType.RequireScan, """{"scanners":["Trivy"],"maxAgeHours":"168"}""", "maxAgeHours")]
    [InlineData(PolicyRuleType.RequireScan, """{"scanners":["Trivy"],"maxAgeDays":7}""", "maxAgeDays")]
    [InlineData(PolicyRuleType.MaxFindings, """{"severity":"Unknown"}""", "severity")]
    [InlineData(PolicyRuleType.MaxFindings, """{"severity":"Severe"}""", "severity")]
    [InlineData(PolicyRuleType.MaxFindings, """{"max":-1}""", "max")]
    [InlineData(PolicyRuleType.MaxFindings, """{"max":1.5}""", "max")]
    [InlineData(PolicyRuleType.MaxFindings, """{"max":10001}""", "max")]
    [InlineData(PolicyRuleType.MaxFindings, """{"scanner":"Snyk"}""", "scanner")]
    [InlineData(PolicyRuleType.MaxFindings, """{"scanner":""}""", "scanner")]
    [InlineData(PolicyRuleType.MaxFindings, """{"includeUnknown":"yes"}""", "includeUnknown")]
    [InlineData(PolicyRuleType.MaxFindings, """{"maxSeverity":"High"}""", "maxSeverity")]
    [InlineData(PolicyRuleType.MaxFindings, """{"max":1,"Max":2}""", "max")]
    [InlineData(PolicyRuleType.RequireApprovals, """{"count":0}""", "count")]
    [InlineData(PolicyRuleType.RequireApprovals, """{"count":11}""", "count")]
    [InlineData(PolicyRuleType.RequireApprovals, """{"count":null}""", "count")]
    [InlineData(PolicyRuleType.RequireApprovals, """{"count":2,"roles":["Developer"]}""", "roles[0]")]
    [InlineData(PolicyRuleType.RequireApprovals, """{"count":2,"roles":["approver"]}""", "roles[0]")]
    [InlineData(PolicyRuleType.RequireApprovals, """{"count":2,"roles":["Admin","Admin"]}""", "roles[1]")]
    [InlineData(PolicyRuleType.RequireApprovals, """{"count":2,"roles":"Admin"}""", "roles")]
    [InlineData(PolicyRuleType.RequireApprovals, """{"count":2,"distinctTeams":true}""", "distinctTeams")]
    [InlineData(PolicyRuleType.AllowedBranches, """{"patterns":["main",""]}""", "patterns[1]")]
    [InlineData(PolicyRuleType.AllowedBranches, """{"patterns":[7]}""", "patterns[0]")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"timezone":"Mars/Olympus_Mons","allowed":[{"days":["Mon"],"from":"09:00","to":"18:00"}]}""", "timezone")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"timezone":"","allowed":[{"days":["Mon"],"from":"09:00","to":"18:00"}]}""", "timezone")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"timezone":"UTC"}""", "allowed")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"timezone":"UTC","allowed":[]}""", "allowed")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"allowed":["Mon 09:00-18:00"]}""", "allowed[0]")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"allowed":[{"days":[],"from":"09:00","to":"18:00"}]}""", "allowed[0].days")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"allowed":[{"days":["Monday"],"from":"09:00","to":"18:00"}]}""", "allowed[0].days[0]")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"allowed":[{"days":["Mon","mon"],"from":"09:00","to":"18:00"}]}""", "allowed[0].days[1]")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"allowed":[{"days":["Mon"],"to":"18:00"}]}""", "allowed[0].from")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"allowed":[{"days":["Mon"],"from":"9","to":"18:00"}]}""", "allowed[0].from")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"allowed":[{"days":["Mon"],"from":"09:00","to":"24:00"}]}""", "allowed[0].to")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"allowed":[{"days":["Mon"],"from":"09:00","to":"1.02:00"}]}""", "allowed[0].to")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"allowed":[{"days":["Mon"],"from":"22:00","to":"02:00"}]}""", "allowed[0].to")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"allowed":[{"days":["Mon"],"from":"09:00","to":"09:00"}]}""", "allowed[0].to")]
    [InlineData(PolicyRuleType.DeploymentWindow, """{"allowed":[{"days":["Mon"],"from":"09:00","to":"18:00"},{"days":["Fri"],"from":"09:00","until":"15:00"}]}""", "allowed[1].until")]
    [InlineData(PolicyRuleType.RequirePriorEnvironment, "{}", "tier")]
    [InlineData(PolicyRuleType.RequirePriorEnvironment, """{"tier":"QA"}""", "tier")]
    [InlineData(PolicyRuleType.RequirePriorEnvironment, """{"tier":"Staging","status":"Failed"}""", "status")]
    [InlineData(PolicyRuleType.RequirePriorEnvironment, """{"tier":"Staging","maxAgeHours":0}""", "maxAgeHours")]
    public void Invalid_parameters_name_the_field(PolicyRuleType type, string parameters, string field) {
        var problem = PolicyRuleParameters.Validate(type, parameters);
        Assert.NotNull(problem);
        Assert.Equal(field, problem.Field);
        Assert.False(string.IsNullOrWhiteSpace(problem.Message));
    }

    [Fact]
    public void Window_slot_limit_and_messages_name_ranges() {
        var slot = """{"days":["Mon"],"from":"09:00","to":"18:00"}""";
        var many = $$"""{"allowed":[{{string.Join(",", Enumerable.Repeat(slot, PolicyRuleParameters.MaxWindowSlots + 1))}}]}""";
        Assert.Equal("allowed", PolicyRuleParameters.Validate(PolicyRuleType.DeploymentWindow, many)?.Field);
        var limit = $$"""{"allowed":[{{string.Join(",", Enumerable.Repeat(slot.Replace("Mon", "Tue", StringComparison.Ordinal), PolicyRuleParameters.MaxWindowSlots))}}]}""";
        Assert.Null(PolicyRuleParameters.Validate(PolicyRuleType.DeploymentWindow, limit));

        Assert.Contains("from 1 to 10", PolicyRuleParameters.Validate(PolicyRuleType.RequireApprovals, """{"count":11}""")!.Message, StringComparison.Ordinal);
        Assert.Contains("from 1 to 8760", PolicyRuleParameters.Validate(PolicyRuleType.RequireScan, """{"scanners":["Trivy"],"maxAgeHours":0}""")!.Message, StringComparison.Ordinal);
        Assert.Contains("allowed: scanners, maxAgeHours", PolicyRuleParameters.Validate(PolicyRuleType.RequireScan, """{"maxAgeDays":7}""")!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Approval_roles_are_the_roles_that_can_approve() {
        Assert.Equal(["Admin", "Approver", "Security"], PolicyRuleParameters.ApproverRoles);
        Assert.Equal(["Gitleaks", "Trivy", "Semgrep"], PolicyRuleParameters.Scanners);
        Assert.Equal(["Development", "Staging", "Production"], PolicyRuleParameters.Tiers);
    }

    [Fact]
    public void Stored_window_with_an_unknown_time_zone_fails_closed_instead_of_throwing() {
        // Validation runs only on save; a rule stored earlier with a bad time zone must still evaluate.
        var policyId = Guid.CreateVersion7();
        var rule = new ApplicableRule(policyId, "Window", 1, Guid.CreateVersion7(), PolicyRuleType.DeploymentWindow, RuleEffect.Deny,
            """{"timezone":"Mars/Olympus_Mons","allowed":[{"days":["Mon"],"from":"00:00","to":"23:59"}]}""", 0);
        var policy = new ApplicablePolicy(policyId, "Window", 1, PolicyScope.Global, null, [EnvironmentTier.Production], true, [rule]);
        var context = new PolicyContext("main", CheckStatus.Passed, CheckStatus.Passed, "sha256:abc", EnvironmentTier.Production,
            Guid.CreateVersion7(), Guid.CreateVersion7(), null, 0, DateTimeOffset.UnixEpoch, []);

        var result = Engine.Evaluate(context, [policy]);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Equal("Deployment window time zone is invalid.", result.Results[0].Message);
    }
}
