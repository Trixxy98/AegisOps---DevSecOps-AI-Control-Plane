using AegisOps.Domain.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Organization;

public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project> {
    public void Configure(EntityTypeBuilder<Project> builder) {
        builder.ToTable("projects", "org");
        builder.HasKey(project => project.Id);
        builder.Property(project => project.Name).HasMaxLength(200).IsRequired();
        builder.Property(project => project.Slug).HasMaxLength(100).IsRequired();
        builder.HasIndex(project => project.Slug).IsUnique();
        builder.Property(project => project.Description).HasMaxLength(2000);
        builder.Property(project => project.IsArchived).IsRequired();
        builder.Property(project => project.CreatedAt).IsRequired();
        builder.HasOne<Team>()
            .WithMany()
            .HasForeignKey(project => project.TeamId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(project => project.TeamId);
    }
}