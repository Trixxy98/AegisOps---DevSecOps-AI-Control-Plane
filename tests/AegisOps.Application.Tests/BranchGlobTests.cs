using System.Diagnostics;
using AegisOps.Application.PolicyEngine;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using Engine = AegisOps.Application.PolicyEngine.PolicyEngine;

namespace AegisOps.Application.Tests;

public sealed class BranchGlobTests {
    [Theory]
    [InlineData("main", "main", true)]
    [InlineData("main", "mainline", false)]
    [InlineData("release/*", "release/1.2", true)]
    [InlineData("release/*", "release/", true)]
    [InlineData("release/*", "hotfix/1.2", false)]
    [InlineData("*-urgent", "hotfix/db-urgent", true)]
    [InlineData("feature/a.b", "feature/aXb", false)] // '.' is literal, not a regex wildcard
    [InlineData("(a+)+$", "(a+)+$", true)] // regex syntax in a glob is literal text
    [InlineData("(a+)+$", "aaaa", false)]
    [InlineData("Main", "main", false)] // branch names are case-sensitive
    public void Globs_match_with_star_as_the_only_wildcard(string glob, string branch, bool expected) {
        Assert.Equal(expected, BranchGlob.IsMatch(branch, glob));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("release/*")]
    [InlineData("*")]
    [InlineData("feature/[x]?{1}\\d")]
    public void Valid_globs_pass_validation(string glob) {
        Assert.Null(BranchGlob.Validate(glob));
        BranchGlob.ValidateRuleParameters(PolicyRuleType.AllowedBranches, $$"""{"patterns":[{{System.Text.Json.JsonSerializer.Serialize(glob)}}]}""");
    }

    [Theory]
    [InlineData("""{"patterns":[]}""")]
    [InlineData("""{"patterns":null}""")]
    [InlineData("""{}""")]
    [InlineData("""{"patterns":"main"}""")]
    [InlineData("""{"patterns":[1]}""")]
    [InlineData("""{"patterns":[""]}""")]
    [InlineData("""{"patterns":["\t"]}""")]
    [InlineData("""{"patterns":["ma\u0007in"]}""")]
    public void Invalid_branch_rules_are_rejected(string parameters) {
        Assert.Throws<ArgumentException>(() => BranchGlob.ValidateRuleParameters(PolicyRuleType.AllowedBranches, parameters));
    }

    [Fact]
    public void Too_many_or_too_long_globs_are_rejected_and_other_rule_types_are_not_checked() {
        Assert.NotNull(BranchGlob.Validate(new string('a', BranchGlob.MaxPatternLength + 1)));
        Assert.Null(BranchGlob.Validate(new string('a', BranchGlob.MaxPatternLength)));
        var many = string.Join(",", Enumerable.Repeat("\"main\"", BranchGlob.MaxPatterns + 1));
        Assert.Throws<ArgumentException>(() => BranchGlob.ValidateRuleParameters(PolicyRuleType.AllowedBranches, $$"""{"patterns":[{{many}}]}"""));
        BranchGlob.ValidateRuleParameters(PolicyRuleType.RequireTestsPassed, "{}");
        BranchGlob.ValidateRuleParameters(PolicyRuleType.RequireApprovals, """{"count":2}""");
    }

    [Fact]
    public void Pathological_glob_and_branch_complete_quickly() {
        // With a backtracking engine ^.*a.*a.*a...b$ against a long run of 'a' without 'b' is polynomial-to-exponential.
        var glob = string.Concat(Enumerable.Repeat("*a", 40)) + "*b";
        var branch = new string('a', 20_000);
        var watch = Stopwatch.StartNew();
        Assert.Null(BranchGlob.Validate(glob));
        Assert.False(BranchGlob.IsMatch(branch, glob));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Matching took {watch.Elapsed}.");

        var result = Engine.Evaluate(Context(branch), [Policy(glob)]);
        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("is not allowed", result.Results[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stored_rules_with_empty_or_odd_patterns_still_evaluate_and_fail_closed() {
        // Rules saved before save-time validation existed must still load and evaluate.
        var empty = Engine.Evaluate(Context("main"), [Policy("""{"patterns":[]}""", raw: true)]);
        Assert.Equal(PolicyDecision.Deny, empty.Decision);
        var blank = Engine.Evaluate(Context(""), [Policy("""{"patterns":[""]}""", raw: true)]);
        Assert.Equal(PolicyDecision.Deny, blank.Decision);
        var allowed = Engine.Evaluate(Context("release/2"), [Policy("""{"patterns":["","release/*"]}""", raw: true)]);
        Assert.Equal(PolicyDecision.Allow, allowed.Decision);
    }

    private static readonly Guid PolicyId = Guid.CreateVersion7();

    private static PolicyContext Context(string branch) => new(branch, CheckStatus.Passed, CheckStatus.Passed, "sha256:abc",
        EnvironmentTier.Production, Guid.CreateVersion7(), Guid.CreateVersion7(), null, 0,
        new DateTimeOffset(2026, 10, 6, 6, 0, 0, TimeSpan.Zero), []);

    private static ApplicablePolicy Policy(string globOrParameters, bool raw = false) {
        var parameters = raw ? globOrParameters : $$"""{"patterns":[{{System.Text.Json.JsonSerializer.Serialize(globOrParameters)}}]}""";
        var rule = new ApplicableRule(PolicyId, "Branches", 1, Guid.CreateVersion7(), PolicyRuleType.AllowedBranches, RuleEffect.Deny, parameters, 0);
        return new ApplicablePolicy(PolicyId, "Branches", 1, PolicyScope.Global, null, [EnvironmentTier.Production], true, [rule]);
    }
}
