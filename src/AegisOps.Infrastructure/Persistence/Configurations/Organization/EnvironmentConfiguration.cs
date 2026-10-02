using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Organization;

public sealed class EnvironmentConfiguration : IEntityTypeConfiguration<AegisOps.Domain.Organization.Environment> {
    public void Configure(EntityTypeBuilder<AegisOps.Domain.Organization.Environment> builder) {
        builder.ToTable("environments", "org", table =>
            table.HasCheckConstraint(
                "ck_environments_tier",
                "tier IN ('Development', 'Staging', 'Production')"
            ));

            builder.HasKey(environment => environment.Id);
        builder.Property(environment => environment.Name).HasMaxLength(100).IsRequired();
        builder.Property(environment => environment.Tier)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(environment => environment.Order)
            .HasColumnName("order")
            .IsRequired();
        builder.OwnsOne(environment => environment.Target, target => {
            target.ToJson("target");
            target.Property(item => item.Type).IsRequired();
            target.Property(item => item.AllowApiKeyProduction).IsRequired();
        });
        builder.HasOne<AegisOps.Domain.Organization.Project>()
            .WithMany()
            .HasForeignKey(environment => environment.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(environment => new { environment.ProjectId, environment.Name }).IsUnique();

    }
}
