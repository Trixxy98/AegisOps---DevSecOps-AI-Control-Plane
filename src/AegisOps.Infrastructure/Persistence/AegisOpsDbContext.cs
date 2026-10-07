using AegisOps.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using AegisOps.Domain.Organization;

namespace AegisOps.Infrastructure.Persistence;

public sealed class AegisOpsDbContext : IdentityDbContext<User, IdentityRole<Guid>, Guid> {
    public AegisOpsDbContext(DbContextOptions<AegisOpsDbContext> options) : base(options) {
    }
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<AegisOps.Domain.Organization.Environment> Environments => Set<AegisOps.Domain.Organization.Environment>();
    public DbSet<Repository> Repositories => Set<Repository>();

    protected override void OnModelCreating(ModelBuilder builder) {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AegisOpsDbContext).Assembly);
        builder.HasDefaultSchema("identity");

        builder.Entity<RefreshToken>(token => {
            token.ToTable("refresh_tokens");
            token.HasIndex(item => item.TokenHash).IsUnique();
            token.HasOne<User>().WithMany().HasForeignKey(item => item.UserId);
        });
    }
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

}
