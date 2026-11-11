using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using AegisOps.Application.Audit;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Docker;
using AegisOps.Infrastructure.Observability;
using AegisOps.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using StackExchange.Redis;
using OrgEnvironment = AegisOps.Domain.Organization.Environment;

namespace AegisOps.Api.Tests;

/// <summary>
/// P5-08: intermediate Docker timeline steps are committed and hinted one by one. SQLite in-memory and a fake
/// executor/Redis stand in for PostgreSQL, Docker and Redis; the real lease guard and commit path are exercised.
/// Shares the non-parallel metrics collection because the deployment counters are process-wide.
/// </summary>
[Collection("WorkerMetrics")]
public sealed class DeploymentStepStreamingTests {
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Each_docker_step_is_committed_and_published_before_the_next_step_starts(bool healthy) {
        var ct = TestContext.Current.CancellationToken;
        var counted = new ConcurrentQueue<string>();
        using var listener = ListenForDeploymentTotals(counted);
        await using var fixture = await Fixture.CreateAsync(ct);
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var guard = DeploymentProcessor.LeaseGuard(fixture.Db, fixture.Job.Id, fixture.Job.LeaseToken!.Value, TimeProvider.System, lost);
        var steps = healthy
            ? new[] { DeploymentEventType.ImagePulled, DeploymentEventType.ContainerStarted, DeploymentEventType.HealthCheckPassed }
            : [DeploymentEventType.ImagePulled, DeploymentEventType.ContainerStarted, DeploymentEventType.HealthCheckFailed, DeploymentEventType.RolledBack];

        async Task<DockerExecution> Execute(DeploymentTarget target, Artifact artifact, string slug, string environment, Guid id,
            IReadOnlyList<KeyValuePair<string, string>> variables, Func<DeploymentEventType, string, CancellationToken, Task> record, CancellationToken token) {
            Assert.Equal(fixture.Deployment.Id, id);
            for (var index = 0; index < steps.Length; index++) {
                // Before the step starts, every earlier step (plus DeployStarted) is already committed and hinted.
                Assert.Equal([DeploymentEventType.DeployStarted, .. steps[..index]], await fixture.PersistedTypesAsync(token));
                Assert.Equal(index + 1, fixture.Redis.Published.Count);
                await record(steps[index], $"step {index}", token);
                Assert.Equal(index + 2, (await fixture.PersistedTypesAsync(token)).Length);
            }
            // The deployment is still Deploying in the store: intermediate commits never move it to a terminal state.
            Assert.Equal(DeploymentStatus.Deploying, await fixture.PersistedStatusAsync(token));
            Assert.DoesNotContain("deployments_total", counted);
            return healthy ? new DockerExecution(true, "Container is healthy.") : new DockerExecution(false, "Health check failed.");
        }

        await DeploymentProcessor.ExecuteAsync(fixture.Db, new NullAudit(), fixture.Deployment.Id, TimeProvider.System, lost.Token,
            fixture.Redis.Multiplexer, guard, Execute);

        // One hint per committed step, each sent after its row was visible to another reader.
        Assert.Equal(steps.Length + 1, fixture.Redis.Published.Count);
        Assert.All(fixture.Redis.Published, item => Assert.Equal(fixture.Deployment.Id.ToString("D"), item.Message));
        Assert.Equal(Enumerable.Range(1, steps.Length + 1), fixture.Redis.Published.Select(item => item.PersistedRows));
        Assert.All(fixture.Redis.Published, item => Assert.Equal(DeploymentUpdates.Channel, item.Channel));

        // The terminal transition and its event are still only tracked: FinishAsync owns that commit.
        Assert.Equal(DeploymentStatus.Deploying, await fixture.PersistedStatusAsync(ct));
        Assert.Empty(counted);
        await fixture.Db.SaveChangesAsync(ct); // stands in for FinishAsync's commit
        var terminal = healthy ? DeploymentStatus.Succeeded : DeploymentStatus.Failed;
        Assert.Equal(terminal, await fixture.PersistedStatusAsync(ct));
        Assert.Equal([$"deployments_total:{terminal}"], counted);

        var timeline = await fixture.PersistedTimelineAsync(ct);
        Assert.Equal(Enumerable.Range(1, steps.Length + 2), timeline.Select(item => item.Sequence));
        Assert.Equal(healthy ? DeploymentEventType.Succeeded : DeploymentEventType.Failed, timeline[^1].Type);
        Assert.False(lost.IsCancellationRequested);
    }

    [Fact]
    public async Task Lost_lease_stops_further_step_writes_and_a_retry_cannot_duplicate_steps() {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var guard = DeploymentProcessor.LeaseGuard(fixture.Db, fixture.Job.Id, fixture.Job.LeaseToken!.Value, TimeProvider.System, lost);
        var reachedAfterLoss = false;

        async Task<DockerExecution> Execute(DeploymentTarget target, Artifact artifact, string slug, string environment, Guid id,
            IReadOnlyList<KeyValuePair<string, string>> variables, Func<DeploymentEventType, string, CancellationToken, Task> record, CancellationToken token) {
            await record(DeploymentEventType.ImagePulled, "image", token);
            await fixture.TakeOverLeaseAsync(token); // another worker now owns the job
            await record(DeploymentEventType.ContainerStarted, "container", token);
            reachedAfterLoss = true;
            return new DockerExecution(true, "Container is healthy.");
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeploymentProcessor.ExecuteAsync(fixture.Db, new NullAudit(),
            fixture.Deployment.Id, TimeProvider.System, lost.Token, fixture.Redis.Multiplexer, guard, Execute));

        Assert.False(reachedAfterLoss);
        // The worker's existing lost-lease cancellation fired, so TryProcessOneAsync skips FinishAsync.
        Assert.True(lost.IsCancellationRequested);
        Assert.Equal([DeploymentEventType.DeployStarted, DeploymentEventType.ImagePulled], await fixture.PersistedTypesAsync(ct));
        Assert.Equal(2, fixture.Redis.Published.Count);

        // Docker's rollback path records with CancellationToken.None; a stale worker still writes nothing.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeploymentProcessor.CommitStepAsync(fixture.Db, fixture.Deployment.Id,
            DeploymentEventType.RolledBack, "restored", TimeProvider.System, guard, fixture.Redis.Multiplexer, CancellationToken.None));
        Assert.Equal(2, (await fixture.PersistedTypesAsync(ct)).Length);
        Assert.Equal(2, fixture.Redis.Published.Count);

        // A retried execution of the same deployment cannot Start() a Deploying release again, so the
        // committed steps stay the only ones for this deployment (existing abandon/retry semantics).
        await using var retry = fixture.NewContext();
        var executorCalled = false;
        Task<DockerExecution> Never(DeploymentTarget target, Artifact artifact, string slug, string environment, Guid id,
            IReadOnlyList<KeyValuePair<string, string>> variables, Func<DeploymentEventType, string, CancellationToken, Task> record, CancellationToken token) {
            executorCalled = true;
            return Task.FromResult(new DockerExecution(true, "unexpected"));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeploymentProcessor.ExecuteAsync(retry, new NullAudit(),
            fixture.Deployment.Id, TimeProvider.System, ct, fixture.Redis.Multiplexer, null, Never));
        Assert.False(executorCalled);
        Assert.Equal([1, 2], (await fixture.PersistedTimelineAsync(ct)).Select(item => item.Sequence));
    }

    private static MeterListener ListenForDeploymentTotals(ConcurrentQueue<string> counted) {
        var listener = new MeterListener {
            InstrumentPublished = (instrument, current) => {
                if (instrument.Meter.Name == JobMetrics.MeterName && instrument.Name == "aegisops_deployments_total")
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
            if (value == 0) return;
            foreach (var tag in tags)
                if (tag.Key == "status") counted.Enqueue($"deployments_total:{tag.Value}");
        });
        listener.Start();
        return listener;
    }

    private sealed class NullAudit : IAuditWriter {
        public void Write(AuditEvent auditEvent) { }
    }

    private sealed class Fixture : IAsyncDisposable {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<AegisOpsDbContext> options;

        private Fixture(SqliteConnection connection) {
            this.connection = connection;
            // The assembly is at EF's twenty cached-provider limit: these contexts opt out of the cache, and the
            // limit warning (raised even for uncached providers once the cache is full) is ignored for them only.
            options = new DbContextOptionsBuilder<AegisOpsDbContext>()
                .UseSqlite(connection).UseSnakeCaseNamingConvention()
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>()
                .EnableServiceProviderCaching(false)
                .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)).Options;
            Db = NewContext();
            Redis = new RecordingRedis(this);
        }

        public AegisOpsDbContext Db { get; }
        public RecordingRedis Redis { get; }
        public Deployment Deployment { get; private set; } = null!;
        public Job Job { get; private set; } = null!;

        public AegisOpsDbContext NewContext() => new(options);

        public static async Task<Fixture> CreateAsync(CancellationToken ct) {
            var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=False");
            await connection.OpenAsync(ct);
            var fixture = new Fixture(connection);
            var db = fixture.Db;
            await db.Database.EnsureCreatedAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var team = Team.Create("Streaming", "streaming", now);
            var project = Project.Create(team.Id, "Streaming", "streaming", now);
            var environment = OrgEnvironment.Create(project.Id, "dev", EnvironmentTier.Development, 0,
                DeploymentTarget.Docker("streaming-app", null, 9100, 8080, "/health", 5));
            var artifact = Artifact.Create(project.Id, "1.0.0", new string('a', 40), "main", "example/app:1.0.0",
                CheckStatus.Passed, CheckStatus.Passed, now, createdById: Guid.NewGuid());
            var deployment = Deployment.Request(project.Id, environment.Id, artifact.Id, now, "streaming", Guid.NewGuid(), null, "test");
            deployment.BeginEvaluation();
            deployment.ApplyDecision(new PolicyOutcome(PolicyDecision.Allow, Guid.NewGuid(), 0, null), now);
            var job = Job.Enqueue(JobType.ExecuteDeployment, JsonSerializer.Serialize(new { deploymentId = deployment.Id }), now, "streaming");
            job.MarkRunning(now, "worker-under-test");
            db.AddRange(team, project, environment, artifact, deployment, job);
            await db.SaveChangesAsync(ct);
            fixture.Deployment = deployment;
            fixture.Job = job;
            return fixture;
        }

        public async Task<DeploymentEventType[]> PersistedTypesAsync(CancellationToken ct) =>
            (await PersistedTimelineAsync(ct)).Select(item => item.Type).ToArray();

        public async Task<DeploymentEvent[]> PersistedTimelineAsync(CancellationToken ct) {
            await using var reader = NewContext();
            return (await reader.DeploymentEvents.AsNoTracking().Where(item => item.DeploymentId == Deployment.Id)
                .ToListAsync(ct)).OrderBy(item => item.Sequence).ToArray();
        }

        public int PersistedRows() {
            using var reader = NewContext();
            return reader.DeploymentEvents.Count(item => item.DeploymentId == Deployment.Id);
        }

        public async Task<DeploymentStatus> PersistedStatusAsync(CancellationToken ct) {
            await using var reader = NewContext();
            return await reader.Deployments.AsNoTracking().Where(item => item.Id == Deployment.Id).Select(item => item.Status).SingleAsync(ct);
        }

        public async Task TakeOverLeaseAsync(CancellationToken ct) {
            await using var other = NewContext();
            await other.Jobs.Where(item => item.Id == Job.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.LeaseToken, Guid.NewGuid()), ct);
        }

        public async ValueTask DisposeAsync() {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>Fake Redis: records each hint together with how many timeline rows were committed at that moment.</summary>
    private sealed class RecordingRedis {
        public RecordingRedis(Fixture fixture) {
            var multiplexer = DispatchProxy.Create<IConnectionMultiplexer, MultiplexerProxy>();
            var subscriber = DispatchProxy.Create<ISubscriber, SubscriberProxy>();
            ((SubscriberProxy)(object)subscriber).OnPublish = (channel, message) =>
                Published.Add(new PublishedHint(channel, message, fixture.PersistedRows()));
            ((MultiplexerProxy)(object)multiplexer).Subscriber = subscriber;
            Multiplexer = multiplexer;
        }

        public IConnectionMultiplexer Multiplexer { get; }
        public List<PublishedHint> Published { get; } = [];
    }

    private sealed record PublishedHint(string Channel, string Message, int PersistedRows);

    public class MultiplexerProxy : DispatchProxy {
        public ISubscriber? Subscriber { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IConnectionMultiplexer.GetSubscriber)
                ? Subscriber
                : throw new InvalidOperationException($"Unexpected Redis call: {targetMethod?.Name}");
    }

    public class SubscriberProxy : DispatchProxy {
        public Action<string, string>? OnPublish { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            if (targetMethod?.Name != nameof(ISubscriber.PublishAsync))
                throw new InvalidOperationException($"Unexpected subscriber call: {targetMethod?.Name}");
            OnPublish?.Invoke(((RedisChannel)args![0]!).ToString(), ((RedisValue)args[1]!).ToString());
            return Task.FromResult(1L);
        }
    }

    private sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<Policy>().Ignore(policy => policy.AppliesToTiers);
            // SQLite cannot compare DateTimeOffset text; RenewLeaseAsync filters on locked_at.
            modelBuilder.Entity<Job>().Property(job => job.LockedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
        }
    }
}
