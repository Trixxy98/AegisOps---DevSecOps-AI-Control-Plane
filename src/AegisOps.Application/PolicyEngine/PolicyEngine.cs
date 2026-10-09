using System.Text.Json;
using System.Text.RegularExpressions;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;

namespace AegisOps.Application.PolicyEngine;

public sealed record PriorSuccess(
    EnvironmentTier Tier,
    DateTimeOffset CompletedAt
);

public sealed record PolicyContext(
    string Branch,
    CheckStatus BuildStatus,
    CheckStatus TestStatus,
    string? ImageDigest,
    EnvironmentTier Tier,
    Guid ProjectId,
    Guid TeamId,
    PriorSuccess? Prior,
    int SatisfiedApprovals,
    DateTimeOffset Now
);

public sealed record ApplicableRule(
    Guid PolicyId,
    string PolicyName,
    int PolicyVersion,
    Guid RuleId,
    PolicyRuleType Type,
    RuleEffect Effect,
    string Parameters,
    int Order
);

public sealed record ApplicablePolicy(
    Guid Id,
    string Name,
    int Version,
    PolicyScope Scope,
    Guid? ScopeId,
    IReadOnlyList<EnvironmentTier> Tiers,
    bool IsEnabled,
    IReadOnlyList<ApplicableRule> Rules
);

public sealed record RuleResult(
    Guid PolicyId,
    string PolicyName,
    int PolicyVersion,
    Guid RuleId,
    string Type,
    string Effect,
    bool Passed,
    string Message
);

public sealed record EvaluationResult(
    PolicyDecision Decision,
    int ApprovalsRequired,
    string? Message,
    IReadOnlyList<RuleResult> Results
);

public static class PolicyEngine {
    private static readonly JsonSerializerOptions Json = new() {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<ApplicablePolicy> Applicable(
        IEnumerable<ApplicablePolicy> policies,
        PolicyContext context
    ) {
        return policies
            .Where(policy => policy.IsEnabled)
            .Where(policy => policy.Tiers.Contains(context.Tier))
            .Where(policy => policy.Scope switch {
                PolicyScope.Global => true,
                PolicyScope.Team => policy.ScopeId == context.TeamId,
                PolicyScope.Project => policy.ScopeId == context.ProjectId,
                _ => false,
            })
            .OrderBy(policy => policy.Scope)
            .ToArray();
    }

    public static EvaluationResult Evaluate(
        PolicyContext context,
        IEnumerable<ApplicablePolicy> policies
    ) {
        var results = new List<RuleResult>();
        var approvalsRequired = 0;

        foreach (var policy in Applicable(policies, context)) {
            foreach (var rule in policy.Rules.Where(item => item.PolicyId == policy.Id).OrderBy(item => item.Order)) {
                var (passed, message, quorum) = EvaluateRule(rule, context);
                if (quorum > approvalsRequired) {
                    approvalsRequired = quorum;
                }

                results.Add(new RuleResult(
                    policy.Id,
                    policy.Name,
                    policy.Version,
                    rule.RuleId,
                    rule.Type.ToString(),
                    rule.Effect.ToString(),
                    passed,
                    message
                ));
            }
        }

        var denies = results.Where(result => !result.Passed && result.Effect == nameof(RuleEffect.Deny) && result.Type != nameof(PolicyRuleType.RequireApprovals)).ToArray();
        if (denies.Length > 0) {
            return new EvaluationResult(PolicyDecision.Deny, approvalsRequired, denies[0].Message, results);
        }

        var escalations = results.Any(result => !result.Passed && result.Effect == nameof(RuleEffect.RequireApproval) && result.Type != nameof(PolicyRuleType.RequireApprovals));
        var required = Math.Max(approvalsRequired, escalations ? 1 : 0);
        if (required > 0) {
            if (context.SatisfiedApprovals >= required) {
                return new EvaluationResult(PolicyDecision.Allow, required, null, results);
            }

            return new EvaluationResult(
                PolicyDecision.RequireApproval,
                required,
                $"{required} approval(s) required.",
                results
            );
        }

        return new EvaluationResult(PolicyDecision.Allow, 0, null, results);
    }

    private static (bool Passed, string Message, int Quorum) EvaluateRule(ApplicableRule rule, PolicyContext context) {
        try {
            return rule.Type switch {
                PolicyRuleType.RequireTestsPassed => (
                    context.BuildStatus == CheckStatus.Passed && context.TestStatus == CheckStatus.Passed,
                    context.BuildStatus == CheckStatus.Passed && context.TestStatus == CheckStatus.Passed
                        ? "Build and tests passed."
                        : "Build and tests must both be Passed.",
                    0
                ),
                PolicyRuleType.RequireImageDigest => (
                    !string.IsNullOrWhiteSpace(context.ImageDigest),
                    string.IsNullOrWhiteSpace(context.ImageDigest)
                        ? "Image digest is required."
                        : "Image digest is present.",
                    0
                ),
                PolicyRuleType.AllowedBranches => AllowedBranches(rule.Parameters, context.Branch),
                PolicyRuleType.RequirePriorEnvironment => PriorEnvironment(rule.Parameters, context),
                PolicyRuleType.DeploymentWindow => Window(rule.Parameters, context.Now),
                PolicyRuleType.RequireApprovals => Approvals(rule.Parameters),
                PolicyRuleType.RequireScan => (false, "Scan evidence is not available in this phase.", 0),
                PolicyRuleType.MaxFindings => (false, "Finding evidence is not available in this phase.", 0),
                _ => (false, "Rule type is unknown.", 0),
            };
        } catch (JsonException) {
            return (false, "Rule parameters are invalid.", 0);
        }
    }

    private static (bool, string, int) AllowedBranches(string parameters, string branch) {
        var body = JsonSerializer.Deserialize<BranchParameters>(parameters, Json) ?? new BranchParameters(null);
        var patterns = body.Patterns ?? [];
        if (patterns.Length == 0) {
            return (false, "No branch patterns are configured.", 0);
        }

        var matched = patterns.Any(pattern => Regex.IsMatch(
            branch,
            "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
            RegexOptions.CultureInvariant
        ));
        return matched
            ? (true, $"Branch {branch} is allowed.", 0)
            : (false, $"Branch {branch} is not allowed.", 0);
    }

    private static (bool, string, int) PriorEnvironment(string parameters, PolicyContext context) {
        var body = JsonSerializer.Deserialize<PriorParameters>(parameters, Json) ?? new PriorParameters(null, null, null);
        if (!Enum.TryParse<EnvironmentTier>(body.Tier, ignoreCase: true, out var tier)) {
            return (false, "Prior environment tier is invalid.", 0);
        }

        if (context.Prior is null || context.Prior.Tier != tier) {
            return (false, $"Artifact has not succeeded in {tier}.", 0);
        }

        if (body.MaxAgeHours is > 0) {
            var age = context.Now - context.Prior.CompletedAt;
            if (age.TotalHours > body.MaxAgeHours) {
                return (false, $"Prior {tier} success is older than {body.MaxAgeHours} hours.", 0);
            }
        }

        return (true, $"Artifact succeeded in {tier}.", 0);
    }

    private static (bool, string, int) Window(string parameters, DateTimeOffset now) {
        var body = JsonSerializer.Deserialize<WindowParameters>(parameters, Json);
        if (body?.Allowed is null || body.Allowed.Length == 0) {
            return (false, "Deployment window is not configured.", 0);
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(body.Timezone) ? "Asia/Kuala_Lumpur" : body.Timezone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var day = local.DayOfWeek switch {
            DayOfWeek.Monday => "Mon",
            DayOfWeek.Tuesday => "Tue",
            DayOfWeek.Wednesday => "Wed",
            DayOfWeek.Thursday => "Thu",
            DayOfWeek.Friday => "Fri",
            DayOfWeek.Saturday => "Sat",
            _ => "Sun",
        };
        var clock = local.TimeOfDay;
        foreach (var slot in body.Allowed) {
            if (slot.Days is null || !slot.Days.Contains(day, StringComparer.OrdinalIgnoreCase)) {
                continue;
            }

            if (!TimeSpan.TryParse(slot.From, out var from) || !TimeSpan.TryParse(slot.To, out var to)) {
                continue;
            }

            if (clock >= from && clock <= to) {
                return (true, "Inside an allowed deployment window.", 0);
            }
        }

        return (false, $"Outside allowed window ({zone.Id}).", 0);
    }

    private static (bool, string, int) Approvals(string parameters) {
        var body = JsonSerializer.Deserialize<ApprovalParameters>(parameters, Json) ?? new ApprovalParameters(1, null);
        var count = body.Count < 1 ? 1 : body.Count;
        return (true, $"{count} approval(s) required.", count);
    }

    private sealed record BranchParameters(string[]? Patterns);
    private sealed record PriorParameters(string? Tier, string? Status, int? MaxAgeHours);
    private sealed record ApprovalParameters(int Count, string[]? Roles);
    private sealed record WindowParameters(string? Timezone, WindowSlot[]? Allowed);
    private sealed record WindowSlot(string[]? Days, string? From, string? To);
}
