namespace AegisOps.Domain.Deploy;

public sealed class DeploymentEvent {
    public Guid Id {get; private set;}
    public Guid DeploymentId {get; private set;}
    public int Sequence {get; private set;}
    public DateTimeOffset Timestamp {get; private set;}
    public DeploymentEventType Type {get; private set;}
    public string Message {get; private set;}

    private DeploymentEvent() {
        Message = string.Empty;
    }

    public static DeploymentEvent Create(
        Guid deploymentId,
        int sequence,
        DateTimeOffset timestamp,
        DeploymentEventType type,
        string message
    ) {
        if (deploymentId == Guid.Empty) {
            throw new ArgumentException("Deployment ID is required.", nameof(deploymentId));
        }

        if (sequence < 1) {
            throw new ArgumentException("Sequence must start at 1.", nameof(sequence));
        }

        if (string.IsNullOrWhiteSpace(message)) {
            throw new ArgumentException("Message is required.", nameof(message));
        }

        return new DeploymentEvent {
            Id = Guid.CreateVersion7(),
            DeploymentId = deploymentId,
            Sequence = sequence,
            Timestamp = timestamp,
            Type = type,
            Message = message.Trim(),
        };
    }
}
