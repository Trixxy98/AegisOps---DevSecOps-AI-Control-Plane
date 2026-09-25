using AegisOps.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Infrastructure.Persistence;

public sealed class AegisOpsDbContext : IdentityDbContext<User, IdentityRole<Guid>, Guid> {
    public AegisOpsDbContext(DbContextOptions<AegisOpsDbContext> options) : base(options) {
    }
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder) {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("identity");

        builder.Entity<RefreshToken>(token => {
            token.ToTable("refresh_tokens");
            token.HasIndex(item => item.TokenHash).IsUnique();
            token.HasOne<User>().WithMany().HasForeignKey(item => item.UserId);
        });
    }

}
