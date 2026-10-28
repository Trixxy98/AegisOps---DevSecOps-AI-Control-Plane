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
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<AegisOps.Domain.Organization.Environment> Environments => Set<AegisOps.Domain.Organization.Environment>();
    public DbSet<Repository> Repositories => Set<Repository>();

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
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    // The notification outbox is written in the same transaction as each timeline event.
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => SaveChangesAsync(true, cancellationToken);

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) {
        foreach (var entry in ChangeTracker.Entries<DeploymentEvent>().Where(e => e.State == EntityState.Added).ToArray()) {
            var id = entry.Entity.Id;
            if (!ChangeTracker.Entries<Job>().Any(e => e.State == EntityState.Added && e.Entity.Type == JobType.SendNotification && e.Entity.Payload.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase))) {
                Jobs.Add(Job.Enqueue(JobType.SendNotification, JsonSerializer.Serialize(new { eventId = id }), entry.Entity.Timestamp, Guid.CreateVersion7().ToString("N")));
            }
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
