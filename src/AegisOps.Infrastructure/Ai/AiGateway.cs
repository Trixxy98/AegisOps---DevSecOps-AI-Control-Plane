using System.Diagnostics;
using System.Text.RegularExpressions;
using AegisOps.Domain.Ai;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Infrastructure.Ai;

public sealed record LlmMessage(string Role, string Content);
public sealed record LlmReply(string Content, int? PromptTokens, int? CompletionTokens);
public interface ILlmClient {
    Task<LlmReply> Chat(string model, IReadOnlyList<LlmMessage> messages, CancellationToken ct);
    Task<IReadOnlyList<string>> ListModels(CancellationToken ct);
}
public sealed record RateLimitDecision(bool Allowed, int RemainingMinute, int RemainingDay);
public interface IAiRateLimiter {
    Task<RateLimitDecision> Check(Guid userId, int minute, int day, CancellationToken ct);
}
public sealed record AiGatewayResult(int StatusCode, AiRequest Request, string? Content, RateLimitDecision? RateLimit);

public sealed class AiGateway(AegisOpsDbContext db, ILlmClient llm, IAiRateLimiter limiter,
    ISensitiveDataDetector detector, TimeProvider time) {
    public async Task<AiGatewayResult> Chat(Guid userId, Guid? teamId, string? modelName,
        IReadOnlyList<LlmMessage>? messages, string correlationId, CancellationToken ct, AiPurpose purpose = AiPurpose.Chat) {
        var now = time.GetUtcNow();
        var watch = Stopwatch.StartNew();
        RateLimitDecision? rate = null;
        SensitiveInspection inspection = new("", [], 0);
        // Cap the in-memory audit material; never persist uninspected input as a preview.
        var raw = messages is null ? "" : string.Join("\n", messages.Where(m => m is not null).Select(m => m.Content ?? ""));
        var policy = teamId is null ? null : await db.AiPolicies.AsNoTracking().Where(p => p.Scope == PolicyScope.Team && p.ScopeId == teamId && p.IsEnabled).SingleOrDefaultAsync(ct);
        policy ??= await db.AiPolicies.AsNoTracking().Where(p => p.Scope == PolicyScope.Global && p.IsEnabled).SingleOrDefaultAsync(ct);
        var model = modelName is null ? null : await db.AiModels.AsNoTracking().SingleOrDefaultAsync(m => m.Name == modelName, ct);
        try { inspection = detector.Inspect(raw, policy?.RedactPii ?? true); }
        catch (RegexMatchTimeoutException) { inspection = new SensitiveInspection("", [], 0); return await Finish(503, "InspectionUnavailable", null); }
        var safePreview = inspection.Redacted;
        if (policy is null) return await Finish(503, "PolicyUnavailable", null);
        if (model is null || !model.IsEnabled || !policy.AllowedModelIds.Contains(model.Id)) return await Finish(403, "ModelNotAllowed", null);
        try { rate = await limiter.Check(userId, policy.RequestsPerMinute, policy.RequestsPerDay, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return await Finish(503, "RateLimiterUnavailable", null); }
        if (!rate.Allowed) return await Finish(429, "RateLimited", null);
        if (messages is null || messages.Count is < 1 or > 32 || messages.Any(m =>
                m is null || m.Content is null || m.Role is not ("user" or "assistant" or "system")) ||
            raw.Length == 0 || raw.Length > policy.MaxPromptChars) return await Finish(400, "PromptRejected", null);
        try {
            if (policy.BlockedPatterns.Any(pattern => Regex.IsMatch(raw, pattern,
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))))
                return await Finish(400, "PromptRejected", null);
        } catch (ArgumentException) { return await Finish(503, "PolicyInvalid", null); }
          catch (RegexMatchTimeoutException) { return await Finish(503, "PolicyInvalid", null); }
        if (inspection.Redactions > 0 && policy.SensitiveDataAction == SensitiveDataAction.Block)
            return await Finish(422, "SensitiveData", null);
        IReadOnlyList<LlmMessage> forwarded = policy.SensitiveDataAction == SensitiveDataAction.Redact
            ? messages.Select(m => new LlmMessage(m.Role, detector.Inspect(m.Content, policy.RedactPii).Redacted)).ToArray()
            : messages;
        try {
            var reply = await llm.Chat(model.Name, forwarded, ct);
            // Response previews are always secret-redacted even if the policy allows full responses.
            var safeReply = detector.Inspect(reply.Content, true).Redacted;
            return await Finish(200, null, reply with { Content = safeReply }, safeReply);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return await Finish(503, "ProviderUnavailable", null);
        }

        async Task<AiGatewayResult> Finish(int status, string? reason, LlmReply? reply, string? safeReply = null) {
            watch.Stop();
            var request = AiRequest.Record(userId, teamId, model?.Id, policy,
                status == 200 ? AiRequestStatus.Completed : status == 503 ? AiRequestStatus.Failed : AiRequestStatus.Blocked,
                reason, raw, safePreviewOrEmpty(), reply?.Content, inspectionOrEmpty().Types, inspectionOrEmpty().Redactions,
                reply?.PromptTokens, reply?.CompletionTokens, watch.ElapsedMilliseconds, correlationId, now, purpose, safeReply);
            db.AiRequests.Add(request);
            db.AuditEvents.Add(AuditEvent.Record(now, "User", userId, "AI user", "ai.request", "AiRequest", request.Id,
                status == 200 ? "Succeeded" : "Denied", correlationId[..Math.Min(64, correlationId.Length)]));
            await db.SaveChangesAsync(ct);
            return new AiGatewayResult(status, request, reply?.Content, rate);
        }
        string safePreviewOrEmpty() => inspection?.Redacted ?? "";
        SensitiveInspection inspectionOrEmpty() => inspection ?? new SensitiveInspection("", [], 0);
    }
}
