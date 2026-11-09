namespace AegisOps.Domain.Security;

public sealed class FindingSuppression {
    public Guid Id {get; private set;}
    public Guid ProjectId {get; private set;}
    public string Fingerprint {get; private set;}
    public string Reason {get; private set;}
    public Guid CreatedById {get; private set;}
    public DateTimeOffset CreatedAt {get; private set;}
    public DateTimeOffset? ExpiresAt {get; private set;}

    private FindingSuppression() {
        Fingerprint = string.Empty;
        Reason = string.Empty;
    }

    public static FindingSuppression Create(
        Guid projectId,
        string fingerprint,
        string reason,
        Guid createdById,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt = null
    ) {
        if (projectId == Guid.Empty || createdById == Guid.Empty) {
            throw new ArgumentException("Project and actor IDs are required.");
        }

        if (string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(reason)) {
            throw new ArgumentException("Fingerprint and reason are required.");
        }

        return new FindingSuppression {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Fingerprint = fingerprint.Trim(),
            Reason = reason.Trim(),
            CreatedById = createdById,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>Re-suppress an existing fingerprint (e.g. after it expired) with a new reason and expiry.</summary>
    public void Renew(string reason, Guid actorId, DateTimeOffset now, DateTimeOffset? expiresAt) {
        if (actorId == Guid.Empty || string.IsNullOrWhiteSpace(reason)) {
            throw new ArgumentException("Actor and reason are required.");
        }

        Reason = reason.Trim();
        CreatedById = actorId;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    public bool IsActive(DateTimeOffset now) {
        return ExpiresAt is null || ExpiresAt > now;
    }
}
