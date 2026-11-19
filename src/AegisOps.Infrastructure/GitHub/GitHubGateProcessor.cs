using System.Globalization;
using System.Text;
using System.Text.Json;
using AegisOps.Application.Audit;
using AegisOps.Application.Observability;
using AegisOps.Application.PolicyEngine;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Observability;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Infrastructure.GitHub;

/// <summary>
/// Worker steps of the GitHub deployment protection rule (docs/github-deployment-gate.md). Both run inside a durable job:
/// <list type="bullet">
/// <item><see cref="EvaluateAsync"/> maps the request to a project, environment and artifact and creates a normal
/// AegisOps deployment (the regular evaluation job takes over), or decides "rejected" with an explanation.</item>
/// <item><see cref="CallbackAsync"/> posts the decision to GitHub once, then finishes a released deployment as
/// Succeeded ("Released to GitHub").</item>
/// </list>
/// </summary>
public static class GitHubGateProcessor {
    /// <summary>Audit actor for everything the GitHub App does on AegisOps' side.</summary>
    public const string ActorType = "System";
    public const string ActorDisplay = "github-app";

    public static async Task EvaluateAsync(AegisOpsDbContext db, IAuditWriter audit, Guid gateId, TimeProvider time, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(time);
        using var activity = AegisOpsTracing.Source.StartActivity("GitHubGateEvaluate");
        var gate = await db.GitHubDeploymentGates.SingleOrDefaultAsync(item => item.Id == gateId, cancellationToken);
        if (gate is null || gate.Status != GitHubGateStatus.Received) return; // idempotent re-run

        var now = time.GetUtcNow();
        var shortSha = gate.CommitSha.Length > 7 ? gate.CommitSha[..7] : gate.CommitSha;
        // GitHub names are case-insensitive; one repository row per project keeps this list small.
        var repository = (await db.Repositories.AsNoTracking()
                .Where(item => item.Provider == RepositoryProvider.GitHub)
                .Select(item => new { item.ProjectId, item.FullName })
                .ToListAsync(cancellationToken))
            .Where(item => string.Equals(item.FullName, gate.RepositoryFullName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.ProjectId)
            .FirstOrDefault();
        if (repository is null) {
            Reject(db, audit, gate, null, now,
                $"AegisOps: repository {gate.RepositoryFullName} is not connected to an AegisOps project. Set it as the project's repository in AegisOps (PUT /api/v1/projects/{{slug}}/repository) and re-run the job.");
            return;
        }

        var project = await db.Projects.AsNoTracking().SingleAsync(item => item.Id == repository.ProjectId, cancellationToken);
        if (project.IsArchived) {
            Reject(db, audit, gate, project.Id, now, $"AegisOps: project {project.Slug} is archived; it accepts no deployments.");
            return;
        }

        var environment = (await db.Environments
                .Where(item => item.ProjectId == project.Id)
                .ToListAsync(cancellationToken))
            .Where(item => string.Equals(item.Name, gate.EnvironmentName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Order)
            .FirstOrDefault();
        if (environment is null) {
            Reject(db, audit, gate, project.Id, now,
                $"AegisOps: project {project.Slug} has no environment named {gate.EnvironmentName}. Create it in AegisOps (names match case-insensitively) with target type github.");
            return;
        }

        if (!environment.Target.IsGitHub) {
            Reject(db, audit, gate, project.Id, now,
                $"AegisOps: environment {environment.Name} of project {project.Slug} uses the {environment.Target.Type} target. Set its target type to github so AegisOps decides GitHub deployments for it.");
            return;
        }

        // Artifact SHAs are stored lowercase, like the gate's.
        var sha = gate.CommitSha;
        var artifact = (await db.Artifacts.AsNoTracking()
                .Where(item => item.ProjectId == project.Id && item.CommitSha == sha)
                .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
        if (artifact is null) {
            Reject(db, audit, gate, project.Id, now,
                $"AegisOps: no artifact registered for commit {shortSha} in project {project.Slug}. Register it from CI before the deploy job.");
            return;
        }

        var correlationId = Guid.CreateVersion7().ToString("N");
        var deployment = Deployment.RequestFromGitHub(project.Id, environment.Id, artifact.Id, now, correlationId,
            $"GitHub Actions run {gate.RunUrl}", environment.CurrentArtifactId);
        db.Deployments.Add(deployment);
        db.DeploymentEvents.Add(DeploymentEvent.Create(deployment.Id, 1, now, DeploymentEventType.Requested,
            $"Requested by the GitHub deployment protection rule for {gate.RepositoryFullName} ({gate.EnvironmentName}), run {gate.RunUrl}."));
        db.Jobs.Add(Job.Enqueue(JobType.EvaluateDeployment, JobTraceContext.DeploymentPayload(deployment.Id, correlationId), now, correlationId));
        gate.Link(project.Id, deployment.Id, correlationId);
        audit.Write(AuditEvent.Record(now, ActorType, null, ActorDisplay, "deployment.requested", "Deployment", deployment.Id, "Accepted", correlationId));
        activity?.SetTag("outcome", "linked");
    }

    private static void Reject(AegisOpsDbContext db, IAuditWriter audit, GitHubDeploymentGate gate, Guid? projectId, DateTimeOffset now, string comment) {
        gate.Decide(GitHubDeploymentGate.Rejected, comment, now, projectId);
        db.Jobs.Add(Job.Enqueue(JobType.GitHubGateCallback, AegisOpsDbContext.GitHubGatePayload(gate.Id, gate.CorrelationId), now, gate.CorrelationId));
        audit.Write(AuditEvent.Record(now, ActorType, null, ActorDisplay, "github.gate_unmapped", "GitHubDeploymentGate", gate.Id, "Rejected", gate.CorrelationId));
    }

    /// <summary>
    /// Posts the gate's decision once. GitHub's acknowledgement is committed on its own (lease-checked) before the
    /// deployment is finished, so a crash after that commit never posts a second review; a transient failure throws
    /// and the job retries with backoff (then dead-letters like any other job).
    /// </summary>
    public static async Task CallbackAsync(
        AegisOpsDbContext db,
        IAuditWriter audit,
        GitHubAppClient client,
        Guid gateId,
        TimeProvider time,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? leaseGuard = null
    ) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        using var activity = AegisOpsTracing.Source.StartActivity("GitHubGateCallback");
        var gate = await db.GitHubDeploymentGates.SingleOrDefaultAsync(item => item.Id == gateId, cancellationToken);
        if (gate?.Decision is null) return;
        var deployment = gate.DeploymentId is Guid id
            ? await db.Deployments.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            : null;

        if (!gate.IsSettled) {
            var comment = await BuildCommentAsync(db, gate, deployment, client.Settings.PublicBaseUrl, cancellationToken);
            var result = await client.ReviewAsync(gate, gate.Decision, comment, cancellationToken);
            var now = time.GetUtcNow();
            if (result.Accepted) gate.MarkReported(comment, result.StatusCode, now);
            else gate.MarkCallbackFailed(result.StatusCode, now);
            audit.Write(AuditEvent.Record(now, ActorType, null, ActorDisplay,
                result.Accepted ? "github.gate_reported" : "github.gate_report_refused", "GitHubDeploymentGate", gate.Id,
                result.Accepted ? gate.Decision == GitHubDeploymentGate.Approved ? "Approved" : "Rejected" : "Failed", gate.CorrelationId));
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken)) {
                if (leaseGuard is not null && !await leaseGuard(cancellationToken))
                    throw new OperationCanceledException("Job lease was lost.");
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            activity?.SetTag("github.status", result.StatusCode);
        }

        if (deployment is null || deployment.Status != DeploymentStatus.Deploying) return;
        var at = time.GetUtcNow();
        if (gate.Decision == GitHubDeploymentGate.Approved && gate.Status == GitHubGateStatus.Reported) {
            var environment = await db.Environments.SingleAsync(item => item.Id == deployment.EnvironmentId, cancellationToken);
            environment.RecordDeployment(deployment.ArtifactId, deployment.Id);
            deployment.Succeed(at);
            await AddEventAsync(db, deployment.Id, DeploymentEventType.Succeeded,
                $"Released to GitHub: workflow run {gate.RunUrl} may deploy to {gate.EnvironmentName}.", at, cancellationToken);
            audit.Write(AuditEvent.Record(at, ActorType, null, ActorDisplay, "deployment.succeeded", "Deployment", deployment.Id, "Succeeded", deployment.CorrelationId));
        } else if (gate.Status == GitHubGateStatus.CallbackFailed) {
            var reason = string.Create(CultureInfo.InvariantCulture,
                $"GitHub refused the decision (HTTP {gate.CallbackStatusCode}); the workflow run may have been cancelled or timed out.");
            deployment.Fail(reason, at);
            await AddEventAsync(db, deployment.Id, DeploymentEventType.Failed, reason, at, cancellationToken);
            audit.Write(AuditEvent.Record(at, ActorType, null, ActorDisplay, "deployment.failed", "Deployment", deployment.Id, "Failed", deployment.CorrelationId));
        }
    }

    /// <summary>The GitHub comment: the decision, the policies behind it and the failed rules, at most 1024 characters.</summary>
    internal static async Task<string> BuildCommentAsync(AegisOpsDbContext db, GitHubDeploymentGate gate, Deployment? deployment, Uri? publicBaseUrl,
        CancellationToken cancellationToken) {
        if (deployment is null) return gate.DecisionComment ?? "AegisOps rejected this deployment.";
        var text = new StringBuilder();
        var evaluation = await db.PolicyEvaluations.AsNoTracking()
            .Where(item => item.DeploymentId == deployment.Id)
            .ToListAsync(cancellationToken);
        var latest = evaluation.OrderByDescending(item => item.EvaluatedAt).FirstOrDefault();
        var rules = latest is null ? [] : ReadRules(latest.RuleResults);
        var policies = rules.Select(rule => string.Create(CultureInfo.InvariantCulture, $"{rule.PolicyName} v{rule.PolicyVersion}"))
            .Distinct(StringComparer.Ordinal).ToList();

        if (gate.Decision == GitHubDeploymentGate.Approved) {
            text.Append("AegisOps approved this deployment");
            if (deployment.ApprovalsReceived > 0)
                text.Append(CultureInfo.InvariantCulture, $" after {deployment.ApprovalsReceived} approval(s)");
            text.Append('.');
        } else {
            var verb = deployment.Status switch {
                DeploymentStatus.Denied => "denied this deployment by policy",
                DeploymentStatus.Rejected => "rejected this deployment (an approver declined it)",
                DeploymentStatus.Cancelled => "cancelled this deployment",
                DeploymentStatus.Failed => "could not release this deployment",
                _ => "rejected this deployment",
            };
            text.Append("AegisOps ").Append(verb).Append('.');
            if (!string.IsNullOrWhiteSpace(deployment.FailureReason)) text.Append(' ').Append(deployment.FailureReason.Trim());
        }

        text.Append(policies.Count == 0 ? " No policy applied." : " Policies: " + string.Join(", ", policies) + ".");
        var failed = rules.Where(rule => !rule.Passed).ToList();
        if (gate.Decision == GitHubDeploymentGate.Rejected && failed.Count > 0) {
            text.Append("\nFailed rules:");
            foreach (var rule in failed)
                text.Append(CultureInfo.InvariantCulture, $"\n- {rule.Type} [{rule.Effect}] ({rule.PolicyName} v{rule.PolicyVersion}): {rule.Message}");
        }

        text.Append(CultureInfo.InvariantCulture, $"\nAegisOps deployment {deployment.Id:D}");
        if (publicBaseUrl is not null) text.Append(" — ").Append(publicBaseUrl.AbsoluteUri);
        var result = text.ToString();
        if (result.Length <= GitHubDeploymentGate.MaxCommentLength) return result;
        // Keep the deployment reference at the end even when the rule list is long.
        var tail = string.Create(CultureInfo.InvariantCulture, $"…\nAegisOps deployment {deployment.Id:D}");
        return result[..(GitHubDeploymentGate.MaxCommentLength - tail.Length)] + tail;
    }

    private static readonly JsonSerializerOptions RuleJson = new() { PropertyNameCaseInsensitive = true };

    private static List<RuleResult> ReadRules(string json) {
        try {
            return JsonSerializer.Deserialize<List<RuleResult>>(json, RuleJson) ?? [];
        } catch (JsonException) {
            return [];
        }
    }

    private static async Task AddEventAsync(AegisOpsDbContext db, Guid deploymentId, DeploymentEventType type, string message, DateTimeOffset at,
        CancellationToken cancellationToken) {
        var stored = await db.DeploymentEvents.Where(item => item.DeploymentId == deploymentId)
            .MaxAsync(item => (int?)item.Sequence, cancellationToken) ?? 0;
        var pending = db.ChangeTracker.Entries<DeploymentEvent>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.DeploymentId == deploymentId)
            .Select(entry => entry.Entity.Sequence).DefaultIfEmpty(0).Max();
        db.DeploymentEvents.Add(DeploymentEvent.Create(deploymentId, Math.Max(stored, pending) + 1, at, type, message));
    }
}
