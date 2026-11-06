using System.Data.Common;
using System.Diagnostics.Metrics;
using AegisOps.Domain.Jobs;
using AegisOps.Infrastructure.Observability;
using AegisOps.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;

namespace AegisOps.Api.Tests;

[Collection("WorkerMetrics")]
public sealed class JobQueueDepthServiceTests {
    [Fact]
    public async Task PollAsync_reports_persisted_counts_by_status() {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        var queries = new SqlObserver();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection).UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCustomizer, SqliteCustomizer>().AddInterceptors(queries));
        services.AddSingleton<JobQueueDepthService>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var now = DateTimeOffset.UtcNow;
            var pending = Job.Enqueue(JobType.ParseScanReport, "{}", now.AddMinutes(-1), "pending");
            var delayed = Job.Enqueue(JobType.ParseScanReport, "{}", now.AddHours(1), "delayed");
            var running = Job.Enqueue(JobType.EvaluateDeployment, "{}", now, "running");
            running.MarkRunning(now, "worker");
            var deadLettered = Job.Enqueue(JobType.ExecuteDeployment, "{}", now, "dead");
            deadLettered.MarkRunning(now, "worker");
            deadLettered.Abandon("test", now);
            var succeeded = Job.Enqueue(JobType.ExecuteDeployment, "{}", now, "done");
            succeeded.MarkRunning(now, "worker");
            succeeded.Succeed(now);
            db.Jobs.AddRange(pending, delayed, running, deadLettered, succeeded);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        queries.Statements.Clear();

        var observed = new Dictionary<string, long>();
        using var listener = new MeterListener {
            InstrumentPublished = (instrument, current) => {
                if (instrument.Meter.Name == JobMetrics.MeterName && instrument.Name == "aegisops_jobs_queue_depth")
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
            foreach (var tag in tags)
                if (tag.Key == "status") observed[(string)tag.Value!] = value;
        });
        listener.Start();
        try {
            await provider.GetRequiredService<JobQueueDepthService>().PollAsync(TestContext.Current.CancellationToken);
            listener.RecordObservableInstruments();
            Assert.Equal(1, observed["Pending"]);
            Assert.Equal(1, observed["Delayed"]);
            Assert.Equal(1, observed["Running"]);
            Assert.Equal(1, observed["DeadLettered"]);
            Assert.Equal(4, observed.Count);
            Assert.Single(queries.Statements);
            var groupQuery = Assert.Single(queries.Statements, sql => sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("WHERE", groupQuery, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("status", groupQuery, StringComparison.OrdinalIgnoreCase);
        } finally {
            JobMetrics.ClearQueueDepth();
        }
    }

    [Fact]
    public async Task PollAsync_discards_stale_depth_when_database_query_fails() {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection).UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCustomizer, SqliteCustomizer>());
        services.AddSingleton<JobQueueDepthService>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }
        var service = provider.GetRequiredService<JobQueueDepthService>();
        await service.PollAsync(TestContext.Current.CancellationToken);
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE jobs", TestContext.Current.CancellationToken);
        }
        var measurements = new List<long>();
        using var listener = new MeterListener {
            InstrumentPublished = (instrument, current) => {
                if (instrument.Meter.Name == JobMetrics.MeterName && instrument.Name == "aegisops_jobs_queue_depth")
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => measurements.Add(value));
        listener.Start();
        try {
            await Assert.ThrowsAnyAsync<Exception>(() => service.PollAsync(TestContext.Current.CancellationToken));
            listener.RecordObservableInstruments();
            Assert.Empty(measurements);
        } finally {
            JobMetrics.ClearQueueDepth();
        }
    }

    [Fact]
    public void SafeDetail_reports_inner_exception_type_without_message() {
        var exception = new InvalidOperationException("Password=not-for-logs", new TimeoutException("secret"));
        Assert.Equal("TimeoutException", JobQueueDepthService.SafeDetail(exception));
    }

    private sealed class SqlObserver : DbCommandInterceptor {
        public List<string> Statements { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) {
            Statements.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default) {
            Statements.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class SqliteCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<AegisOps.Domain.Identity.ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<AegisOps.Domain.Policy.Policy>().Ignore(policy => policy.AppliesToTiers);
            modelBuilder.Entity<Job>().Property(job => job.ScheduledAt)
                .HasConversion(new DateTimeOffsetToBinaryConverter());
        }
    }
}
