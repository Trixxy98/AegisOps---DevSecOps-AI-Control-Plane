using AegisOps.Domain.Audit;

namespace AegisOps.Application.Audit;

public interface IAuditWriter {
    void Write(AuditEvent auditEvent);
}

public static class AuditWriterExtensions {
    public static void Write(this IAuditWriter writer, DateTimeOffset timestamp, Guid? actorId,
        string? actorDisplay, string action, string resourceType, Guid? resourceId) {
        writer.Write(AuditEvent.Record(timestamp, "User", actorId,
            string.IsNullOrWhiteSpace(actorDisplay) ? "unknown" : actorDisplay.Trim(),
            action, resourceType, resourceId, "Succeeded", null));
    }
}
