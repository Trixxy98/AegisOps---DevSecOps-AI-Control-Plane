using System.Diagnostics;
using System.Diagnostics.Metrics;
using AegisOps.Domain.Ai;

namespace AegisOps.Infrastructure.Observability;

public static class AiMetrics {
    public const string MeterName = "AegisOps.Ai";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("aegisops_ai_requests_total");
    private static readonly Counter<long> SensitiveDetections = Meter.CreateCounter<long>("aegisops_ai_sensitive_detections_total");
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
    }

    public static void Record(AiRequestStatus status, string? reason, IEnumerable<string> detectedTypes,
        SensitiveDataAction? action) {
        var safeReason = reason is null ? "None" :
            KnownReasons.Contains(reason, StringComparer.Ordinal) ? reason : "Other";
        Requests.Add(1, new TagList { { "status", status.ToString() }, { "reason", safeReason } });
        foreach (var type in detectedTypes.Distinct(StringComparer.Ordinal)) {
            if (KnownTypes.Contains(type))
                SensitiveDetections.Add(1, new TagList { { "type", type }, { "policy_action", action?.ToString() ?? "NoPolicy" } });
        }
    }
}
