using AegisOps.Domain.Ai;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Ai;

public sealed class AiModelConfiguration : IEntityTypeConfiguration<AiModel> {
    public void Configure(EntityTypeBuilder<AiModel> builder) {
        builder.ToTable("ai_models", "ai"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(150).IsRequired(); builder.HasIndex(x => x.Name).IsUnique();
        builder.Property(x => x.Provider).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.DisplayName).HasMaxLength(150);
        builder.Property(x => x.CapabilitiesJson).HasColumnType("jsonb").IsRequired();
    }
}
public sealed class AiPolicyConfiguration : IEntityTypeConfiguration<AiPolicy> {
    public void Configure(EntityTypeBuilder<AiPolicy> builder) {
        builder.ToTable("ai_policies", "ai"); builder.HasKey(x => x.Id);
        builder.Ignore(x => x.AllowedModelIds); builder.Ignore(x => x.BlockedPatterns);
        builder.Property(x => x.Name).HasMaxLength(150).IsRequired();
        builder.Property(x => x.Scope).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.SensitiveDataAction).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.AllowedModelIdsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.BlockedPatternsJson).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(x => x.ScopeId).IsUnique().HasFilter("scope = 'Team' AND is_enabled = true");
        builder.HasIndex(x => x.Scope).IsUnique().HasFilter("scope = 'Global' AND is_enabled = true");
        builder.HasOne<Team>().WithMany().HasForeignKey(x => x.ScopeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedById).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class AiRequestConfiguration : IEntityTypeConfiguration<AiRequest> {
    public void Configure(EntityTypeBuilder<AiRequest> builder) {
        builder.ToTable("ai_requests", "ai"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.BlockReason).HasMaxLength(64);
        builder.Property(x => x.PromptHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.PromptPreview).HasMaxLength(500); builder.Property(x => x.ResponsePreview).HasMaxLength(500);
        builder.Property(x => x.DetectedSensitiveTypesJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.UserId, x.CreatedAt }); builder.HasIndex(x => x.CreatedAt);
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AiPolicy>().WithMany().HasForeignKey(x => x.AiPolicyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AiModel>().WithMany().HasForeignKey(x => x.AiModelId).OnDelete(DeleteBehavior.Restrict);
    }
}
