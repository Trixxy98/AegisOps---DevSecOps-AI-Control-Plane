using System.Security.Claims;
using AegisOps.Domain.Ai;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Ai;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Ai;

public sealed record AiChatRequest(string? Model, Guid? TeamId, LlmMessage[]? Messages);
public sealed record AiPolicyRequest(Guid? Id, string Name, PolicyScope Scope, Guid? ScopeId, Guid[] AllowedModelIds,
    int RequestsPerMinute, int RequestsPerDay, int MaxPromptChars, SensitiveDataAction SensitiveDataAction,
    string[] BlockedPatterns, bool RedactPii, bool StoreContent, bool IsEnabled);

public static class AiEndpoints {
    public static async Task<IResult> Chat(
        AiChatRequest request, ClaimsPrincipal principal, UserManager<User> users, AegisOpsDbContext db,
        AiGateway gateway, HttpContext http
    ) {
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out var userId))
            return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        var user = await users.FindByIdAsync(subject!);
        if (user is null || !user.IsActive)
            return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);

        var teamId = request.TeamId;
        if (teamId.HasValue) {
            if (!await db.TeamMembers.AsNoTracking().AnyAsync(member => member.UserId == userId && member.TeamId == teamId, http.RequestAborted))
                return Results.Problem(title: "Team was not found.", statusCode: StatusCodes.Status404NotFound);
        } else {
            teamId = await db.TeamMembers.AsNoTracking().Where(member => member.UserId == userId)
                .Select(member => (Guid?)member.TeamId).FirstOrDefaultAsync(http.RequestAborted);
        }

        var result = await gateway.Chat(userId, teamId, request.Model, request.Messages,
            http.TraceIdentifier, http.RequestAborted);
        if (result.StatusCode != StatusCodes.Status200OK) {
            var reason = result.Request.BlockReason ?? "AiRequestFailed";
            return Results.Problem(title: reason, statusCode: result.StatusCode,
                extensions: new Dictionary<string, object?> { ["reason"] = reason, ["requestId"] = result.Request.Id });
        }

        return Results.Ok(new {
            id = result.Request.Id,
            model = request.Model,
            message = new { role = "assistant", content = result.Content },
            usage = new { promptTokens = result.Request.PromptTokens, completionTokens = result.Request.CompletionTokens, latencyMs = result.Request.LatencyMs },
            governance = new {
                policy = result.Request.AiPolicyId,
                sensitiveData = new { action = result.Request.RedactionsApplied > 0 ? "Redact" : "Allow", detected = System.Text.Json.JsonSerializer.Deserialize<string[]>(result.Request.DetectedSensitiveTypesJson), redactions = result.Request.RedactionsApplied },
                rateLimit = result.RateLimit is null ? null : new { remainingMinute = result.RateLimit.RemainingMinute, remainingDay = result.RateLimit.RemainingDay },
            },
        });
    }

    public static async Task<IResult> Models(
        ClaimsPrincipal principal, UserManager<User> users, AegisOpsDbContext db, HttpContext http, Guid? teamId = null
    ) {
        var access = await ResolveUserAndTeam(principal, users, db, teamId, http.RequestAborted);
        if (access.Error is not null) return access.Error;
        var policy = await EffectivePolicy(db, access.TeamId, http.RequestAborted);
        if (policy is null) return Results.Ok(Array.Empty<object>());
        var allowedIds = policy.AllowedModelIds;
        var models = await db.AiModels.AsNoTracking().Where(model => model.IsEnabled && allowedIds.Contains(model.Id))
            .OrderBy(model => model.Name).Select(model => new { model.Id, model.Name, model.DisplayName, provider = model.Provider.ToString() })
            .ToListAsync(http.RequestAborted);
        return Results.Ok(models);
    }

    public static async Task<IResult> Requests(
        ClaimsPrincipal principal, UserManager<User> users, AegisOpsDbContext db, HttpContext http, int page = 1, int pageSize = 50
    ) {
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out var userId)) return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        var user = await users.FindByIdAsync(subject!);
        if (user is null || !user.IsActive) return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        if (page < 1 || pageSize is < 1 or > 100) return Results.Problem(title: "Invalid pagination.", statusCode: StatusCodes.Status400BadRequest);
        var roles = await users.GetRolesAsync(user);
        var canReadAll = roles.Contains("Admin", StringComparer.Ordinal) || roles.Contains("Security", StringComparer.Ordinal);
        var query = db.AiRequests.AsNoTracking().AsQueryable();
        if (!canReadAll) query = query.Where(request => request.UserId == userId);
        var total = await query.CountAsync(http.RequestAborted);
        var items = await query.OrderByDescending(request => request.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(request => new {
                request.Id, request.TeamId, request.AiModelId, request.AiPolicyId, status = request.Status.ToString(), purpose = request.Purpose.ToString(),
                request.PromptChars, request.ResponseChars, request.PromptTokens, request.CompletionTokens, request.LatencyMs,
                request.DetectedSensitiveTypesJson, request.RedactionsApplied, request.BlockReason, request.PromptHash,
                request.PromptPreview, request.ResponsePreview, request.CorrelationId, request.CreatedAt,
            }).ToListAsync(http.RequestAborted);
        return Results.Ok(new { page, pageSize, total, items });
    }

    public static async Task<IResult> SyncModels(ILlmClient llm, AegisOpsDbContext db, HttpContext http) {
        IReadOnlyList<string> names;
        try { names = await llm.ListModels(http.RequestAborted); }
        catch (Exception exception) when (exception is not OperationCanceledException) {
            return Results.Problem(title: "Model provider is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        var existing = await db.AiModels.ToDictionaryAsync(model => model.Name, http.RequestAborted);
        var added = 0;
        foreach (var name in names.Distinct(StringComparer.Ordinal)) {
            if (existing.ContainsKey(name)) continue;
            var model = AiModel.Create(name);
            model.SetEnabled(true);
            db.AiModels.Add(model);
            added++;
        }
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { discovered = names.Count, added });
    }

    public static async Task<IResult> Policies(AegisOpsDbContext db, HttpContext http) {
        var policies = await db.AiPolicies.AsNoTracking().OrderBy(policy => policy.Scope).ThenBy(policy => policy.Name)
            .Select(policy => new {
                policy.Id, policy.Name, scope = policy.Scope.ToString(), policy.ScopeId,
                policy.AllowedModelIdsJson, policy.RequestsPerMinute, policy.RequestsPerDay, policy.MaxPromptChars,
                sensitiveDataAction = policy.SensitiveDataAction.ToString(), policy.BlockedPatternsJson,
                policy.RedactPii, policy.StoreContent, policy.IsEnabled, policy.Version, policy.UpdatedById, policy.UpdatedAt,
            }).ToListAsync(http.RequestAborted);
        return Results.Ok(policies);
    }

    public static async Task<IResult> SavePolicy(
        AiPolicyRequest request, ClaimsPrincipal principal, UserManager<User> users, AegisOpsDbContext db, HttpContext http
    ) {
        if (request is null || request.AllowedModelIds is null || request.BlockedPatterns is null)
            return Results.Problem(title: "AI policy settings are invalid.", statusCode: StatusCodes.Status400BadRequest);
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out var userId)) return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        var actor = await users.FindByIdAsync(subject!);
        if (actor is null || !actor.IsActive) return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        var roles = await users.GetRolesAsync(actor);
        if ((request.Scope == PolicyScope.Global && !roles.Contains("Admin", StringComparer.Ordinal)) ||
            (request.Scope == PolicyScope.Team && !roles.Contains("Security", StringComparer.Ordinal)) ||
            request.Scope is not (PolicyScope.Global or PolicyScope.Team))
            return Results.Forbid();
        if (request.Scope == PolicyScope.Team && (request.ScopeId is null || !await db.Teams.AnyAsync(team => team.Id == request.ScopeId, http.RequestAborted)))
            return Results.Problem(title: "Team was not found.", statusCode: StatusCodes.Status404NotFound);
        if (request.AllowedModelIds.Length > 0 && await db.AiModels.CountAsync(model => request.AllowedModelIds.Contains(model.Id), http.RequestAborted) != request.AllowedModelIds.Distinct().Count())
            return Results.Problem(title: "One or more AI models were not found.", statusCode: StatusCodes.Status400BadRequest);

        var policy = request.Id is null ? null : await db.AiPolicies.SingleOrDefaultAsync(item => item.Id == request.Id, http.RequestAborted);
        if (request.Id is not null && policy is null) return Results.Problem(title: "AI policy was not found.", statusCode: StatusCodes.Status404NotFound);
        var now = TimeProvider.System.GetUtcNow();
        try {
            if (policy is null) {
                policy = AiPolicy.Create(request.Name, request.Scope, request.ScopeId, actor.Id, now);
                db.AiPolicies.Add(policy);
            } else if (policy.Scope != request.Scope || policy.ScopeId != request.ScopeId) {
                return Results.Problem(title: "An AI policy's scope cannot be changed.", statusCode: StatusCodes.Status400BadRequest);
            }
            policy.Configure(request.AllowedModelIds, request.RequestsPerMinute, request.RequestsPerDay, request.MaxPromptChars,
                request.SensitiveDataAction, request.BlockedPatterns, request.RedactPii, request.StoreContent,
                request.IsEnabled, actor.Id, now);
        } catch (ArgumentException) {
            return Results.Problem(title: "AI policy settings are invalid.", statusCode: StatusCodes.Status400BadRequest);
        }
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { policy.Id, policy.Name, scope = policy.Scope.ToString(), policy.ScopeId, policy.Version, policy.UpdatedAt });
    }

    private static async Task<(Guid? TeamId, IResult? Error)> ResolveUserAndTeam(
        ClaimsPrincipal principal, UserManager<User> users, AegisOpsDbContext db, Guid? requestedTeam, CancellationToken ct
    ) {
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out var userId)) return (null, Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized));
        var user = await users.FindByIdAsync(subject!);
        if (user is null || !user.IsActive) return (null, Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized));
        if (requestedTeam.HasValue) {
            var member = await db.TeamMembers.AsNoTracking().AnyAsync(item => item.UserId == userId && item.TeamId == requestedTeam, ct);
            if (!member) return (null, Results.Problem(title: "Team was not found.", statusCode: StatusCodes.Status404NotFound));
            return (requestedTeam, null);
        }
        var firstTeam = await db.TeamMembers.AsNoTracking().Where(item => item.UserId == userId)
            .Select(item => (Guid?)item.TeamId).FirstOrDefaultAsync(ct);
        return (firstTeam, null);
    }

    private static async Task<AiPolicy?> EffectivePolicy(AegisOpsDbContext db, Guid? teamId, CancellationToken ct) {
        if (teamId.HasValue) {
            var teamPolicy = await db.AiPolicies.AsNoTracking()
                .SingleOrDefaultAsync(policy => policy.Scope == PolicyScope.Team && policy.ScopeId == teamId && policy.IsEnabled, ct);
            if (teamPolicy is not null) return teamPolicy;
        }
        return await db.AiPolicies.AsNoTracking()
            .SingleOrDefaultAsync(policy => policy.Scope == PolicyScope.Global && policy.IsEnabled, ct);
    }
}
