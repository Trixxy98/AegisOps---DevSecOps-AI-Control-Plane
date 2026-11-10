namespace AegisOps.Domain.Deploy;

/// <summary>
/// The last Docker runtime observation the Worker recorded for one Docker-target environment.
/// The API never talks to Docker; it serves this bounded, redacted snapshot instead.
/// </summary>
public sealed class EnvironmentRuntimeSnapshot {
    public Guid EnvironmentId { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }
    public bool InspectionAvailable { get; private set; }
    public string? Reason { get; private set; }
    public List<RuntimeContainer> Containers { get; private set; } = [];

    private EnvironmentRuntimeSnapshot() { }

    public static EnvironmentRuntimeSnapshot Create(Guid environmentId) => new() { EnvironmentId = environmentId };

    public void Observe(DateTimeOffset observedAt, IEnumerable<RuntimeContainer> containers) {
        ObservedAt = observedAt;
        InspectionAvailable = true;
        Reason = null;
        Containers = containers.ToList();
    }

    public void Unavailable(DateTimeOffset observedAt, string reason) {
        ObservedAt = observedAt;
        InspectionAvailable = false;
        Reason = reason;
        Containers = [];
    }
}

public sealed class RuntimeContainer {
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Health { get; set; }
    public string Image { get; set; } = "";
    public string? ImageId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public List<string> Ports { get; set; } = [];
}
