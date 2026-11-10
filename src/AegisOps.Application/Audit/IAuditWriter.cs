using AegisOps.Domain.Audit;

namespace AegisOps.Application.Audit;

public interface IAuditWriter {
    void Write(AuditEvent auditEvent);
}

public static class AuditWriterExtensions {
    /// <summary>Records a successful action by a user.</summary>
    public static void Write(this IAuditWriter writer, DateTimeOffset timestamp, Guid? actorId,
        string? actorDisplay, string action, string resourceType, Guid? resourceId) =>
        writer.Write(timestamp, "User", actorId, actorDisplay, action, resourceType, resourceId);

    /// <summary>Records a successful action by an explicit actor type ("User" or "ApiKey").</summary>
    public static void Write(this IAuditWriter writer, DateTimeOffset timestamp, string actorType, Guid? actorId,
        string? actorDisplay, string action, string resourceType, Guid? resourceId) {
        writer.Write(AuditEvent.Record(timestamp, actorType, actorId,
            string.IsNullOrWhiteSpace(actorDisplay) ? "unknown" : actorDisplay.Trim(),
            action, resourceType, resourceId, "Succeeded", null));
    }
}
