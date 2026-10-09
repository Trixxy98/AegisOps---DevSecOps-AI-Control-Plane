using System.Security.Claims;
using System.Text.Json;
using AegisOps.Application.PolicyEngine;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Organization;

public static class PolicyPhase1Endpoints {
    public static IResult RuleTypes() {
        return Results.Content("""
            [
              {"type":"RequireTestsPassed","effect":"Deny","parameters":{}},
              {"type":"RequireApprovals","effect":"RequireApproval","parameters":{"count":1,"roles":["Approver","Security","Admin"]}},
              {"type":"AllowedBranches","effect":"Deny","parameters":{"patterns":["main"]}},
              {"type":"RequireImageDigest","effect":"Deny","parameters":{}},
              {"type":"RequirePriorEnvironment","effect":"Deny","parameters":{"tier":"Staging","status":"Succeeded","maxAgeHours":720}},
              {"type":"DeploymentWindow","effect":"Deny","parameters":{"timezone":"Asia/Kuala_Lumpur","allowed":[{"days":["Mon","Tue","Wed","Thu"],"from":"09:00","to":"18:00"},{"days":["Fri"],"from":"09:00","to":"15:00"}]}},
              {"type":"RequireScan","effect":"Deny","parameters":{"scanners":["Gitleaks","Trivy","Semgrep"],"maxAgeHours":168}},
              {"type":"MaxFindings","effect":"Deny","parameters":{"severity":"Critical","max":0}}
            ]
            """, "application/json");
    }

    public static async Task<IResult> Replace(
        Guid id,
        CreatePolicyRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        HttpContext http
    ) {
        var subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || !Guid.TryParse(subject, out var userId)) {
            return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsActive) {
            return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var cancellationToken = http.RequestAborted;
        var policy = await db.Policies.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (policy is null) {
            return Results.Problem(title: "Policy was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        try {
            var now = time.GetUtcNow();
            policy.Rename(request.Name, userId, now);
            policy.Describe(request.Description, userId, now);
            var tiers = (request.AppliesToTiers ?? []).Select(tier => {
                if (!Enum.TryParse<EnvironmentTier>(tier, true, out var parsed) || !Enum.IsDefined(parsed)) {
                    throw new ArgumentException("Each tier must be Development, Staging, or Production.");
                }

                return parsed;
            }).ToArray();
            policy.SetTiers(tiers, userId, now);
            var existing = await db.PolicyRules.Where(rule => rule.PolicyId == policy.Id).ToListAsync(cancellationToken);
            db.PolicyRules.RemoveRange(existing);
            var rules = new List<PolicyRule>();
            foreach (var item in request.Rules ?? []) {
                if (!Enum.TryParse<PolicyRuleType>(item.Type, true, out var type) || !Enum.IsDefined(type)) {
                    throw new ArgumentException("Rule type is invalid.");
                }

                if (!Enum.TryParse<RuleEffect>(item.Effect, true, out var effect) || !Enum.IsDefined(effect)) {
                    throw new ArgumentException("Rule effect must be Deny, RequireApproval, or Warn.");
                }

                var parameters = item.Parameters is null || item.Parameters.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? null
                    : item.Parameters.Value.GetRawText();
                rules.Add(PolicyRule.Create(policy.Id, type, effect, item.Order, parameters));
            }

            db.PolicyRules.AddRange(rules);
            db.AuditEvents.Add(AuditEvent.Record(now, "User", userId, user.Email ?? user.DisplayName, "policy.updated", "Policy", policy.Id, "Succeeded", null));
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(CreatePolicy.ToResponse(policy, rules));
        } catch (ArgumentException exception) {
            return Results.Problem(title: exception.Message, statusCode: StatusCodes.Status400BadRequest);
        } catch (InvalidOperationException exception) {
            return Results.Problem(title: exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    public static async Task<IResult> Simulate(
        SimulatePolicyRequest request,
        AegisOpsDbContext db,
        TimeProvider time,
        HttpContext http
    ) {
        var cancellationToken = http.RequestAborted;
        var artifact = await db.Artifacts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.ArtifactId, cancellationToken);
        var environment = await db.Environments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.EnvironmentId, cancellationToken);
        if (artifact is null || environment is null || artifact.ProjectId != environment.ProjectId) {
            return Results.Problem(title: "Artifact or environment was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var project = await db.Projects.AsNoTracking().SingleAsync(item => item.Id == artifact.ProjectId, cancellationToken);
        var policies = await db.Policies.AsNoTracking().Where(item => item.ArchivedAt == null && item.IsEnabled).ToListAsync(cancellationToken);
        var rules = await db.PolicyRules.AsNoTracking().Where(item => item.IsEnabled).ToListAsync(cancellationToken);
        var applicable = policies.Select(policy => new ApplicablePolicy(
            policy.Id,
            policy.Name,
            policy.Version,
            policy.Scope,
            policy.ScopeId,
            policy.AppliesToTiers,
            policy.IsEnabled,
            rules.Where(rule => rule.PolicyId == policy.Id).Select(rule => new ApplicableRule(policy.Id, policy.Name, policy.Version, rule.Id, rule.Type, rule.Effect, rule.Parameters, rule.Order)).ToArray()
        ));
        var result = PolicyEngine.Evaluate(new PolicyContext(
            artifact.Branch,
            artifact.BuildStatus,
            artifact.TestStatus,
            artifact.ImageDigest,
            environment.Tier,
            project.Id,
            project.TeamId,
            null,
            0,
            time.GetUtcNow()
        ), applicable);
        return Results.Ok(new { decision = result.Decision.ToString(), result.ApprovalsRequired, result.Message, result.Results });
    }

    public static async Task<IResult> Audit(AegisOpsDbContext db, HttpContext http) {
        var events = await db.AuditEvents.AsNoTracking()
            .OrderByDescending(item => item.Timestamp)
            .Take(200)
            .Select(item => new {
                item.Id,
                item.Timestamp,
                item.ActorType,
                item.ActorDisplay,
                item.Action,
                item.ResourceType,
                item.ResourceId,
                item.Outcome,
            })
            .ToListAsync(http.RequestAborted);
        return Results.Ok(events);
    }
}

public sealed record SimulatePolicyRequest(Guid ArtifactId, Guid EnvironmentId);
