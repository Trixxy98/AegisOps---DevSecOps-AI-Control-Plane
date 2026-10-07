using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Identity;

public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey> {
    public void Configure(EntityTypeBuilder<ApiKey> builder) {
        builder.ToTable("api_keys", "identity");
        builder.HasKey(apiKey => apiKey.Id);
        builder.Property(apiKey => apiKey.Name).HasMaxLength(200).IsRequired();
        builder.Property(apiKey => apiKey.KeyPrefix).HasMaxLength(8).IsRequired();
        builder.HasIndex(apiKey => apiKey.KeyPrefix).IsUnique();
        builder.Property(apiKey => apiKey.KeyHash).HasMaxLength(64).IsRequired();
        builder.Property(apiKey => apiKey.Scopes)
            .HasConversion(
                scopes => scopes.ToArray(),
                scopes => scopes
            )
            .HasColumnType("text[]")
            .IsRequired();
        builder.Property(apiKey => apiKey.ExpiresAt).IsRequired();
        builder.Property(apiKey => apiKey.CreatedAt).IsRequired();
        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(apiKey => apiKey.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(apiKey => apiKey.ProjectId);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(apiKey => apiKey.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}