using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Organization;

public sealed class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember> {
    public void Configure(EntityTypeBuilder<TeamMember> builder) {
        builder.ToTable("team_members", "org", table => table.HasCheckConstraint(
            "ck_team_members_role",
            "role IN ('Owner', 'Member')"
        ));

        builder.HasKey(member => new {member.TeamId, member.UserId});
        builder.Property(member => member.Role)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(member => member.JoinedAt).IsRequired();
        builder.HasOne<Team>()
            .WithMany()
            .HasForeignKey(member => member.TeamId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(member => member.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}