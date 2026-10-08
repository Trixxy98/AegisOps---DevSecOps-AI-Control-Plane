using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Security;

public sealed class ArtifactConfiguration : IEntityTypeConfiguration<Artifact> {
    public void Configure(EntityTypeBuilder<Artifact> builder) {
        builder.ToTable("artifacts", "security", table => {
            table.HasCheckConstraint(
                "ck_artifacts_build_status",
                "build_status IN ('Unknown', 'Passed', 'Failed', 'Skipped')"
            );
            table.HasCheckConstraint(
                "ck_artifacts_test_status",
                "test_status IN ('Unknown', 'Passed', 'Failed', 'Skipped')"
            );
        });
        builder.HasKey(artifact => artifact.Id);
        builder.Property(artifact => artifact.Version).HasMaxLength(100).IsRequired();
        builder.Property(artifact => artifact.CommitSha).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.Branch).HasMaxLength(200).IsRequired();
        builder.Property(artifact => artifact.ImageReference).HasMaxLength(500).IsRequired();
        builder.Property(artifact => artifact.ImageDigest).HasMaxLength(71);
        builder.Property(artifact => artifact.CiProvider).HasMaxLength(100);
        builder.Property(artifact => artifact.CiRunId).HasMaxLength(100);
        builder.Property(artifact => artifact.CiRunUrl).HasMaxLength(500);
        builder.Property(artifact => artifact.BuildStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(artifact => artifact.TestStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.OwnsOne(artifact => artifact.TestSummary, summary => {
            summary.ToJson("test_summary");
        });
        builder.Property(artifact => artifact.CreatedAt).IsRequired();
        builder.HasIndex(artifact => new { artifact.ProjectId, artifact.Version }).IsUnique();
        builder.HasIndex(artifact => new { artifact.ProjectId, artifact.CreatedAt });
        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(artifact => artifact.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(artifact => artifact.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApiKey>()
            .WithMany()
            .HasForeignKey(artifact => artifact.CreatedByApiKeyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}