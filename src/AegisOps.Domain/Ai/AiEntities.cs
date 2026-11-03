using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisOps.Domain.Policy;

namespace AegisOps.Domain.Ai;

public enum AiProvider { Ollama }
public enum AiPurpose { Chat, DeploymentSummary, FindingsExplanation }
public enum AiRequestStatus { Completed, Blocked, Failed }
public enum SensitiveDataAction { Allow, Redact, Block }

public sealed class AiModel {
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public AiProvider Provider { get; private set; }
    public string DisplayName { get; private set; } = "";
    public bool IsEnabled { get; private set; }
    public int MaxContextTokens { get; private set; }
    public string CapabilitiesJson { get; private set; } = "[]";
    public static AiModel Create(string name, int contextTokens = 8192) {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 150 || contextTokens < 1) throw new ArgumentException("Invalid model.");
        return new AiModel { Id = Guid.CreateVersion7(), Name = name, DisplayName = name, Provider = AiProvider.Ollama,
            IsEnabled = false, MaxContextTokens = contextTokens, CapabilitiesJson = "[\"chat\"]" };
    }
    public void SetEnabled(bool enabled) => IsEnabled = enabled;
}

public sealed class AiPolicy {
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public PolicyScope Scope { get; private set; }
    public Guid? ScopeId { get; private set; }
    public string AllowedModelIdsJson { get; private set; } = "[]";
    public Guid[] AllowedModelIds => JsonSerializer.Deserialize<Guid[]>(AllowedModelIdsJson) ?? [];
    public int RequestsPerMinute { get; private set; } = 20;
    public int RequestsPerDay { get; private set; } = 500;
    public int MaxPromptChars { get; private set; } = 16000;
    public SensitiveDataAction SensitiveDataAction { get; private set; } = SensitiveDataAction.Redact;
    public string BlockedPatternsJson { get; private set; } = "[]";
    public string[] BlockedPatterns => JsonSerializer.Deserialize<string[]>(BlockedPatternsJson) ?? [];
    public bool RedactPii { get; private set; }
    public bool StoreContent { get; private set; }
    public bool IsEnabled { get; private set; } = true;
    public int Version { get; private set; } = 1;
    public Guid UpdatedById { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AiPolicy Create(string name, PolicyScope scope, Guid? teamId, Guid actor, DateTimeOffset now) {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 150 ||
            (scope == PolicyScope.Global && teamId is not null) || (scope == PolicyScope.Team && (teamId is null || teamId == Guid.Empty)) ||
            scope is not (PolicyScope.Global or PolicyScope.Team)) throw new ArgumentException("Invalid AI policy scope or name.");
        return new AiPolicy { Id = Guid.CreateVersion7(), Name = name, Scope = scope, ScopeId = teamId, UpdatedById = actor, UpdatedAt = now };
    }
    public void Configure(Guid[] models, int minute, int day, int maxChars, SensitiveDataAction action,
        string[] blockedPatterns, bool redactPii, bool storeContent, bool enabled, Guid actor, DateTimeOffset now) {
        if (models.Length > 100 || models.Distinct().Count() != models.Length || models.Contains(Guid.Empty) ||
            minute is < 1 or > 10000 || day is < 1 or > 100000 || maxChars is < 1 or > 100000 ||
            blockedPatterns.Length > 20 || blockedPatterns.Any(p => string.IsNullOrEmpty(p) || p.Length > 256) || !Enum.IsDefined(action))
            throw new ArgumentException("Invalid AI policy controls.");
        AllowedModelIdsJson = JsonSerializer.Serialize(models);
        RequestsPerMinute = minute; RequestsPerDay = day; MaxPromptChars = maxChars;
        SensitiveDataAction = action; BlockedPatternsJson = JsonSerializer.Serialize(blockedPatterns);
        RedactPii = redactPii; StoreContent = storeContent; IsEnabled = enabled;
        UpdatedById = actor; UpdatedAt = now; Version++;
    }
}

public sealed class AiRequest {
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? TeamId { get; private set; }
    public Guid? AiModelId { get; private set; }
    public Guid? AiPolicyId { get; private set; }
    public AiRequestStatus Status { get; private set; }
    public AiPurpose Purpose { get; private set; }
    public int PromptChars { get; private set; }
    public int ResponseChars { get; private set; }
    public int? PromptTokens { get; private set; }
    public int? CompletionTokens { get; private set; }
    public long LatencyMs { get; private set; }
    public string DetectedSensitiveTypesJson { get; private set; } = "[]";
    public int RedactionsApplied { get; private set; }
    public string? BlockReason { get; private set; }
    public string PromptHash { get; private set; } = "";
    public string? PromptPreview { get; private set; }
    public string? ResponsePreview { get; private set; }
    public string? PromptContent { get; private set; }
    public string? ResponseContent { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public static AiRequest Record(Guid userId, Guid? teamId, Guid? modelId, AiPolicy? policy, AiRequestStatus status,
        string? reason, string rawPrompt, string safePrompt, string? response, string[] detections, int redactions,
        int? promptTokens, int? completionTokens, long latencyMs, string correlationId, DateTimeOffset now,
        AiPurpose purpose = AiPurpose.Chat, string? safeResponse = null, bool storePromptContent = true) {
        return new AiRequest {
            Id = Guid.CreateVersion7(), UserId = userId, TeamId = teamId, AiModelId = modelId, AiPolicyId = policy?.Id,
            Status = status, Purpose = purpose, BlockReason = reason, PromptChars = rawPrompt.Length,
            PromptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPrompt))),
            PromptPreview = safePrompt[..Math.Min(500, safePrompt.Length)],
            ResponsePreview = safeResponse?[..Math.Min(500, safeResponse.Length)],
            PromptContent = policy?.StoreContent == true && storePromptContent ? rawPrompt : null,
            ResponseContent = policy?.StoreContent == true ? response : null,
            ResponseChars = response?.Length ?? 0, DetectedSensitiveTypesJson = JsonSerializer.Serialize(detections),
            RedactionsApplied = redactions, PromptTokens = promptTokens, CompletionTokens = completionTokens,
            LatencyMs = latencyMs, CorrelationId = correlationId[..Math.Min(64, correlationId.Length)], CreatedAt = now,
        };
    }
}
