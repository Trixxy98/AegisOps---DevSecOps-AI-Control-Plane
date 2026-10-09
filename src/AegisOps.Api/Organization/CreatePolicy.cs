using System.Security.Claims;
using System.Text.Json;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Organization;

public sealed record CreatePolicyRuleRequest(
    string Type,
    string Effect,
    int Order,
    JsonElement? Parameters
);

public sealed record CreatePolicyRequest(
    string Name,
    string? Description,
    string Scope,
    Guid? ScopeId,
    IReadOnlyList<string> AppliesToTiers,
    IReadOnlyList<CreatePolicyRuleRequest>? Rules
);

public sealed record PolicyRuleResponse(
    Guid Id,
    string Type,
    string Effect,
    int Order,
    bool IsEnabled,
    JsonElement Parameters
);

public sealed record PolicyResponse(
    Guid Id,
    string Name,
    string? Description,
    string Scope,
    Guid? ScopeId,
    IReadOnlyList<string> AppliesToTiers,
    bool IsEnabled,
    int Version,
    IReadOnlyList<PolicyRuleResponse> Rules
);

public static class CreatePolicy {
    public static async Task<IResult> Handle(
        CreatePolicyRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        HttpContext http
    ) {
        if (!Enum.TryParse<PolicyScope>(request.Scope, ignoreCase: true, out var scope) || !Enum.IsDefined(scope)) {
            return Results.Problem(
                title: "Scope must be Global, Team, or Project.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || !Guid.TryParse(subject, out var userId)) {
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsActive) {
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var cancellationToken = http.RequestAborted;
        if (scope == PolicyScope.Team) {
            var teamExists = request.ScopeId is not null && await db.Teams.AnyAsync(
                team => team.Id == request.ScopeId,
                cancellationToken
            );
            if (!teamExists) {
                return Results.Problem(
                    title: "Team was not found.",
                    statusCode: StatusCodes.Status404NotFound
                );
            }
        }

        if (scope == PolicyScope.Project) {
            var projectExists = request.ScopeId is not null && await db.Projects.AnyAsync(
                project => project.Id == request.ScopeId,
                cancellationToken
            );
            if (!projectExists) {
                return Results.Problem(
                    title: "Project was not found.",
                    statusCode: StatusCodes.Status404NotFound
                );
            }
        }

        try {
            var tiers = (request.AppliesToTiers ?? [])
                .Select(tier => {
                    if (!Enum.TryParse<EnvironmentTier>(tier, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed)) {
                        throw new ArgumentException("Each tier must be Development, Staging, or Production.");
                    }

                    return parsed;
                })
                .ToArray();

            var now = time.GetUtcNow();
            var policy = Policy.Create(
                request.Name,
                scope,
                tiers,
                userId,
                now,
                request.ScopeId,
                request.Description
            );

            var rules = new List<PolicyRule>();
            foreach (var item in request.Rules ?? []) {
                if (!Enum.TryParse<PolicyRuleType>(item.Type, ignoreCase: true, out var type) || !Enum.IsDefined(type)) {
                    throw new ArgumentException("Rule type is invalid.");
                }

                if (!Enum.TryParse<RuleEffect>(item.Effect, ignoreCase: true, out var effect) || !Enum.IsDefined(effect)) {
                    throw new ArgumentException("Rule effect must be Deny, RequireApproval, or Warn.");
                }

                var parameters = item.Parameters is null || item.Parameters.Value.ValueKind == JsonValueKind.Undefined
                    ? null
                    : item.Parameters.Value.GetRawText();

                rules.Add(PolicyRule.Create(policy.Id, type, effect, item.Order, parameters));
            }

            if (rules.Select(rule => rule.Order).Distinct().Count() != rules.Count) {
                return Results.Problem(
                    title: "Rule order values must be unique.",
                    statusCode: StatusCodes.Status400BadRequest
                );
            }

            db.Policies.Add(policy);
            db.PolicyRules.AddRange(rules);
            AuditLog.Write(db, now, user.Id, user.Email ?? user.DisplayName, "policy.created", "Policy", policy.Id);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/v1/policies/{policy.Id}", ToResponse(policy, rules));
        } catch (ArgumentException exception) {
            return Results.Problem(
                title: exception.Message,
                statusCode: StatusCodes.Status400BadRequest
            );
        }
    }

    internal static PolicyResponse ToResponse(Policy policy, IEnumerable<PolicyRule> rules) {
        return new PolicyResponse(
            policy.Id,
            policy.Name,
            policy.Description,
            policy.Scope.ToString(),
            policy.ScopeId,
            policy.AppliesToTiers.Select(tier => tier.ToString()).ToArray(),
            policy.IsEnabled,
            policy.Version,
            rules
                .OrderBy(rule => rule.Order)
                .Select(rule => new PolicyRuleResponse(
                    rule.Id,
                    rule.Type.ToString(),
                    rule.Effect.ToString(),
                    rule.Order,
                    rule.IsEnabled,
                    JsonDocument.Parse(rule.Parameters).RootElement.Clone()
                ))
                .ToArray()
        );
    }
}