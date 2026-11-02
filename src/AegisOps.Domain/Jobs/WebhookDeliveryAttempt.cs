namespace AegisOps.Domain.Jobs;

public sealed class WebhookDeliveryAttempt {
    public Guid Id { get; private set; }
    public Guid JobId { get; private set; }
    public int AttemptNumber { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string Status { get; private set; } = "Running";
    public string? Error { get; private set; }

    private WebhookDeliveryAttempt() { }

    public static WebhookDeliveryAttempt Start(Guid jobId, int attemptNumber, DateTimeOffset at) => new() {
        Id = Guid.CreateVersion7(), JobId = jobId, AttemptNumber = attemptNumber, StartedAt = at, Status = "Running"
    };

    public void Succeed(DateTimeOffset at) { Status = "Succeeded"; CompletedAt = at; Error = null; }
    public void Fail(DateTimeOffset at) { Status = "Failed"; CompletedAt = at; Error = "Webhook delivery failed."; }
}
