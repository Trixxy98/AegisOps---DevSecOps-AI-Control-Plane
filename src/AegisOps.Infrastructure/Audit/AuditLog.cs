using AegisOps.Domain.Audit;
using AegisOps.Infrastructure.Persistence;

namespace AegisOps.Infrastructure.Audit;

public static class AuditLog {
    public static void Write(
        AegisOpsDbContext db,
        DateTimeOffset timestamp,
        Guid? actorId,
        string? actorDisplay,
        string action,
        string resourceType,
        Guid? resourceId
    ) {
        db.AuditEvents.Add(AuditEvent.Record(
            timestamp,
            "User",
            actorId,
            string.IsNullOrWhiteSpace(actorDisplay) ? "unknown" : actorDisplay.Trim(),
            action,
            resourceType,
            resourceId,
            "Succeeded",
            null
        ));
    }
}
