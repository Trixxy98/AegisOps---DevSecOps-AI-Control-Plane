using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Policies;

public sealed class PolicyConfiguration : IEntityTypeConfiguration<Policy> {
    public void Configure(EntityTypeBuilder<Policy> builder) {
        builder.ToTable("policies", "policy", table =>
            table.HasCheckConstraint(
                "ck_policies_scope",
                "scope IN ('Global', 'Team', 'Project')"
            )
        );
        builder.HasKey(policy => policy.Id);
        builder.Property(policy => policy.Name).HasMaxLength(200).IsRequired();
        builder.Property(policy => policy.Description).HasMaxLength(2000);
        builder.Property(policy => policy.Scope)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(policy => policy.AppliesToTiers)
            .HasConversion(
                tiers => tiers.Select(tier => tier.ToString()).ToArray(),
                tiers => tiers.Select(tier => Enum.Parse<EnvironmentTier>(tier)).ToArray()
            )
            .HasColumnType("text[]")
            .IsRequired()
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<EnvironmentTier>>(
                (left, right) => left!.SequenceEqual(right!),
                tiers => tiers.Aggregate(0, (hash, tier) => HashCode.Combine(hash, tier.GetHashCode())),
                tiers => tiers.ToArray()
            ));
        builder.Property(policy => policy.IsEnabled).IsRequired();
        builder.Property(policy => policy.Version).IsRequired();
        builder.Property(policy => policy.CreatedAt).IsRequired();
        builder.Property(policy => policy.UpdatedAt).IsRequired();
        builder.HasIndex(policy => new { policy.Scope, policy.ScopeId })
            .HasFilter("archived_at IS NULL");
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(policy => policy.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(policy => policy.UpdatedById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}