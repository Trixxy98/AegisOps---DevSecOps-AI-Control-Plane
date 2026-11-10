using AegisOps.Domain.Deploy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations;

public sealed class EnvironmentRuntimeSnapshotConfiguration : IEntityTypeConfiguration<EnvironmentRuntimeSnapshot> {
    public void Configure(EntityTypeBuilder<EnvironmentRuntimeSnapshot> builder) {
        builder.ToTable("environment_runtime_snapshots", "deploy");
        builder.HasKey(item => item.EnvironmentId);
        builder.Property(item => item.EnvironmentId).ValueGeneratedNever();
        builder.Property(item => item.ObservedAt).IsRequired();
        builder.Property(item => item.InspectionAvailable).IsRequired();
        builder.Property(item => item.Reason).HasMaxLength(500);
        builder.OwnsMany(item => item.Containers, container => {
            container.ToJson("containers");
        });
        builder.HasOne<AegisOps.Domain.Organization.Environment>().WithOne()
            .HasForeignKey<EnvironmentRuntimeSnapshot>(item => item.EnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
