using AegisOps.Domain.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Organization;

public sealed class TeamWebhookSettingsConfiguration : IEntityTypeConfiguration<TeamWebhookSettings> {
    public void Configure(EntityTypeBuilder<TeamWebhookSettings> builder) {
        builder.ToTable("team_webhook_settings", "org");
        builder.HasKey(settings => settings.TeamId);
        builder.Property(settings => settings.Url).HasMaxLength(2048).IsRequired();
        builder.Property(settings => settings.EncryptedSecret).HasMaxLength(4096).IsRequired();
        builder.Property(settings => settings.UpdatedAt).IsRequired();
        builder.HasOne<Team>().WithMany().HasForeignKey(settings => settings.TeamId).OnDelete(DeleteBehavior.Cascade);
    }
}
