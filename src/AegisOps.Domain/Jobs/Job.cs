namespace AegisOps.Domain.Jobs;

public enum JobStatus {
    Pending = 1,
    Running = 2,
    Succeeded = 3,
    DeadLettered = 4,
}

public enum JobType {
    EvaluateDeployment = 1,
    ExecuteDeployment = 2,
}

public sealed class Job {
    public Guid Id {get; private set;}
    public JobType Type {get; private set;}
    public string Payload {get; private set;}
    public JobStatus Status {get; private set;}
    public int Attempts {get; private set;}
    public int MaxAttempts {get; private set;}
    public DateTimeOffset ScheduledAt {get; private set;}
    public DateTimeOffset? LockedAt {get; private set;}
    public string? LockedBy {get; private set;}
    public DateTimeOffset? CompletedAt {get; private set;}
    public string? LastError {get; private set;}
    public string CorrelationId {get; private set;}
    public DateTimeOffset CreatedAt {get; private set;}

    private Job() {
        Payload = "{}";
        CorrelationId = string.Empty;
    }

    public static Job Enqueue(
        JobType type,
        string payload,
        DateTimeOffset createdAt,
        string correlationId
    ) {
        if (!Enum.IsDefined(type)) {
            throw new ArgumentException("Job type is invalid.", nameof(type));
        }

        if (string.IsNullOrWhiteSpace(payload)) {
            throw new ArgumentException("Payload is required.", nameof(payload));
        }

        return new Job {
            Id = Guid.CreateVersion7(),
            Type = type,
            Payload = payload,
            Status = JobStatus.Pending,
            Attempts = 0,
            MaxAttempts = 5,
            ScheduledAt = createdAt,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.CreateVersion7().ToString("N") : correlationId.Trim(),
            CreatedAt = createdAt,
        };
    }

    public void MarkRunning(DateTimeOffset at, string workerId) {
        if (Status != JobStatus.Pending) {
            throw new InvalidOperationException("Only a pending job can be claimed.");
        }

        Status = JobStatus.Running;
        Attempts++;
        LockedAt = at;
        LockedBy = workerId;
    }

    public void Succeed(DateTimeOffset at) {
        if (Status != JobStatus.Running) {
            throw new InvalidOperationException("Only a running job can succeed.");
        }

        Status = JobStatus.Succeeded;
        CompletedAt = at;
        LockedAt = null;
        LockedBy = null;
        LastError = null;
    }

    public void Fail(string error, DateTimeOffset at) {
        if (Status != JobStatus.Running) {
            throw new InvalidOperationException("Only a running job can fail.");
        }

        LastError = string.IsNullOrWhiteSpace(error) ? "Job failed." : error.Trim();
        LockedAt = null;
        LockedBy = null;
        if (Attempts >= MaxAttempts) {
            Status = JobStatus.DeadLettered;
            CompletedAt = at;
            return;
        }

        Status = JobStatus.Pending;
        var delaySeconds = Math.Min(60, Math.Pow(2, Attempts));
        ScheduledAt = at.AddSeconds(delaySeconds);
    }
}
