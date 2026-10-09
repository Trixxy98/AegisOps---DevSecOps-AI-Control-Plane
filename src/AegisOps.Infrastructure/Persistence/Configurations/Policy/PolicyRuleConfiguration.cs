using AegisOps.Domain.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Policies;

public sealed class PolicyRuleConfiguration : IEntityTypeConfiguration<PolicyRule> {
    public void Configure(EntityTypeBuilder<PolicyRule> builder) {
        builder.ToTable("policy_rules", "policy", table => {
            table.HasCheckConstraint(
                "ck_policy_rules_type",
                "type IN ('RequireTestsPassed', 'RequireScan', 'MaxFindings', 'RequireApprovals', 'AllowedBranches', 'DeploymentWindow', 'RequireImageDigest', 'RequirePriorEnvironment')"
            );
            table.HasCheckConstraint(
                "ck_policy_rules_effect",
                "effect IN ('Deny', 'RequireApproval', 'Warn')"
            );
        });
        builder.HasKey(rule => rule.Id);
        builder.Property(rule => rule.Type)
            .HasConversion<string>()
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(rule => rule.Effect)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(rule => rule.Parameters)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(rule => rule.Order)
            .HasColumnName("order")
            .IsRequired();
        builder.Property(rule => rule.IsEnabled).IsRequired();
        builder.HasOne<Policy>()
            .WithMany()
            .HasForeignKey(rule => rule.PolicyId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(rule => new { rule.PolicyId, rule.Order });
    }
}