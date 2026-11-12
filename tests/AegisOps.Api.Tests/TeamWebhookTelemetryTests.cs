using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Json;
using AegisOps.Api.Organization;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Observability;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;
using ProjectEnvironment = AegisOps.Domain.Organization.Environment;
using AegisOps.Infrastructure.Identity;

namespace AegisOps.Api.Tests;

/// <summary>
/// Audit check P5-10: all-time webhook telemetry (durable per-team counters + Prometheus metrics) and
/// historical replay. SQLite-backed except the processor test, which needs AEGISOPS_TEST_POSTGRES.
/// </summary>
public sealed class TeamWebhookTelemetryTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly WebhookDeliveryOutcome?[] RetriesThenDeadLetter = [
        WebhookDeliveryOutcome.Failed, WebhookDeliveryOutcome.Failed, WebhookDeliveryOutcome.Failed,
        WebhookDeliveryOutcome.Failed, WebhookDeliveryOutcome.DeadLettered,
    ];

    // ---------- Durable all-time summary ----------

    [Fact]
    public async Task Summary_counts_real_attempts_and_survives_the_succeeded_job_purge() {
        await using var store = await Store.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await using var scope = store.Scope();
        var db = Db(scope);

        var delivered = await store.SeedEventAsync(db, store.Team.Id, now.AddDays(-8));
        var deliveredJob = await NotificationJobAsync(db, delivered);
        Assert.Equal(WebhookDeliveryOutcome.Succeeded, await AttemptAsync(db, deliveredJob, succeeded: true, attempted: true, now.AddDays(-8)));

        var failing = await store.SeedEventAsync(db, store.Team.Id, now.AddMinutes(-5));
        var failingJob = await NotificationJobAsync(db, failing);
        var outcomes = new List<WebhookDeliveryOutcome?>();
        for (var i = 0; i < failingJob.MaxAttempts; i++)
            outcomes.Add(await AttemptAsync(db, failingJob, succeeded: false, attempted: true, now.AddMinutes(-5 + i)));
        Assert.Equal(RetriesThenDeadLetter, outcomes);
        Assert.Equal(JobStatus.DeadLettered, failingJob.Status);
        Assert.All(await db.WebhookDeliveryAttempts.AsNoTracking().Where(item => item.JobId == failingJob.Id).ToListAsync(Token),
            attempt => Assert.Equal("Failed", attempt.Status));

        // A skipped notification (no destination / not subscribed) is not a delivery and is not counted.
        var skipped = await store.SeedEventAsync(db, store.Team.Id, now);
        Assert.Null(await AttemptAsync(db, await NotificationJobAsync(db, skipped), succeeded: true, attempted: false, now));

        var expected = new TeamWebhookSummaryResponse(1, 5, 1, now.AddDays(-8), now.AddMinutes(-1));
        Assert.Equal(expected, await SummaryAsync(store, store.Owner, scope));

        // The 7-day retention purge deletes the delivered job and cascades its attempt rows...
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<JobCleanupService>().CleanupAsync(Token));
        db.ChangeTracker.Clear();
        Assert.False(await db.Jobs.AnyAsync(job => job.Id == deliveredJob.Id, Token));
        Assert.False(await db.WebhookDeliveryAttempts.AnyAsync(attempt => attempt.JobId == deliveredJob.Id, Token));
        // ...but the all-time counters are untouched.
        Assert.Equal(expected, await SummaryAsync(store, store.Owner, scope));
        Assert.Equal(expected, await SummaryAsync(store, store.Admin, scope));
    }

    [Fact]
    public async Task Summary_is_empty_for_a_new_team_and_hidden_from_members_and_outsiders() {
        await using var store = await Store.CreateAsync();
        await using var scope = store.Scope();
        Assert.Equal(new TeamWebhookSummaryResponse(0, 0, 0, null, null), await SummaryAsync(store, store.Owner, scope));
        foreach (var user in new[] { store.Member, store.Outsider }) {
            var result = await TeamWebhookSettingsEndpoints.Summary(store.Team.Slug, Principal(user.Id), Users(scope), Db(scope), Context());
            Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        }
    }

    [Fact]
    public async Task Counters_of_different_teams_are_isolated_and_accumulate_atomically() {
        await using var store = await Store.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await using var scope = store.Scope();
        var db = Db(scope);
        var mine = await store.SeedEventAsync(db, store.Team.Id, now);
        var theirs = await store.SeedEventAsync(db, store.OtherTeam.Id, now);

        for (var i = 0; i < 3; i++)
            Assert.True(await TeamWebhookDeliveryStatsStore.RecordAsync(db, mine, WebhookDeliveryOutcome.Succeeded, now, Token));
        Assert.True(await TeamWebhookDeliveryStatsStore.RecordAsync(db, theirs, WebhookDeliveryOutcome.DeadLettered, now, Token));
        Assert.False(await TeamWebhookDeliveryStatsStore.RecordAsync(db, Guid.NewGuid(), WebhookDeliveryOutcome.Failed, now, Token));

        var stats = await db.TeamWebhookDeliveryStats.AsNoTracking().ToDictionaryAsync(item => item.TeamId, Token);
        Assert.Equal(2, stats.Count);
        Assert.Equal((3L, 0L, 0L), (stats[store.Team.Id].Delivered, stats[store.Team.Id].FailedAttempts, stats[store.Team.Id].DeadLettered));
        Assert.Equal((0L, 1L, 1L), (stats[store.OtherTeam.Id].Delivered, stats[store.OtherTeam.Id].FailedAttempts, stats[store.OtherTeam.Id].DeadLettered));
    }

    // ---------- Historical replay ----------

    [Fact]
    public async Task Replay_is_owner_or_admin_only_enqueues_once_per_event_and_is_audited() {
        await using var store = await Store.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await using var scope = store.Scope();
        var db = Db(scope);
        var eventId = await store.SeedEventAsync(db, store.Team.Id, now);
        var original = await NotificationJobAsync(db, eventId);

        // The outbox job is still pending: a replay would only duplicate it.
        Assert.Equal(StatusCodes.Status409Conflict, Status(await ReplayAsync(store, store.Owner, eventId, scope)));
        await AttemptAsync(db, original, succeeded: true, attempted: true, now);

        // Members and non-members cannot see that the event exists.
        foreach (var user in new[] { store.Member, store.Outsider })
            Assert.Equal(StatusCodes.Status404NotFound, Status(await ReplayAsync(store, user, eventId, scope)));
        // Another team's event and unknown ids are indistinguishable.
        var foreign = await store.SeedEventAsync(db, store.OtherTeam.Id, now);
        Assert.Equal(StatusCodes.Status404NotFound, Status(await ReplayAsync(store, store.Owner, foreign, scope)));
        Assert.Equal(StatusCodes.Status404NotFound, Status(await ReplayAsync(store, store.Owner, Guid.NewGuid(), scope)));
        Assert.Empty(await AuditRowsAsync(db));

        var accepted = await ReplayAsync(store, store.Owner, eventId, scope);
        Assert.Equal(StatusCodes.Status202Accepted, Status(accepted));
        var response = Assert.IsType<TeamWebhookReplayResponse>(Assert.IsAssignableFrom<IValueHttpResult>(accepted).Value);
        Assert.Equal(eventId, response.EventId);
        var replay = await db.Jobs.AsNoTracking().SingleAsync(job => job.Id == response.JobId, Token);
        Assert.Equal((JobType.SendNotification, JobStatus.Pending, 0), (replay.Type, replay.Status, replay.Attempts));
        // Exactly the outbox payload, so the Worker sends it through the same signed TeamWebhookSender path.
        Assert.Equal(JsonSerializer.Serialize(new { eventId }), replay.Payload);
        var audit = Assert.Single(await AuditRowsAsync(db));
        Assert.Equal((TeamWebhookSettingsEndpoints.ReplayedAction, "User", (Guid?)store.Owner.Id, store.Owner.Email, "DeploymentEvent", (Guid?)eventId, "Succeeded"),
            (audit.Action, audit.ActorType, audit.ActorId, audit.ActorDisplay, audit.ResourceType, audit.ResourceId, audit.Outcome));

        // One active replay per event: Pending and Running both block another one.
        Assert.Equal(StatusCodes.Status409Conflict, Status(await ReplayAsync(store, store.Owner, eventId, scope)));
        var tracked = await db.Jobs.SingleAsync(job => job.Id == response.JobId, Token);
        tracked.MarkRunning(now, "worker");
        db.WebhookDeliveryAttempts.Add(WebhookDeliveryAttempt.Start(tracked.Id, tracked.Attempts, now));
        await db.SaveChangesAsync(Token);
        Assert.Equal(StatusCodes.Status409Conflict, Status(await ReplayAsync(store, store.Admin, eventId, scope)));
        Assert.Single(await AuditRowsAsync(db));
        Assert.Equal(1, await ActiveJobsAsync(db, eventId));

        // Once it finishes, a platform Admin (not a team member) may replay it again.
        tracked.Succeed(now);
        await db.SaveChangesAsync(Token);
        Assert.Equal(StatusCodes.Status202Accepted, Status(await ReplayAsync(store, store.Admin, eventId, scope)));
        Assert.Equal(1, await ActiveJobsAsync(db, eventId));
        var rows = await AuditRowsAsync(db);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.ActorId == store.Admin.Id && row.Action == TeamWebhookSettingsEndpoints.ReplayedAction);
    }

    [Fact]
    public async Task Replay_works_for_an_event_whose_original_job_was_purged() {
        await using var store = await Store.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await using var scope = store.Scope();
        var db = Db(scope);
        var eventId = await store.SeedEventAsync(db, store.Team.Id, now.AddDays(-30));
        await AttemptAsync(db, await NotificationJobAsync(db, eventId), succeeded: true, attempted: true, now.AddDays(-30));
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<JobCleanupService>().CleanupAsync(Token));
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Jobs.ToListAsync(Token));
        Assert.Equal("Expired", (await DeliveryAsync(store, scope, eventId)).Status);

        var result = await ReplayAsync(store, store.Owner, eventId, scope);

        Assert.Equal(StatusCodes.Status202Accepted, Status(result));
        Assert.Equal(1, await ActiveJobsAsync(db, eventId));
        Assert.Equal("Pending", (await DeliveryAsync(store, scope, eventId)).Status);
        // Replays do not rewrite history: the all-time counters still show the original delivery.
        Assert.Equal(1, (await SummaryAsync(store, store.Owner, scope)).Delivered);
    }

    // ---------- Migration ----------

    [Fact]
    public void PostgresMigrationAndModelSnapshotIncludeDeliveryStats() {
        var options = new DbContextOptionsBuilder<AegisOpsDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused").UseSnakeCaseNamingConvention()
            .EnableServiceProviderCaching(false).Options;
        using var db = new AegisOpsDbContext(options);

        var migrations = db.GetService<IMigrationsAssembly>();
        Assert.Contains(migrations.Migrations.Keys, id => id.EndsWith("_TeamWebhookDeliveryStats", StringComparison.Ordinal));
        var entity = migrations.ModelSnapshot?.Model.FindEntityType(typeof(TeamWebhookDeliveryStats).FullName!);
        Assert.NotNull(entity);
        Assert.Equal("team_webhook_delivery_stats", entity.GetTableName());
        Assert.Equal("org", entity.GetSchema());
        // Counters reference only the team: the jobs purge cannot cascade into them.
        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(Team).FullName, foreignKey.PrincipalEntityType.Name);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    // ---------- Helpers ----------

    private static async Task<WebhookDeliveryOutcome?> AttemptAsync(AegisOpsDbContext db, Job job, bool succeeded, bool attempted, DateTimeOffset at) {
        // The claim and completion steps of DeploymentProcessor, minus the PostgreSQL-only row locks.
        job.MarkRunning(at, "worker");
        db.WebhookDeliveryAttempts.Add(WebhookDeliveryAttempt.Start(job.Id, job.Attempts, at));
        await db.SaveChangesAsync(Token);
        if (succeeded) job.Succeed(at); else job.Fail("Webhook delivery failed.", at);
        var outcome = await DeploymentProcessor.CompleteWebhookAttemptAsync(db, job, job.Attempts, succeeded, attempted, at, Token);
        await db.SaveChangesAsync(Token);
        return outcome;
    }

    private static async Task<Job> NotificationJobAsync(AegisOpsDbContext db, Guid eventId) {
        var jobs = await db.Jobs.Where(job => job.Type == JobType.SendNotification).ToListAsync(Token);
        return jobs.Single(job => TeamWebhookDeliveryStatsStore.TryReadEventId(job.Payload) == eventId);
    }

    private static async Task<int> ActiveJobsAsync(AegisOpsDbContext db, Guid eventId) =>
        (await db.Jobs.AsNoTracking().Where(job => job.Status == JobStatus.Pending || job.Status == JobStatus.Running).ToListAsync(Token))
        .Count(job => TeamWebhookDeliveryStatsStore.TryReadEventId(job.Payload) == eventId);

    private static async Task<List<AuditEvent>> AuditRowsAsync(AegisOpsDbContext db) =>
        await db.AuditEvents.AsNoTracking().Where(row => row.Action == TeamWebhookSettingsEndpoints.ReplayedAction).ToListAsync(Token);

    private static async Task<TeamWebhookSummaryResponse> SummaryAsync(Store store, User user, AsyncServiceScope scope) {
        var result = await TeamWebhookSettingsEndpoints.Summary(store.Team.Slug, Principal(user.Id), Users(scope), Db(scope), Context());
        return Assert.IsType<TeamWebhookSummaryResponse>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
    }

    private static Task<IResult> ReplayAsync(Store store, User user, Guid eventId, AsyncServiceScope scope) =>
        TeamWebhookSettingsEndpoints.Replay(store.Team.Slug, eventId, Principal(user.Id), Users(scope), Db(scope),
            new EfAuditWriter(Db(scope)), TimeProvider.System, Context());

    private static async Task<TeamWebhookDeliveryResponse> DeliveryAsync(Store store, AsyncServiceScope scope, Guid eventId) {
        var result = await TeamWebhookSettingsEndpoints.Deliveries(store.Team.Slug, Principal(store.Owner.Id), Users(scope), Db(scope), TimeProvider.System, Context());
        return Assert.Single(Assert.IsAssignableFrom<IEnumerable<TeamWebhookDeliveryResponse>>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value),
            item => item.EventId == eventId);
    }

    private static AegisOpsDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
    private static UserManager<User> Users(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<UserManager<User>>();

    private static ClaimsPrincipal Principal(Guid userId) => new(new ClaimsIdentity(
        [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "test"));

    private static DefaultHttpContext Context() => new() { RequestAborted = Token };

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? StatusCodes.Status200OK;

    private sealed class Store : IAsyncDisposable {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly ServiceProvider services;
        public Team Team { get; } = Team.Create("Webhook Team", $"webhook-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        public Team OtherTeam { get; } = Team.Create("Other Team", $"other-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        public User Owner { get; } = User.Create($"owner-{Guid.NewGuid():N}@example.test", "Owner", DateTimeOffset.UtcNow);
        public User Member { get; } = User.Create($"member-{Guid.NewGuid():N}@example.test", "Member", DateTimeOffset.UtcNow);
        public User Outsider { get; } = User.Create($"outsider-{Guid.NewGuid():N}@example.test", "Outsider", DateTimeOffset.UtcNow);
        public User Admin { get; } = User.Create($"admin-{Guid.NewGuid():N}@example.test", "Admin", DateTimeOffset.UtcNow);

        private Store() {
            connection.Open();
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton(TimeProvider.System);
            collection.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection).UseSnakeCaseNamingConvention()
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>().EnableServiceProviderCaching(false));
            collection.AddIdentityCore<User>(IdentityStoreExtensions.ConfigureIdentity).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AegisOpsDbContext>();
            collection.AddSingleton<JobCleanupService>();
            services = collection.BuildServiceProvider();
        }

        public static async Task<Store> CreateAsync() {
            var store = new Store();
            await using var scope = store.Scope();
            var db = Db(scope);
            await db.Database.EnsureCreatedAsync(Token);
            var users = Users(scope);
            foreach (var user in new[] { store.Owner, store.Member, store.Outsider, store.Admin }) await users.CreateAsync(user);
            var role = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "Admin", NormalizedName = "ADMIN" };
            db.Roles.Add(role);
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = store.Admin.Id, RoleId = role.Id });
            db.Teams.AddRange(store.Team, store.OtherTeam);
            db.TeamMembers.AddRange(
                TeamMember.Create(store.Team.Id, store.Owner.Id, TeamRole.Owner, DateTimeOffset.UtcNow),
                TeamMember.Create(store.Team.Id, store.Member.Id, TeamRole.Member, DateTimeOffset.UtcNow),
                TeamMember.Create(store.OtherTeam.Id, store.Outsider.Id, TeamRole.Owner, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Token);
            return store;
        }

        public AsyncServiceScope Scope() => services.CreateAsyncScope();

        /// <summary>Creates a deployment event of <paramref name="teamId"/>; the outbox adds its Pending SendNotification job.</summary>
        public async Task<Guid> SeedEventAsync(AegisOpsDbContext db, Guid teamId, DateTimeOffset at) {
            var project = Project.Create(teamId, "Telemetry", $"telemetry-{Guid.NewGuid():N}", at);
            var environment = ProjectEnvironment.Create(project.Id, "dev", EnvironmentTier.Development, 1, DeploymentTarget.Noop());
            var artifact = Artifact.Create(project.Id, "1.0.0", "abcdef1234567", "main", "example/image:1", CheckStatus.Passed,
                CheckStatus.Passed, at, createdById: Owner.Id);
            var deployment = Deployment.Request(project.Id, environment.Id, artifact.Id, at, "telemetry", Owner.Id, null, "test");
            db.AddRange(project, environment, artifact, deployment);
            await db.SaveChangesAsync(Token);
            var item = DeploymentEvent.Create(deployment.Id, 1, at, DeploymentEventType.Succeeded, "done");
            db.DeploymentEvents.Add(item);
            await db.SaveChangesAsync(Token);
            return item.Id;
        }

        public async ValueTask DisposeAsync() {
            await services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<Policy>().Ignore(policy => policy.AppliesToTiers);
            modelBuilder.Entity<Deployment>().Property(item => item.RequestedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<DeploymentEvent>().Property(item => item.Timestamp).HasConversion(new DateTimeOffsetToBinaryConverter());
            // SQLite cannot compare DateTimeOffset text; JobCleanupService filters on completed_at.
            modelBuilder.Entity<Job>().Property(job => job.CompletedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
        }
    }
}

/// <summary>Process-wide meters: share the non-parallel metrics collection with the other metric tests.</summary>
[Collection("WorkerMetrics")]
public sealed class TeamWebhookMetricsTests {
    private sealed record Measurement(string Instrument, double Value, Dictionary<string, string?> Tags);

    private static readonly string[] OutcomeLabelOnly = ["outcome"];
    private static readonly string[] AllOutcomes = ["DeadLettered", "Failed", "Succeeded"];

    [Fact]
    public void Metrics_seed_every_outcome_and_record_only_the_bounded_outcome_label() {
        var measurements = new ConcurrentQueue<Measurement>();
        using var listener = Listen(measurements);

        WebhookMetrics.InitializeZeroSeries();
        var seeded = measurements.Where(item => item.Instrument == "aegisops_webhook_deliveries_total" && item.Value == 0).ToArray();
        Assert.Equal(AllOutcomes, seeded.Select(item => item.Tags["outcome"]!).Distinct().Order(StringComparer.Ordinal));
        // Histograms are never seeded with fake samples.
        Assert.DoesNotContain(measurements, item => item.Instrument == "aegisops_webhook_delivery_duration_seconds");

        measurements.Clear();
        WebhookMetrics.Record(WebhookDeliveryOutcome.Succeeded, 0.25);
        WebhookMetrics.Record(WebhookDeliveryOutcome.Failed, 10);
        WebhookMetrics.Record(WebhookDeliveryOutcome.DeadLettered, null); // lease expiry: no timing

        Assert.Single(measurements, item => item.Instrument == "aegisops_webhook_deliveries_total" && item.Value == 1 && item.Tags["outcome"] == "Succeeded");
        Assert.Single(measurements, item => item.Instrument == "aegisops_webhook_deliveries_total" && item.Value == 1 && item.Tags["outcome"] == "Failed");
        Assert.Single(measurements, item => item.Instrument == "aegisops_webhook_deliveries_total" && item.Value == 1 && item.Tags["outcome"] == "DeadLettered");
        var durations = measurements.Where(item => item.Instrument == "aegisops_webhook_delivery_duration_seconds").ToArray();
        Assert.Equal(2, durations.Length);
        Assert.Contains(durations, item => item.Value == 0.25 && item.Tags["outcome"] == "Succeeded");
        Assert.Contains(durations, item => item.Value == 10 && item.Tags["outcome"] == "Failed");
        // Bounded labels only: no team, URL, event, deployment or job identifiers.
        Assert.All(measurements, item => Assert.Equal(OutcomeLabelOnly, item.Tags.Keys));
        Assert.All(measurements, item => Assert.Contains(item.Tags["outcome"]!, AllOutcomes));
    }

    /// <summary>
    /// End-to-end through DeploymentProcessor on PostgreSQL (row locks, advisory lock, ON CONFLICT upsert).
    /// Set AEGISOPS_TEST_POSTGRES to an administrative connection string to run it against a disposable database.
    /// </summary>
    [Fact]
    public async Task Processor_commits_failed_attempt_counter_and_emits_bounded_metrics_on_postgres() {
        var connection = System.Environment.GetEnvironmentVariable("AEGISOPS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) Assert.Skip("Set AEGISOPS_TEST_POSTGRES to run the PostgreSQL webhook processor test.");
        var ct = TestContext.Current.CancellationToken;
        var name = "aegisops_webhook_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(connection)) {
            await admin.OpenAsync(ct);
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await command.ExecuteNonQueryAsync(ct);
        }
        var target = new NpgsqlConnectionStringBuilder(connection) { Database = name };
        try {
            var now = DateTimeOffset.UtcNow;
            var team = Team.Create("Webhook metrics", "webhook-metrics", now);
            // A literal non-public IPv4 destination fails URL validation before any network I/O.
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                [$"Webhooks:Teams:{team.Id:D}:Url"] = "https://203.0.113.10/hook",
                [$"Webhooks:Teams:{team.Id:D}:Secret"] = Convert.ToBase64String(new byte[32]),
            }).Build();
            await using var services = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration)
                .AddDbContext<AegisOpsDbContext>(options => options.UseNpgsql(target.ConnectionString).UseSnakeCaseNamingConvention()
                    .EnableServiceProviderCaching(false))
                .BuildServiceProvider();
            Guid eventId;
            await using (var scope = services.CreateAsyncScope()) {
                var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
                await db.Database.MigrateAsync(ct);
                var project = Project.Create(team.Id, "Webhook metrics", "webhook-metrics", now);
                var environment = ProjectEnvironment.Create(project.Id, "dev", EnvironmentTier.Development, 0, DeploymentTarget.Noop());
                db.AddRange(team, project, environment);
                await db.SaveChangesAsync(ct);
                var artifactId = Guid.NewGuid();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO security.artifacts (id, project_id, version, commit_sha, branch, image_reference,
                        build_status, test_status, created_at)
                    VALUES ({artifactId}, {project.Id}, 'v1', 'abcdef1234567890', 'main', 'example:latest', 'Passed', 'Passed', {now})
                    """, ct);
                var deployment = Deployment.Request(project.Id, environment.Id, artifactId, now, "metrics", null, Guid.NewGuid(), null);
                db.Deployments.Add(deployment);
                await db.SaveChangesAsync(ct);
                var item = DeploymentEvent.Create(deployment.Id, 1, now, DeploymentEventType.Succeeded, "done");
                db.DeploymentEvents.Add(item); // the outbox enqueues the SendNotification job
                await db.SaveChangesAsync(ct);
                eventId = item.Id;
            }

            var measurements = new ConcurrentQueue<Measurement>();
            using var listener = Listen(measurements);
            await using (var scope = services.CreateAsyncScope()) {
                var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
                Assert.True(await DeploymentProcessor.TryProcessOneAsync(db, new EfAuditWriter(db),
                    services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, "metrics-worker", ct));
            }

            await using (var scope = services.CreateAsyncScope()) {
                var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
                var job = await db.Jobs.AsNoTracking().SingleAsync(item => item.Type == JobType.SendNotification, ct);
                Assert.Equal(JobStatus.Pending, job.Status); // retry scheduled
                Assert.Equal("Failed", (await db.WebhookDeliveryAttempts.AsNoTracking().SingleAsync(ct)).Status);
                var stats = await db.TeamWebhookDeliveryStats.AsNoTracking().SingleAsync(ct);
                Assert.Equal((team.Id, 0L, 1L, 0L), (stats.TeamId, stats.Delivered, stats.FailedAttempts, stats.DeadLettered));
                Assert.NotNull(stats.LastFailureAt);
            }
            Assert.Single(measurements, item => item.Instrument == "aegisops_webhook_deliveries_total" && item.Value == 1 && item.Tags["outcome"] == "Failed");
            Assert.Single(measurements, item => item.Instrument == "aegisops_webhook_delivery_duration_seconds" && item.Tags["outcome"] == "Failed");
            Assert.All(measurements.Where(item => item.Instrument.StartsWith("aegisops_webhook_", StringComparison.Ordinal)),
                item => Assert.Equal(OutcomeLabelOnly, item.Tags.Keys));
            Assert.DoesNotContain(measurements, item => item.Tags.ContainsValue(eventId.ToString()) || item.Tags.ContainsValue(team.Id.ToString()));
        } finally {
            NpgsqlConnection.ClearAllPools();
            // All contexts above are disposed before dropping the test database.
            await using var admin = new NpgsqlConnection(connection);
            await admin.OpenAsync(ct);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(ct);
        }
    }

    private static MeterListener Listen(ConcurrentQueue<Measurement> measurements) {
        var listener = new MeterListener {
            InstrumentPublished = (instrument, current) => {
                if (instrument.Meter.Name == JobMetrics.MeterName && instrument.Name.StartsWith("aegisops_webhook_", StringComparison.Ordinal))
                    current.EnableMeasurementEvents(instrument);
            },
        };
        void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
            var copy = new Dictionary<string, string?>();
            foreach (var tag in tags) copy[tag.Key] = tag.Value?.ToString();
            measurements.Enqueue(new Measurement(instrument.Name, value, copy));
        }
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        listener.Start();
        return listener;
    }
}
