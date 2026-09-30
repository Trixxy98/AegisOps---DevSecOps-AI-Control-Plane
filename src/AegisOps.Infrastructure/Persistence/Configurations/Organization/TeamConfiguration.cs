using AegisOps.Domain.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Organization;

public sealed class TeamConfiguration : IEntityTypeConfiguration<Team> {
    public void Configure(EntityTypeBuilder<Team> builder) {
        builder.ToTable("teams", "org");
        builder.HasKey(team => team.Id);
        builder.Property(team => team.Name).HasMaxLength(200).IsRequired();
        builder.Property(team => team.Slug).HasMaxLength(100).IsRequired();
        builder.HasIndex(team => team.Slug).IsUnique();
        builder.Property(team => team.Description).HasMaxLength(2000);
        builder.Property(team => team.CreatedAt).IsRequired();
    }
}