using System.Globalization;
using AegisOps.Application.PolicyEngine;
using AegisOps.Domain.Policy;

namespace AegisOps.Api.Organization;

/// <summary>
/// Save-time rule parameter validation shared by policy create and replace (docs/05 §4). The 400 Problem Details
/// title names the rule and the field; the <c>ruleType</c>, <c>ruleOrder</c> and <c>field</c> extensions let a client
/// point at the input that needs fixing.
/// </summary>
public static class PolicyRuleProblems {
    public static IResult? Validate(PolicyRuleType type, int order, string? parameters) {
        var problem = PolicyRuleParameters.Validate(type, parameters);
        if (problem is null) {
            return null;
        }

        return Results.Problem(
            title: string.Create(CultureInfo.InvariantCulture, $"{type} rule {order + 1}: {problem.Message}"),
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> {
                ["ruleType"] = type.ToString(),
                ["ruleOrder"] = order,
                ["field"] = problem.Field,
            });
    }
}
