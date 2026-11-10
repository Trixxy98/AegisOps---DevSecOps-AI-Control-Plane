namespace AegisOps.Domain.Organization;

/// <summary>
/// All-time webhook delivery counters for one team. Rows are only incremented by an atomic
/// upsert committed with the attempt result (see TeamWebhookDeliveryStatsStore), never derived
/// from jobs, so the seven-day Succeeded-job purge cannot erase them.
/// </summary>
public sealed class TeamWebhookDeliveryStats {
    public Guid TeamId { get; private set; }
    /// <summary>Attempts the receiver acknowledged with a 2xx response.</summary>
    public long Delivered { get; private set; }
    /// <summary>Every failed attempt, including retried ones and the final attempt of a dead-lettered job.</summary>
    public long FailedAttempts { get; private set; }
    /// <summary>Notification jobs that exhausted their attempts.</summary>
    public long DeadLettered { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public DateTimeOffset? LastFailureAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private TeamWebhookDeliveryStats() { }
}
