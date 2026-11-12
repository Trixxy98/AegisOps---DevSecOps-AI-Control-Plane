using AegisOps.Domain.Audit;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AegisOps.Api.Tests;

/// <summary>
/// Roadmap v1.1 item 6 (docs/17 §2): the restricted application role may only SELECT and INSERT on <c>audit.*</c> and has
/// normal read/write access elsewhere. Grants cannot be exercised on SQLite, so the main test needs
/// AEGISOPS_TEST_POSTGRES (an administrative connection string to a disposable server); it creates and drops its own
/// database and a uniquely named role.
/// </summary>
public sealed class AppDatabaseRoleTests {
    private const string InsufficientPrivilege = "42501";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RejectsInvalidRoleNamesAndShortPasswordsBeforeTouchingTheDatabase() {
        var options = new DbContextOptionsBuilder<AegisOpsDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused").UseSnakeCaseNamingConvention().EnableServiceProviderCaching(false).Options;
        await using var db = new AegisOpsDbContext(options);
        var password = new string('p', 24);
        foreach (var name in new[] { "", "Upper", "1starts_with_digit", "has-dash", "quote\"d", new string('a', 64) }) {
            Assert.False(AppDatabaseRole.IsValidRoleName(name));
            await Assert.ThrowsAsync<ArgumentException>(() => AppDatabaseRole.EnsureAsync(db, name, password, Token));
        }

        Assert.True(AppDatabaseRole.IsValidRoleName(AppDatabaseRole.DefaultName));
        await Assert.ThrowsAsync<ArgumentException>(() => AppDatabaseRole.EnsureAsync(db, "aegisops_app", "short", Token));
    }

    [Fact]
    public async Task AppRoleCanOnlyInsertAndSelectAuditButWriteEverythingElse() {
        var connection = System.Environment.GetEnvironmentVariable("AEGISOPS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) Assert.Skip("Set AEGISOPS_TEST_POSTGRES to run the PostgreSQL role test.");
        var suffix = Guid.NewGuid().ToString("N");
        var databaseName = "aegisops_roles_" + suffix;
        var role = "aegisops_app_test_" + suffix[..12];
        // Built at runtime: no credential-looking literal in the repository.
        var password = "pw-" + Guid.NewGuid().ToString("N");
        var admin = new NpgsqlConnectionStringBuilder(connection);
        await ExecuteAsync(admin.ConnectionString, $"CREATE DATABASE {databaseName}");
        var owner = new NpgsqlConnectionStringBuilder(connection) { Database = databaseName };
        var app = new NpgsqlConnectionStringBuilder(connection) {
            Database = databaseName, Username = role, Password = password, Pooling = false,
        };
        DbContextOptions<AegisOpsDbContext> Options(string connectionString) => new DbContextOptionsBuilder<AegisOpsDbContext>()
            .UseNpgsql(connectionString).UseSnakeCaseNamingConvention().EnableServiceProviderCaching(false).Options;

        try {
            await using (var db = new AegisOpsDbContext(Options(owner.ConnectionString))) {
                await db.Database.MigrateAsync(Token);
                await AppDatabaseRole.EnsureAsync(db, role, "first-" + password, Token);
                // Idempotent, and a second run replaces the password.
                await AppDatabaseRole.EnsureAsync(db, role, password, Token);
                // Tables created after provisioning (by a later migration) inherit the grants.
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE org.later_table (id int PRIMARY KEY); CREATE TABLE audit.later_audit (id int PRIMARY KEY)", Token);
            }

            await using (var db = new AegisOpsDbContext(Options(app.ConnectionString))) {
                var now = DateTimeOffset.UtcNow;
                var team = Team.Create("Roles", "roles-" + suffix[..8], now);
                db.Teams.Add(team);
                db.AuditEvents.Add(AuditEvent.Record(now, "System", null, "role-test", "role.test", "Team", team.Id, "Success", null));
                await db.SaveChangesAsync(Token);

                Assert.Equal(1, await db.AuditEvents.CountAsync(Token));
                team.Rename("Renamed");
                await db.SaveChangesAsync(Token);
                Assert.Empty(await db.Database.GetPendingMigrationsAsync(Token)); // readiness check reads the history

                await AssertDeniedAsync(db, "UPDATE audit.audit_events SET action = 'tampered'");
                await AssertDeniedAsync(db, "DELETE FROM audit.audit_events");
                await AssertDeniedAsync(db, "TRUNCATE audit.audit_events");
                var historySchema = await db.Database.SqlQueryRaw<string>(
                    "SELECT schemaname AS \"Value\" FROM pg_tables WHERE tablename = '__EFMigrationsHistory'").SingleAsync(Token);
                await AssertDeniedAsync(db, $"DELETE FROM \"{historySchema}\".\"__EFMigrationsHistory\"");
                await AssertDeniedAsync(db, "CREATE TABLE org.not_allowed (id int)");

                await db.Database.ExecuteSqlRawAsync("INSERT INTO org.later_table VALUES (1); UPDATE org.later_table SET id = 2", Token);
                await db.Database.ExecuteSqlRawAsync("INSERT INTO audit.later_audit VALUES (1)", Token);
                await AssertDeniedAsync(db, "UPDATE audit.later_audit SET id = 2");
                await AssertDeniedAsync(db, "DELETE FROM audit.later_audit");

                db.Teams.Remove(team);
                await db.SaveChangesAsync(Token);
            }

            await using (var db = new AegisOpsDbContext(Options(owner.ConnectionString))) {
                // The owner (migrate) keeps full rights.
                Assert.Equal(1, await db.Database.ExecuteSqlRawAsync("UPDATE audit.audit_events SET outcome = outcome", Token));
            }
        } finally {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(admin.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE)");
            await ExecuteAsync(admin.ConnectionString, $"DROP ROLE IF EXISTS {role}");
        }
    }

    private static async Task AssertDeniedAsync(AegisOpsDbContext db, string sql) {
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, Token));
        Assert.Equal(InsufficientPrivilege, error.SqlState);
    }

    private static async Task ExecuteAsync(string connectionString, string sql) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Token);
    }
}
