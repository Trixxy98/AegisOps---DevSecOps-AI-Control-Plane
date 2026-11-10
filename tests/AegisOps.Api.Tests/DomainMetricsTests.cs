using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AegisOps.Domain.Ai;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Observability;
using AegisOps.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AegisOps.Api.Tests;

// Meters are process-wide: share the non-parallel metrics collection so other tests'
// commits cannot add to the measurements asserted here.
[Collection("WorkerMetrics")]
public sealed class DomainMetricsTests {
    private sealed record Measurement(string Instrument, double Value, Dictionary<string, string?> Tags);

    [Fact]
    public async Task Committed_deployment_scan_and_audit_changes_are_counted_with_bounded_labels() {
        var ct = TestContext.Current.CancellationToken;
        var measurements = new ConcurrentQueue<Measurement>();
        using var listener = Listen(measurements);
        await using var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=False");
        await connection.OpenAsync(ct);
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var staging = AegisOps.Domain.Organization.Environment.Create(projectId, "staging", EnvironmentTier.Staging, 2, DeploymentTarget.Noop());
        var succeeded = Deployment.Request(projectId, staging.Id, Guid.NewGuid(), now.AddMinutes(-10), "corr-1", Guid.NewGuid(), null, "test");
        var cancelled = Deployment.Request(projectId, staging.Id, Guid.NewGuid(), now, "corr-2", Guid.NewGuid(), null, "test");
        db.Environments.Add(staging);
        db.Deployments.AddRange(succeeded, cancelled);
        await db.SaveChangesAsync(ct);
        Assert.DoesNotContain(measurements, item => item.Instrument == "aegisops_deployments_total");

        succeeded.BeginEvaluation();
        succeeded.ApplyDecision(new PolicyOutcome(PolicyDecision.Allow, Guid.NewGuid(), 0, null), now.AddMinutes(-9));
        succeeded.Start(now.AddMinutes(-2));
        succeeded.Succeed(now);
        cancelled.Cancel(now);
        var scan = SecurityScan.Begin(Guid.NewGuid(), ScannerType.Trivy, ScanSource.CiUploaded, ReportFormat.Sarif, now, null, "/reports/x.sarif", parseInline: true);
        scan.Complete(ScanSummary.Empty(), now);
        db.SecurityScans.Add(scan);
        db.AuditEvents.Add(AuditEvent.Record(now, "User", Guid.NewGuid(), "user", "deployment.cancelled", "Deployment", cancelled.Id, "Cancelled", "corr-2"));
        db.AuditEvents.Add(AuditEvent.Record(now, "User", Guid.NewGuid(), "user", "custom", "Thing", null, "free text outcome", null));
        await db.SaveChangesAsync(ct);

        Assert.Single(measurements, item => item.Instrument == "aegisops_deployments_total" && item.Value == 1
            && item.Tags["environment_tier"] == "Staging" && item.Tags["status"] == "Succeeded");
        Assert.Single(measurements, item => item.Instrument == "aegisops_deployments_total" && item.Value == 1
            && item.Tags["environment_tier"] == "Staging" && item.Tags["status"] == "Cancelled");
        Assert.Equal(120, Assert.Single(measurements, item => item.Instrument == "aegisops_deployment_duration_seconds").Value, 3);
        Assert.Equal(600, Assert.Single(measurements, item => item.Instrument == "aegisops_deployment_lead_time_seconds").Value, 3);
        Assert.Single(measurements, item => item.Instrument == "aegisops_scans_total"
            && item.Tags["scanner"] == "Trivy" && item.Tags["status"] == "Completed");
        Assert.Single(measurements, item => item.Instrument == "aegisops_audit_events_total" && item.Tags["outcome"] == "Cancelled");
        // Free-text outcomes collapse to a bounded label.
        Assert.Single(measurements, item => item.Instrument == "aegisops_audit_events_total" && item.Tags["outcome"] == "Other");
        Assert.All(measurements, item => Assert.DoesNotContain(item.Tags.Keys, key => key is "project" or "user" or "correlation_id"));

        // A failed SaveChanges commits nothing and must count nothing.
        measurements.Clear();
        db.AuditEvents.Add(AuditEvent.Record(now, "User", Guid.NewGuid(), "user", "x", "Deployment", null, "Succeeded", null));
        db.DeploymentEvents.Add(DeploymentEvent.Create(succeeded.Id, 99, now, DeploymentEventType.Succeeded, "a"));
        db.DeploymentEvents.Add(DeploymentEvent.Create(succeeded.Id, 99, now, DeploymentEventType.Succeeded, "duplicate sequence"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        Assert.DoesNotContain(measurements, item => item.Instrument == "aegisops_audit_events_total");
    }

    [Fact]
    public void Ai_latency_and_tokens_are_recorded_without_model_labels() {
        var measurements = new ConcurrentQueue<Measurement>();
        using var listener = Listen(measurements);
        AiMetrics.Record(AiRequestStatus.Completed, null, [], SensitiveDataAction.Redact, latencyMs: 1500, promptTokens: 12, completionTokens: 34);
        AiMetrics.Record(AiRequestStatus.Blocked, "SensitiveData", [], SensitiveDataAction.Block, latencyMs: 3);

        var latencies = measurements.Where(item => item.Instrument == "aegisops_ai_request_latency_seconds").ToArray();
        Assert.Contains(latencies, item => item.Value == 1.5 && item.Tags["status"] == "Completed");
        Assert.Contains(latencies, item => item.Value == 0.003 && item.Tags["status"] == "Blocked");
        Assert.Single(measurements, item => item.Instrument == "aegisops_ai_tokens_total" && item.Value == 12 && item.Tags["kind"] == "prompt");
        Assert.Single(measurements, item => item.Instrument == "aegisops_ai_tokens_total" && item.Value == 34 && item.Tags["kind"] == "completion");
        Assert.All(measurements, item => Assert.DoesNotContain("model", item.Tags.Keys));
    }

    private static MeterListener Listen(ConcurrentQueue<Measurement> measurements) {
        var listener = new MeterListener {
            InstrumentPublished = (instrument, current) => {
                if (instrument.Meter.Name is JobMetrics.MeterName or AiMetrics.MeterName) current.EnableMeasurementEvents(instrument);
            },
        };
        void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
            if (value == 0) return; // zero-series seeding from other code paths
            var copy = new Dictionary<string, string?>();
            foreach (var tag in tags) copy[tag.Key] = tag.Value?.ToString();
            measurements.Enqueue(new Measurement(instrument.Name, value, copy));
        }
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        listener.Start();
        return listener;
    }

    private static AegisOpsDbContext Context(SqliteConnection connection) => new(new DbContextOptionsBuilder<AegisOpsDbContext>()
        .UseSqlite(connection).UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteCustomizer>().Options);

    private sealed class SqliteCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<AegisOps.Domain.Identity.ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<AegisOps.Domain.Policy.Policy>().Ignore(policy => policy.AppliesToTiers);
        }
    }
}
