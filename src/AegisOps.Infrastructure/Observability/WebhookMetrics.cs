using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AegisOps.Infrastructure.Observability;

/// <summary>Result of one committed webhook delivery attempt. Mutually exclusive per attempt.</summary>
public enum WebhookDeliveryOutcome {
    /// <summary>The receiver returned 2xx.</summary>
    Succeeded = 1,
    /// <summary>The attempt failed and the job was scheduled for a retry.</summary>
    Failed = 2,
    /// <summary>The final attempt failed and the job was dead-lettered.</summary>
    DeadLettered = 3,
}

/// <summary>
/// Webhook delivery telemetry on <c>Meter("AegisOps")</c> (docs/15 §2, docs/phase5-webhooks.md).
/// Recorded by the Worker only after the attempt result commits. The only label is the bounded
/// <c>outcome</c>: never team, URL, event, deployment or job identifiers.
/// </summary>
public static class WebhookMetrics {
    private static readonly Meter Meter = new(JobMetrics.MeterName);
    private static readonly Counter<long> Deliveries = Meter.CreateCounter<long>("aegisops_webhook_deliveries_total",
        description: "Committed webhook delivery attempts by outcome.");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("aegisops_webhook_delivery_duration_seconds",
        unit: "s", description: "Outbound webhook HTTP attempt time (signing, DNS, TLS and response headers).");

    /// <summary>The sender's HTTP timeout is 10 s.</summary>
    public static IReadOnlyList<double> DurationBuckets { get; } = Array.AsReadOnly<double>([0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30]);

    public static void InitializeZeroSeries() {
        foreach (var outcome in Enum.GetValues<WebhookDeliveryOutcome>())
            Deliveries.Add(0, new TagList { { "outcome", outcome.ToString() } });
    }

    /// <summary>Records one committed attempt. <paramref name="durationSeconds"/> is null when no timing exists (lease expiry).</summary>
    public static void Record(WebhookDeliveryOutcome outcome, double? durationSeconds) {
        var tags = new TagList { { "outcome", outcome.ToString() } };
        Deliveries.Add(1, tags);
        if (durationSeconds is { } seconds) Duration.Record(Math.Max(0, seconds), tags);
    }
}
