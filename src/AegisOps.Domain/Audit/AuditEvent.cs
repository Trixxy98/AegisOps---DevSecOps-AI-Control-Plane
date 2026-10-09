namespace AegisOps.Domain.Audit;

public sealed class AuditEvent {
    public Guid Id {get; private set;}
    public DateTimeOffset Timestamp {get; private set;}
    public string ActorType {get; private set;}
    public Guid? ActorId {get; private set;}
    public string ActorDisplay {get; private set;}
    public string Action {get; private set;}
    public string ResourceType {get; private set;}
    public Guid? ResourceId {get; private set;}
    public string Outcome {get; private set;}
    public string? CorrelationId {get; private set;}

    private AuditEvent() {
        ActorType = string.Empty;
        ActorDisplay = string.Empty;
        Action = string.Empty;
        ResourceType = string.Empty;
        Outcome = string.Empty;
    }

    public static AuditEvent Record(
        DateTimeOffset timestamp,
        string actorType,
        Guid? actorId,
        string actorDisplay,
        string action,
        string resourceType,
        Guid? resourceId,
        string outcome,
        string? correlationId
    ) {
        return new AuditEvent {
            Id = Guid.CreateVersion7(),
            Timestamp = timestamp,
            ActorType = actorType,
            ActorId = actorId,
            ActorDisplay = actorDisplay,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Outcome = outcome,
            CorrelationId = correlationId,
        };
    }
}
