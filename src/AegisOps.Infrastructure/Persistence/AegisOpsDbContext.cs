using AegisOps.Domain.Ai;
using AegisOps.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using System.Text.Json;

namespace AegisOps.Infrastructure.Persistence;

public sealed class AegisOpsDbContext : IdentityDbContext<User, IdentityRole<Guid>, Guid> {
    public AegisOpsDbContext(DbContextOptions<AegisOpsDbContext> options) : base(options) {
    }
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<TeamWebhookSettings> TeamWebhookSettings => Set<TeamWebhookSettings>();
    public DbSet<TeamWebhookDeliveryStats> TeamWebhookDeliveryStats => Set<TeamWebhookDeliveryStats>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<AegisOps.Domain.Organization.Environment> Environments => Set<AegisOps.Domain.Organization.Environment>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<EnvironmentVariable> EnvironmentVariables => Set<EnvironmentVariable>();

    protected override void OnModelCreating(ModelBuilder builder) {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AegisOpsDbContext).Assembly);
        builder.HasDefaultSchema("identity");

        builder.Entity<RefreshToken>(token => {
            token.ToTable("refresh_tokens");
            token.HasIndex(item => item.TokenHash).IsUnique();
            token.HasOne<User>().WithMany().HasForeignKey(item => item.UserId);
        });
    }
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<SecurityScan> SecurityScans => Set<SecurityScan>();
    public DbSet<SecurityFinding> SecurityFindings => Set<SecurityFinding>();
    public DbSet<FindingSuppression> FindingSuppressions => Set<FindingSuppression>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<PolicyRule> PolicyRules => Set<PolicyRule>();
    public DbSet<PolicyEvaluation> PolicyEvaluations => Set<PolicyEvaluation>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<DeploymentEvent> DeploymentEvents => Set<DeploymentEvent>();
    public DbSet<EnvironmentRuntimeSnapshot> EnvironmentRuntimeSnapshots => Set<EnvironmentRuntimeSnapshot>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<GitHubDeploymentGate> GitHubDeploymentGates => Set<GitHubDeploymentGate>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<WebhookDeliveryAttempt> WebhookDeliveryAttempts => Set<WebhookDeliveryAttempt>();
    public DbSet<AiModel> AiModels => Set<AiModel>();
    public DbSet<AiPolicy> AiPolicies => Set<AiPolicy>();
    public DbSet<AiRequest> AiRequests => Set<AiRequest>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    // The notification outbox is written in the same transaction as each timeline event.
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => SaveChangesAsync(true, cancellationToken);

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) {
        foreach (var entry in ChangeTracker.Entries<DeploymentEvent>().Where(e => e.State == EntityState.Added).ToArray()) {
            var id = entry.Entity.Id;
            if (!ChangeTracker.Entries<Job>().Any(e => e.State == EntityState.Added && e.Entity.Type == JobType.SendNotification && e.Entity.Payload.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase))) {
                Jobs.Add(Job.Enqueue(JobType.SendNotification, JsonSerializer.Serialize(new { eventId = id }), entry.Entity.Timestamp, Guid.CreateVersion7().ToString("N")));
            }
        }
        await QueueGitHubRejectionsAsync(cancellationToken);
        var pending = AegisOps.Infrastructure.Observability.DomainMetrics.Capture(ChangeTracker);
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        // Inside an explicit transaction (job completion, scan replace) this runs before the final
        // commit; a rollback after a successful SaveChanges is rare and would only over-count.
        if (!pending.IsEmpty)
            await AegisOps.Infrastructure.Observability.DomainMetrics.RecordCommittedAsync(this, pending, cancellationToken);
        return saved;
    }

    private static readonly DeploymentStatus[] GitHubRejectingStatuses = [
        DeploymentStatus.Denied, DeploymentStatus.Rejected, DeploymentStatus.Cancelled, DeploymentStatus.Failed,
    ];

    /// <summary>
    /// GitHub deployment gate outbox (docs/github-deployment-gate.md): whatever path ends a gated deployment without a
    /// release (policy Deny in the Worker, a rejection or cancellation in the API, a failure), the gate's "rejected"
    /// decision and its callback job are written in the same transaction as the transition. The approved answer is
    /// queued explicitly by the Worker's execution step.
    /// </summary>
    private async Task QueueGitHubRejectionsAsync(CancellationToken cancellationToken) {
        var ended = ChangeTracker.Entries<Deployment>()
            .Where(entry => entry.State == EntityState.Modified && entry.Property(item => item.Status).IsModified &&
                GitHubRejectingStatuses.Contains(entry.Entity.Status) &&
                entry.Property(item => item.Status).OriginalValue != entry.Entity.Status)
            .Select(entry => entry.Entity)
            .ToList();
        if (ended.Count == 0) return;
        var ids = ended.Select(item => item.Id).ToList();
        var gates = await GitHubDeploymentGates.Where(gate => gate.DeploymentId != null && ids.Contains(gate.DeploymentId.Value))
            .ToListAsync(cancellationToken);
        foreach (var gate in gates) {
            var deployment = ended.Single(item => item.Id == gate.DeploymentId);
            var now = deployment.CompletedAt ?? deployment.RequestedAt;
            if (!gate.Decide(GitHubDeploymentGate.Rejected, null, now)) continue;
            Jobs.Add(Job.Enqueue(JobType.GitHubGateCallback, GitHubGatePayload(gate.Id, deployment.CorrelationId), now, deployment.CorrelationId));
        }
    }

    /// <summary>Gate jobs carry only the gate id and the correlation key, never webhook content.</summary>
    public static string GitHubGatePayload(Guid gateId, string correlationId) =>
        JsonSerializer.Serialize(new { gateId, correlationId, traceparent = System.Diagnostics.Activity.Current?.Id });
}
