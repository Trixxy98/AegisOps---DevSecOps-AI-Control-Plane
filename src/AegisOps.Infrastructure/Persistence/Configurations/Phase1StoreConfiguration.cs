using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations;

public sealed class PolicyEvaluationConfiguration : IEntityTypeConfiguration<PolicyEvaluation> {
    public void Configure(EntityTypeBuilder<PolicyEvaluation> builder) {
        builder.ToTable("policy_evaluations", "policy");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Decision).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(item => item.RuleResults).HasColumnType("jsonb").IsRequired();
        builder.Property(item => item.EvaluatedAt).IsRequired();
        builder.HasIndex(item => item.DeploymentId);
    }
}

public sealed class DeploymentConfiguration : IEntityTypeConfiguration<Deployment> {
    public void Configure(EntityTypeBuilder<Deployment> builder) {
        builder.ToTable("deployments", "deploy");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(item => item.Reason).HasMaxLength(2000);
        builder.Property(item => item.FailureReason).HasMaxLength(2000);
        builder.Property(item => item.CorrelationId).HasMaxLength(64).IsRequired();
        builder.HasIndex(item => new { item.ProjectId, item.RequestedAt });
        builder.HasIndex(item => new { item.EnvironmentId, item.Status });
        builder.HasOne<Project>().WithMany().HasForeignKey(item => item.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AegisOps.Domain.Organization.Environment>().WithMany().HasForeignKey(item => item.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Artifact>().WithMany().HasForeignKey(item => item.ArtifactId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DeploymentEventConfiguration : IEntityTypeConfiguration<DeploymentEvent> {
    public void Configure(EntityTypeBuilder<DeploymentEvent> builder) {
        builder.ToTable("deployment_events", "deploy");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(item => item.Message).HasMaxLength(2000).IsRequired();
        builder.HasIndex(item => new { item.DeploymentId, item.Sequence }).IsUnique();
        builder.HasOne<Deployment>().WithMany().HasForeignKey(item => item.DeploymentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ApprovalConfiguration : IEntityTypeConfiguration<Approval> {
    public void Configure(EntityTypeBuilder<Approval> builder) {
        builder.ToTable("approvals", "deploy");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Decision).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(item => item.Comment).HasMaxLength(2000);
        builder.HasIndex(item => new { item.DeploymentId, item.ApproverId }).IsUnique();
        builder.HasOne<Deployment>().WithMany().HasForeignKey(item => item.DeploymentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class JobConfiguration : IEntityTypeConfiguration<Job> {
    public void Configure(EntityTypeBuilder<Job> builder) {
        builder.ToTable("jobs", "jobs");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Type).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(item => item.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(item => item.LastError).HasMaxLength(2000);
        builder.Property(item => item.LockedBy).HasMaxLength(100);
        builder.Property(item => item.CorrelationId).HasMaxLength(64).IsRequired();
        builder.HasIndex(item => new { item.Status, item.ScheduledAt });
    }
}

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent> {
    public void Configure(EntityTypeBuilder<AuditEvent> builder) {
        builder.ToTable("audit_events", "audit");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.ActorType).HasMaxLength(32).IsRequired();
        builder.Property(item => item.ActorDisplay).HasMaxLength(200).IsRequired();
        builder.Property(item => item.Action).HasMaxLength(100).IsRequired();
        builder.Property(item => item.ResourceType).HasMaxLength(100).IsRequired();
        builder.Property(item => item.Outcome).HasMaxLength(32).IsRequired();
        builder.Property(item => item.CorrelationId).HasMaxLength(64);
        builder.HasIndex(item => item.Timestamp);
    }
}
