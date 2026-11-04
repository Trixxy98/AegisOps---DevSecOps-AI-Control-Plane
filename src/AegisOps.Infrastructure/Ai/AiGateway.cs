using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AegisOps.Application.Audit;
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
    ISensitiveDataDetector detector, TimeProvider time, IAuditWriter audit) {
    public async Task<AiGatewayResult> Chat(Guid userId, Guid? teamId, string? modelName,
        IReadOnlyList<LlmMessage>? messages, string correlationId, CancellationToken ct, AiPurpose purpose = AiPurpose.Chat) {
        var now = time.GetUtcNow();
        var watch = Stopwatch.StartNew();
        RateLimitDecision? rate = null;
        SensitiveInspection inspection = new("", [], 0);
        var policy = teamId is null ? null : await db.AiPolicies.AsNoTracking().Where(p => p.Scope == PolicyScope.Team && p.ScopeId == teamId && p.IsEnabled).SingleOrDefaultAsync(ct);
        policy ??= await db.AiPolicies.AsNoTracking().Where(p => p.Scope == PolicyScope.Global && p.IsEnabled).SingleOrDefaultAsync(ct);
        var model = modelName is null ? null : await db.AiModels.AsNoTracking().SingleOrDefaultAsync(m => m.Name == modelName, ct);
        var raw = "";
        if (messages is not null && messages.Count <= 32) {
            var rawLength = messages.Where(m => m is not null).Sum(m => (long)(m.Content?.Length ?? 0)) +
                Math.Max(0, messages.Count(m => m is not null) - 1);
            if (policy is not null && rawLength > policy.MaxPromptChars) {
                // Use a bounded sentinel for request content; hash the original incrementally.
                // Do not concatenate, inspect, preview, or store the oversized prompt.
                raw = "[OVERSIZED_PROMPT]";
                var promptHash = HashPrompt(messages);
                return await Finish(400, "PromptRejected", null, storePromptContent: false,
                    promptChars: rawLength, promptHash: promptHash);
            }
            raw = string.Join("\n", messages.Where(m => m is not null).Select(m => m.Content ?? ""));
        }
        try { inspection = detector.Inspect(raw, policy?.RedactPii ?? true); }
        catch (RegexMatchTimeoutException) { inspection = new SensitiveInspection("", [], 0); return await Finish(503, "InspectionUnavailable", null, storePromptContent: false); }
        var safePreview = inspection.Redacted;
        if (policy is null) return await Finish(503, "PolicyUnavailable", null);
        if (model is null || !model.IsEnabled || !policy.AllowedModelIds.Contains(model.Id)) return await Finish(403, "ModelNotAllowed", null);
        try { rate = await limiter.Check(userId, policy.RequestsPerMinute, policy.RequestsPerDay, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return await Finish(503, "RateLimiterUnavailable", null); }
        if (!rate.Allowed) return await Finish(429, "RateLimited", null);
        if (messages is null || messages.Count is < 1 or > 32 || messages.Any(m =>
                m is null || m.Content is null || m.Role is not ("user" or "assistant" or "system")) ||
            raw.Length == 0) return await Finish(400, "PromptRejected", null);
        try {
            if (policy.BlockedPatterns.Any(pattern => Regex.IsMatch(raw, pattern,
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))))
                return await Finish(400, "PromptRejected", null);
        } catch (ArgumentException) { return await Finish(503, "PolicyInvalid", null); }
          catch (RegexMatchTimeoutException) { return await Finish(503, "PolicyInvalid", null); }
        if (inspection.Redactions > 0 && policy.SensitiveDataAction == SensitiveDataAction.Block)
            return await Finish(422, "SensitiveData", null);
        IReadOnlyList<LlmMessage> forwarded;
        try {
            forwarded = policy.SensitiveDataAction == SensitiveDataAction.Redact
                ? messages.Select(m => new LlmMessage(m.Role, detector.Inspect(m.Content, policy.RedactPii).Redacted)).ToArray()
                : messages;
        } catch (RegexMatchTimeoutException) {
            return await Finish(503, "InspectionUnavailable", null, storePromptContent: false);
        }
        LlmReply reply;
        try {
            reply = await llm.Chat(model.Name, forwarded, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return await Finish(503, "ProviderUnavailable", null);
        }
        // Never return or persist the provider response until inspection succeeds.
        string safeReply;
        try { safeReply = detector.Inspect(reply.Content, true).Redacted; }
        catch (RegexMatchTimeoutException) { return await Finish(503, "InspectionUnavailable", null); }
        return await Finish(200, null, reply with { Content = safeReply }, safeReply);

        async Task<AiGatewayResult> Finish(int status, string? reason, LlmReply? reply, string? safeReply = null,
            bool storePromptContent = true, long? promptChars = null, string? promptHash = null) {
            watch.Stop();
            var request = AiRequest.Record(userId, teamId, model?.Id, policy,
                status == 200 ? AiRequestStatus.Completed : status == 503 ? AiRequestStatus.Failed : AiRequestStatus.Blocked,
                reason, raw, safePreviewOrEmpty(), reply?.Content, inspectionOrEmpty().Types, inspectionOrEmpty().Redactions,
                reply?.PromptTokens, reply?.CompletionTokens, watch.ElapsedMilliseconds, correlationId, now, purpose, safeReply,
                storePromptContent, promptChars, promptHash);
            db.AiRequests.Add(request);
            audit.Write(AuditEvent.Record(now, "User", userId, "AI user", "ai.request", "AiRequest", request.Id,
                status == 200 ? "Succeeded" : "Denied", correlationId[..Math.Min(64, correlationId.Length)]));
            await db.SaveChangesAsync(ct);
            return new AiGatewayResult(status, request, reply?.Content, rate);
        }
        string safePreviewOrEmpty() => inspection?.Redacted ?? "";
        SensitiveInspection inspectionOrEmpty() => inspection ?? new SensitiveInspection("", [], 0);
    }

    private static string HashPrompt(IReadOnlyList<LlmMessage> messages) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[4096];
        var first = true;
        foreach (var message in messages) {
            if (message is null) continue;
            if (!first) hash.AppendData("\n"u8);
            first = false;
            var encoder = Encoding.UTF8.GetEncoder();
            var remaining = (message.Content ?? "").AsSpan();
            do {
                encoder.Convert(remaining, buffer, true, out var charsUsed, out var bytesUsed, out _);
                hash.AppendData(buffer[..bytesUsed]);
                remaining = remaining[charsUsed..];
            } while (!remaining.IsEmpty);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
