using AegisOps.Domain.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Organization;

public sealed class EnvironmentVariableConfiguration : IEntityTypeConfiguration<EnvironmentVariable> {
    public void Configure(EntityTypeBuilder<EnvironmentVariable> builder) {
        builder.ToTable("environment_variables", "org");
        builder.HasKey(variable => variable.Id);
        builder.Property(variable => variable.Name).HasMaxLength(EnvironmentVariable.MaxNameLength).IsRequired();
        builder.Property(variable => variable.Value).HasMaxLength(EnvironmentVariable.MaxValueLength);
        builder.Property(variable => variable.ProtectedValue).HasMaxLength(EnvironmentVariable.MaxProtectedValueLength);
        builder.Property(variable => variable.IsSecret).IsRequired();
        builder.Property(variable => variable.UpdatedAt).IsRequired();
        builder.Property(variable => variable.UpdatedById).IsRequired();
        builder.HasOne<AegisOps.Domain.Organization.Environment>()
            .WithMany()
            .HasForeignKey(variable => variable.EnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(variable => new { variable.EnvironmentId, variable.Name }).IsUnique();
    }
}
