using System.Diagnostics;
using System.Diagnostics.Metrics;
using AegisOps.Domain.Ai;

namespace AegisOps.Infrastructure.Observability;

public static class AiMetrics {
    public const string MeterName = "AegisOps.Ai";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("aegisops_ai_requests_total");
    private static readonly Counter<long> SensitiveDetections = Meter.CreateCounter<long>("aegisops_ai_sensitive_detections_total");
    // No model label: model names can be user-defined (same rule as the Ollama span).
    private static readonly Histogram<double> Latency = Meter.CreateHistogram<double>("aegisops_ai_request_latency_seconds",
        unit: "s", description: "Gateway request time for committed AI requests, by status.");
    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>("aegisops_ai_tokens_total",
        description: "Provider-reported tokens for completed AI requests.");
    public static readonly IReadOnlyList<double> LatencyBuckets = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120];
    private static readonly string[] KnownReasons = [
        "None", "PromptRejected", "InspectionUnavailable", "PolicyUnavailable", "ModelNotAllowed",
        "RateLimiterUnavailable", "RateLimited", "PolicyInvalid", "SensitiveData", "ProviderUnavailable", "Other",
    ];
    private static readonly HashSet<string> KnownTypes = [
        "AWS_ACCESS_KEY", "AWS_SECRET_KEY", "GITHUB_TOKEN", "GENERIC_API_KEY", "JWT", "PRIVATE_KEY",
        "CONNECTION_STRING", "MY_NRIC", "PHONE_MY", "IPV4_PRIVATE", "EMAIL", "CREDIT_CARD",
    ];

    public static void InitializeZeroSeries() {
        foreach (var status in Enum.GetValues<AiRequestStatus>())
            foreach (var reason in KnownReasons)
                Requests.Add(0, new TagList { { "status", status.ToString() }, { "reason", reason } });
        foreach (var type in KnownTypes) {
            foreach (var action in Enum.GetValues<SensitiveDataAction>())
                SensitiveDetections.Add(0, new TagList { { "type", type }, { "policy_action", action.ToString() } });
            SensitiveDetections.Add(0, new TagList { { "type", type }, { "policy_action", "NoPolicy" } });
        }
        foreach (var kind in new[] { "prompt", "completion" })
            Tokens.Add(0, new TagList { { "kind", kind } });
    }

    public static void Record(AiRequestStatus status, string? reason, IEnumerable<string> detectedTypes,
        SensitiveDataAction? action, long? latencyMs = null, int? promptTokens = null, int? completionTokens = null) {
        if (latencyMs is >= 0) Latency.Record(latencyMs.Value / 1000d, new TagList { { "status", status.ToString() } });
        if (promptTokens is > 0) Tokens.Add(promptTokens.Value, new TagList { { "kind", "prompt" } });
        if (completionTokens is > 0) Tokens.Add(completionTokens.Value, new TagList { { "kind", "completion" } });
        var safeReason = reason is null ? "None" :
            KnownReasons.Contains(reason, StringComparer.Ordinal) ? reason : "Other";
        Requests.Add(1, new TagList { { "status", status.ToString() }, { "reason", safeReason } });
        foreach (var type in detectedTypes.Distinct(StringComparer.Ordinal)) {
            if (KnownTypes.Contains(type))
                SensitiveDetections.Add(1, new TagList { { "type", type }, { "policy_action", action?.ToString() ?? "NoPolicy" } });
        }
    }
}
