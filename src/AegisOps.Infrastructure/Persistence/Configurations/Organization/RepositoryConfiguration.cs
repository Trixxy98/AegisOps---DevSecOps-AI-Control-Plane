using AegisOps.Domain.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Organization;

public sealed class RepositoryConfiguration : IEntityTypeConfiguration<Repository> {
    public void Configure(EntityTypeBuilder<Repository> builder) {
        builder.ToTable("repositories", "org", table => 
            table.HasCheckConstraint(
                "ck_repositories_provider",
                "provider IN ('GitHub')"
            ));
            builder.HasKey(repository => repository.Id);
            builder.Property(repository => repository.Provider)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            builder.Property(repository => repository.FullName).HasMaxLength(300).IsRequired();
            builder.Property(repository => repository.DefaultBranch).HasMaxLength(200).IsRequired();
            builder.Property(repository => repository.HtmlUrl).HasMaxLength(500).IsRequired();
            builder.HasOne<Project>()
                .WithMany()
                .HasForeignKey(repository => repository.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(repository => repository.ProjectId).IsUnique();
    }
}