namespace AegisOps.Domain.Deploy;

public enum GitHubGateStatus {
    /// <summary>Webhook accepted; the Worker has not mapped it yet.</summary>
    Received = 1,
    /// <summary>Linked to an AegisOps deployment that has not reached a decision GitHub needs to hear.</summary>
    Linked = 2,
    /// <summary>A decision is chosen and its callback job is queued.</summary>
    DecisionPending = 3,
    /// <summary>GitHub accepted the review (HTTP 2xx).</summary>
    Reported = 4,
    /// <summary>GitHub refused the review with a non-retryable status (for example the run was cancelled or timed out).</summary>
    CallbackFailed = 5,
}

/// <summary>
/// One GitHub <c>deployment_protection_rule</c> request (docs/github-deployment-gate.md): the linkage between a GitHub
/// Actions workflow run waiting on an environment and the AegisOps deployment that decides it. The delivery ID is unique,
/// so a replayed webhook is a no-op. Only routing data is stored: no payload body, token or signature.
/// </summary>
public sealed class GitHubDeploymentGate {
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    /// <summary>GitHub's limit for a protection rule comment/status report.</summary>
    public const int MaxCommentLength = 1024;

    public Guid Id {get; private set;}
    public string DeliveryId {get; private set;}
    public DateTimeOffset ReceivedAt {get; private set;}
    public long InstallationId {get; private set;}
    public string RepositoryFullName {get; private set;}
    public long RunId {get; private set;}
    public string EnvironmentName {get; private set;}
    public string CommitSha {get; private set;}
    public string? Ref {get; private set;}
    public string CallbackUrl {get; private set;}
    public string RunUrl {get; private set;}
    public Guid? ProjectId {get; private set;}
    public Guid? DeploymentId {get; private set;}
    public GitHubGateStatus Status {get; private set;}
    public string? Decision {get; private set;}
    public string? DecisionComment {get; private set;}
    public DateTimeOffset? DecidedAt {get; private set;}
    public DateTimeOffset? ReportedAt {get; private set;}
    public int? CallbackStatusCode {get; private set;}
    public string CorrelationId {get; private set;}

    private GitHubDeploymentGate() {
        DeliveryId = string.Empty;
        RepositoryFullName = string.Empty;
        EnvironmentName = string.Empty;
        CommitSha = string.Empty;
        CallbackUrl = string.Empty;
        RunUrl = string.Empty;
        CorrelationId = string.Empty;
    }

    public static GitHubDeploymentGate Receive(
        string deliveryId,
        DateTimeOffset receivedAt,
        long installationId,
        string repositoryFullName,
        long runId,
        string environmentName,
        string commitSha,
        string? reference,
        string callbackUrl,
        string runUrl
    ) {
        if (string.IsNullOrWhiteSpace(deliveryId) || deliveryId.Length > 64) throw new ArgumentException("Delivery ID is invalid.", nameof(deliveryId));
        if (installationId <= 0) throw new ArgumentException("Installation ID is required.", nameof(installationId));
        if (string.IsNullOrWhiteSpace(repositoryFullName) || !repositoryFullName.Contains('/', StringComparison.Ordinal) || repositoryFullName.Length > 300)
            throw new ArgumentException("Repository must look like owner/repo.", nameof(repositoryFullName));
        if (runId <= 0) throw new ArgumentException("Run ID is required.", nameof(runId));
        if (string.IsNullOrWhiteSpace(environmentName) || environmentName.Length > 255) throw new ArgumentException("Environment is required.", nameof(environmentName));
        if (string.IsNullOrWhiteSpace(commitSha) || commitSha.Length > 64) throw new ArgumentException("Commit SHA is required.", nameof(commitSha));
        if (string.IsNullOrWhiteSpace(callbackUrl) || callbackUrl.Length > 500) throw new ArgumentException("Callback URL is required.", nameof(callbackUrl));
        if (string.IsNullOrWhiteSpace(runUrl) || runUrl.Length > 500) throw new ArgumentException("Run URL is required.", nameof(runUrl));
        return new GitHubDeploymentGate {
            Id = Guid.CreateVersion7(),
            DeliveryId = deliveryId.Trim(),
            ReceivedAt = receivedAt,
            InstallationId = installationId,
            RepositoryFullName = repositoryFullName.Trim(),
            RunId = runId,
            EnvironmentName = environmentName.Trim(),
            CommitSha = commitSha.Trim().ToLowerInvariant(),
            Ref = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim()[..Math.Min(reference.Trim().Length, 255)],
            CallbackUrl = callbackUrl.Trim(),
            RunUrl = runUrl.Trim(),
            Status = GitHubGateStatus.Received,
            CorrelationId = Guid.CreateVersion7().ToString("N"),
        };
    }

    /// <summary>The repository owner (the part before '/').</summary>
    public string Owner => RepositoryFullName[..RepositoryFullName.IndexOf('/', StringComparison.Ordinal)];

    /// <summary>The repository name (the part after '/').</summary>
    public string Repository => RepositoryFullName[(RepositoryFullName.IndexOf('/', StringComparison.Ordinal) + 1)..];

    public void Link(Guid projectId, Guid deploymentId, string correlationId) {
        if (Status != GitHubGateStatus.Received) throw new InvalidOperationException("Only a received gate can be linked.");
        if (projectId == Guid.Empty || deploymentId == Guid.Empty) throw new ArgumentException("Project and deployment are required.");
        ProjectId = projectId;
        DeploymentId = deploymentId;
        CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? CorrelationId : correlationId.Trim();
        Status = GitHubGateStatus.Linked;
    }

    /// <summary>
    /// Chooses the answer GitHub will receive. The first decision wins: a later transition (for example a Failed after an
    /// approved review was refused) never posts a second, contradicting review. Returns false when already decided.
    /// </summary>
    public bool Decide(string decision, string? comment, DateTimeOffset at, Guid? projectId = null) {
        if (decision is not (Approved or Rejected)) throw new ArgumentException("Decision must be approved or rejected.", nameof(decision));
        if (Decision is not null) return false;
        Decision = decision;
        DecisionComment = Truncate(comment);
        DecidedAt = at;
        ProjectId ??= projectId;
        Status = GitHubGateStatus.DecisionPending;
        return true;
    }

    /// <summary>Records the comment actually sent and GitHub's acknowledgement; later callback attempts become no-ops.</summary>
    public void MarkReported(string comment, int statusCode, DateTimeOffset at) {
        if (Decision is null) throw new InvalidOperationException("No decision to report.");
        DecisionComment = Truncate(comment);
        CallbackStatusCode = statusCode;
        ReportedAt = at;
        Status = GitHubGateStatus.Reported;
    }

    public void MarkCallbackFailed(int statusCode, DateTimeOffset at) {
        if (Decision is null) throw new InvalidOperationException("No decision to report.");
        CallbackStatusCode = statusCode;
        ReportedAt = at;
        Status = GitHubGateStatus.CallbackFailed;
    }

    public bool IsSettled => Status is GitHubGateStatus.Reported or GitHubGateStatus.CallbackFailed;

    public static string? Truncate(string? comment) {
        if (string.IsNullOrWhiteSpace(comment)) return null;
        var trimmed = comment.Trim();
        return trimmed.Length <= MaxCommentLength ? trimmed : trimmed[..(MaxCommentLength - 1)] + "…";
    }
}
