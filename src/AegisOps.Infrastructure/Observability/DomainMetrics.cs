using System.Diagnostics;
using System.Diagnostics.Metrics;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace AegisOps.Infrastructure.Observability;

/// <summary>
/// Deployment, scan and audit metrics on <c>Meter("AegisOps")</c> (docs/15 §2). They are
/// recorded by <see cref="AegisOpsDbContext"/> only after a successful commit, so rolled-back
/// transitions are never counted, and both API and Worker report through one code path.
/// All labels are enums or fixed sets: no project names, user ids, image names or errors.
/// </summary>
public static class DomainMetrics {
    private static readonly Meter Meter = new(JobMetrics.MeterName);
    private static readonly Counter<long> Deployments = Meter.CreateCounter<long>("aegisops_deployments_total",
        description: "Deployments reaching a terminal status.");
    private static readonly Histogram<double> DeploymentDuration = Meter.CreateHistogram<double>("aegisops_deployment_duration_seconds",
        unit: "s", description: "Deploying to Succeeded/Failed.");
    private static readonly Histogram<double> DeploymentLeadTime = Meter.CreateHistogram<double>("aegisops_deployment_lead_time_seconds",
        unit: "s", description: "Requested to Succeeded, including approval wait.");
    private static readonly Counter<long> PolicyEvaluations = Meter.CreateCounter<long>("aegisops_policy_evaluations_total",
        description: "Worker deployment policy decisions.");
    private static readonly Counter<long> Scans = Meter.CreateCounter<long>("aegisops_scans_total",
        description: "Scan reports reaching Completed or Failed.");
    private static readonly Histogram<double> ScanIngestDuration = Meter.CreateHistogram<double>("aegisops_scan_ingest_duration_seconds",
        unit: "s", description: "SARIF parse and normalisation time.");
    private static readonly Counter<long> AuditEvents = Meter.CreateCounter<long>("aegisops_audit_events_total",
        description: "Committed audit events by outcome.");

    public static readonly IReadOnlyList<double> DurationBuckets = [1, 5, 15, 30, 60, 120, 300, 900, 3600, 14400, 86400];

    private static readonly HashSet<string> KnownAuditOutcomes = new(StringComparer.Ordinal) {
        "Succeeded", "Accepted", "Denied", "Failed", "Cancelled", "Approve", "Reject", "Approved", "Rejected",
        "Allow", "Deny", "RequireApproval",
    };

    private static readonly DeploymentStatus[] Terminal =
        [DeploymentStatus.Succeeded, DeploymentStatus.Failed, DeploymentStatus.Denied, DeploymentStatus.Rejected, DeploymentStatus.Cancelled];

    /// <summary>Seed zero series so the first event after start-up is visible to rate()/increase().</summary>
    public static void InitializeZeroSeries() {
        foreach (var tier in Enum.GetValues<EnvironmentTier>())
            foreach (var status in Terminal)
                Deployments.Add(0, new TagList { { "environment_tier", tier.ToString() }, { "status", status.ToString() } });
        foreach (var scanner in Enum.GetValues<ScannerType>())
            foreach (var status in new[] { ScanStatus.Completed, ScanStatus.Failed })
                Scans.Add(0, new TagList { { "scanner", scanner.ToString() }, { "status", status.ToString() } });
        foreach (var outcome in KnownAuditOutcomes.Append("Other"))
            AuditEvents.Add(0, new TagList { { "outcome", outcome } });
    }

    public static void RecordPolicyEvaluation(PolicyDecision decision) =>
        PolicyEvaluations.Add(1, new TagList { { "decision", decision.ToString() } });

    public static void RecordScanIngest(ScannerType scanner, bool inline, TimeSpan elapsed) =>
        ScanIngestDuration.Record(elapsed.TotalSeconds, new TagList { { "scanner", scanner.ToString() }, { "mode", inline ? "Inline" : "Queued" } });

    /// <summary>Snapshot of metric-relevant changes taken before SaveChanges.</summary>
    internal sealed record PendingChanges(
        (Guid EnvironmentId, DeploymentStatus Status, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt)[] Deployments,
        (ScannerType Scanner, ScanStatus Status)[] Scans,
        string[] AuditOutcomes) {
        public bool IsEmpty => Deployments.Length == 0 && Scans.Length == 0 && AuditOutcomes.Length == 0;
    }

    internal static PendingChanges Capture(ChangeTracker tracker) {
        var deployments = tracker.Entries<Deployment>()
            .Where(entry => Terminal.Contains(entry.Entity.Status) && (entry.State == EntityState.Added ||
                (entry.State == EntityState.Modified && !Equals(entry.Property(item => item.Status).OriginalValue, entry.Entity.Status))))
            .Select(entry => (entry.Entity.EnvironmentId, entry.Entity.Status, entry.Entity.RequestedAt, entry.Entity.StartedAt, entry.Entity.CompletedAt))
            .ToArray();
        var scans = tracker.Entries<SecurityScan>()
            .Where(entry => entry.Entity.Status is ScanStatus.Completed or ScanStatus.Failed && (entry.State == EntityState.Added ||
                (entry.State == EntityState.Modified && !Equals(entry.Property(item => item.Status).OriginalValue, entry.Entity.Status))))
            .Select(entry => (entry.Entity.Scanner, entry.Entity.Status))
            .ToArray();
        var audits = tracker.Entries<AuditEvent>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity.Outcome)
            .ToArray();
        return new PendingChanges(deployments, scans, audits);
    }

    internal static async Task RecordCommittedAsync(AegisOpsDbContext db, PendingChanges changes, CancellationToken cancellationToken) {
        foreach (var outcome in changes.AuditOutcomes)
            AuditEvents.Add(1, new TagList { { "outcome", KnownAuditOutcomes.Contains(outcome) ? outcome : "Other" } });
        foreach (var (scanner, status) in changes.Scans)
            Scans.Add(1, new TagList { { "scanner", scanner.ToString() }, { "status", status.ToString() } });
        if (changes.Deployments.Length == 0) return;

        var environmentIds = changes.Deployments.Select(item => item.EnvironmentId).Distinct().ToArray();
        Dictionary<Guid, EnvironmentTier> tiers;
        try {
            tiers = await db.Environments.AsNoTracking().Where(item => environmentIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => item.Tier, cancellationToken);
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            // The business transaction already committed; never fail it because of telemetry.
            tiers = [];
        }

        foreach (var deployment in changes.Deployments) {
            var tier = tiers.TryGetValue(deployment.EnvironmentId, out var value) ? value.ToString() : "Unknown";
            var tags = new TagList { { "environment_tier", tier }, { "status", deployment.Status.ToString() } };
            Deployments.Add(1, tags);
            if (deployment.CompletedAt is not { } completed) continue;
            var tierTag = new TagList { { "environment_tier", tier } };
            if (deployment.StartedAt is { } started && deployment.Status is DeploymentStatus.Succeeded or DeploymentStatus.Failed)
                DeploymentDuration.Record(Math.Max(0, (completed - started).TotalSeconds), tags);
            if (deployment.Status == DeploymentStatus.Succeeded)
                DeploymentLeadTime.Record(Math.Max(0, (completed - deployment.RequestedAt).TotalSeconds), tierTag);
        }
    }
}
