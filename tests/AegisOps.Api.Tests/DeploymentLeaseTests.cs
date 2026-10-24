using System.Text.Json;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrgEnvironment = AegisOps.Domain.Organization.Environment;

namespace AegisOps.Api.Tests;

// Set AEGISOPS_TEST_POSTGRES to an administrative PostgreSQL connection string to
// run against an isolated, disposable database. No Docker daemon is required.
public sealed class DeploymentLeaseTests {
    [Fact]
    public async Task Parallel_claims_renewal_stale_recovery_and_target_serialization() {
        var connection = System.Environment.GetEnvironmentVariable("AEGISOPS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var ct = TestContext.Current.CancellationToken;
        var name = "aegisops_lease_" + Guid.NewGuid().ToString("N");
        var admin = new NpgsqlConnectionStringBuilder(connection);
        await using (var db = new NpgsqlConnection(admin.ConnectionString)) {
            await db.OpenAsync(ct);
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", db);
            await command.ExecuteNonQueryAsync(ct);
        }
        var target = new NpgsqlConnectionStringBuilder(connection) { Database = name };
        var options = new DbContextOptionsBuilder<AegisOpsDbContext>()
            .UseNpgsql(target.ConnectionString).UseSnakeCaseNamingConvention().Options;
        AegisOpsDbContext Open() => new(options);
        try {
            await using (var db = Open()) {
                await db.Database.MigrateAsync(ct);
                var now = DateTimeOffset.UtcNow;
                var team = Team.Create("Lease test", "lease-test", now);
                var project = Project.Create(team.Id, "Lease", "lease", now);
                db.Teams.Add(team);
                db.Projects.Add(project);
                // The claim path only needs these FK rows; no artifact is executed.
                await db.SaveChangesAsync(ct);
                var artifactId = Guid.NewGuid();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO security.artifacts (id, project_id, version, commit_sha, branch, image_reference,
                        build_status, test_status, created_at)
                    VALUES ({artifactId}, {project.Id}, 'v1', 'abcdef1234567890', 'main', 'example:latest',
                        'Passed', 'Passed', {now})
                    """, ct);
                var a = OrgEnvironment.Create(project.Id, "a", EnvironmentTier.Development, 0,
                    DeploymentTarget.Docker("shared", null, 9000, 8080, "/health", 60));
                var b = OrgEnvironment.Create(project.Id, "b", EnvironmentTier.Staging, 1,
                    DeploymentTarget.Docker("other", null, 9000, 8080, "/health", 60));
                var c = OrgEnvironment.Create(project.Id, "c", EnvironmentTier.Production, 2,
                    DeploymentTarget.Docker("independent", null, 9001, 8080, "/health", 60));
                var d = OrgEnvironment.Create(project.Id, "d", EnvironmentTier.Production, 3,
                    DeploymentTarget.Docker("shared", null, 9002, 8080, "/health", 60));
                db.Environments.AddRange(a, b, c, d);
                foreach (var env in new[] { a, b, c, d }) {
                    var deployment = Deployment.Request(project.Id, env.Id, artifactId, now, Guid.NewGuid().ToString("N"),
                        null, Guid.NewGuid(), null);
                    db.Deployments.Add(deployment);
                    db.Jobs.Add(Job.Enqueue(JobType.ExecuteDeployment,
                        JsonSerializer.Serialize(new { deploymentId = deployment.Id }), now, deployment.CorrelationId));
                }
                await db.SaveChangesAsync(ct);
            }

            async Task<Job?> Claim(string worker, TimeProvider? time = null) {
                await using var db = Open();
                return await DeploymentProcessor.TryClaimOneAsync(db, time ?? TimeProvider.System, worker, ct);
            }
            var parallel = await Task.WhenAll(Claim("one"), Claim("two"), Claim("three"));
            Assert.Equal(2, parallel.Count(j => j is not null)); // same name or host port is serialized
            Assert.Equal(2, parallel.Where(j => j is not null).Select(j => j!.Id).Distinct().Count());
            Assert.Null(await Claim("four"));
            var first = parallel.First(j => j is not null)!;
            await using (var db = Open()) {
                var t = DateTimeOffset.UtcNow.AddMinutes(1);
                Assert.True(await DeploymentProcessor.RenewLeaseAsync(db, first.Id, first.LeaseToken!.Value, t, ct));
                Assert.False(await DeploymentProcessor.RenewLeaseAsync(db, first.Id, Guid.NewGuid(), t, ct));
                Assert.Null(await Claim("long-running", new FixedTime(t.AddSeconds(30))));
            }
            // Simulate a crashed owner after its lease expires. Ambiguous Docker executions
            // reserve their targets until an operator reconciles them.
            await using (var db = Open()) {
                await db.Jobs.Where(j => j.Status == JobStatus.Running)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.LockedAt, DateTimeOffset.UtcNow.AddMinutes(-3)), ct);
                var scan = Job.Enqueue(JobType.ParseScanReport,
                    JsonSerializer.Serialize(new { scanId = Guid.NewGuid() }), DateTimeOffset.UtcNow.AddMinutes(-3), "recovery");
                scan.MarkRunning(DateTimeOffset.UtcNow.AddMinutes(-3), "crashed");
                db.Jobs.Add(scan);
                await db.SaveChangesAsync(ct);
            }
            var recovered = await Claim("replacement");
            Assert.Null(recovered);
            await using (var db = Open()) {
                Assert.Equal(2, await db.Jobs.CountAsync(j => j.Status == JobStatus.DeadLettered, ct));
                Assert.False(await DeploymentProcessor.RenewLeaseAsync(db, first.Id, first.LeaseToken!.Value,
                    DateTimeOffset.UtcNow, ct));
            }
            Assert.Equal(JobType.ParseScanReport,
                (await Claim("available", new FixedTime(DateTimeOffset.UtcNow.AddSeconds(5))))!.Type);
        } finally {
            // All contexts above are disposed before dropping the test database.
            await using var db = new NpgsqlConnection(admin.ConnectionString);
            await db.OpenAsync(ct);
            await using var command = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", db);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
