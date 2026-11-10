using AegisOps.Domain.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Organization;

public sealed class TeamWebhookDeliveryStatsConfiguration : IEntityTypeConfiguration<TeamWebhookDeliveryStats> {
    public void Configure(EntityTypeBuilder<TeamWebhookDeliveryStats> builder) {
        builder.ToTable("team_webhook_delivery_stats", "org");
        builder.HasKey(stats => stats.TeamId);
        builder.Property(stats => stats.UpdatedAt).IsRequired();
        // Counters belong to the team, not to jobs: purging jobs must never cascade here.
        builder.HasOne<Team>().WithMany().HasForeignKey(stats => stats.TeamId).OnDelete(DeleteBehavior.Cascade);
    }
}
