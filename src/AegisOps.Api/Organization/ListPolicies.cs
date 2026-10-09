using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Organization;

public static class ListPolicies {
    public static async Task<IResult> Handle(
        AegisOpsDbContext db,
        HttpContext http,
        string? scope = null,
        Guid? scopeId = null,
        string? tier = null
    ) {
        PolicyScope? parsedScope = null;
        if (!string.IsNullOrWhiteSpace(scope)) {
            if (!Enum.TryParse<PolicyScope>(scope, ignoreCase: true, out var value) || !Enum.IsDefined(value)) {
                return Results.Problem(
                    title: "Scope must be Global, Team, or Project.",
                    statusCode: StatusCodes.Status400BadRequest
                );
            }

            parsedScope = value;
        }

        EnvironmentTier? parsedTier = null;
        if (!string.IsNullOrWhiteSpace(tier)) {
            if (!Enum.TryParse<EnvironmentTier>(tier, ignoreCase: true, out var value) || !Enum.IsDefined(value)) {
                return Results.Problem(
                    title: "Tier must be Development, Staging, or Production.",
                    statusCode: StatusCodes.Status400BadRequest
                );
            }

            parsedTier = value;
        }

        var cancellationToken = http.RequestAborted;
        var query = db.Policies
            .AsNoTracking()
            .Where(policy => policy.ArchivedAt == null);

        if (parsedScope is not null) {
            query = query.Where(policy => policy.Scope == parsedScope);
        }

        if (scopeId is not null) {
            query = query.Where(policy => policy.ScopeId == scopeId);
        }

        var policies = await query
            .OrderBy(policy => policy.Name)
            .ToListAsync(cancellationToken);

        if (parsedTier is not null) {
            policies = policies
                .Where(policy => policy.AppliesToTiers.Contains(parsedTier.Value))
                .ToList();
        }

        var policyIds = policies.Select(policy => policy.Id).ToArray();
        var rules = await db.PolicyRules
            .AsNoTracking()
            .Where(rule => policyIds.Contains(rule.PolicyId))
            .ToListAsync(cancellationToken);

        var response = policies
            .Select(policy => CreatePolicy.ToResponse(
                policy,
                rules.Where(rule => rule.PolicyId == policy.Id)
            ))
            .ToArray();

        return Results.Ok(response);
    }
}