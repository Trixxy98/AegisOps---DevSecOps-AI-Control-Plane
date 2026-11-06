using System.Diagnostics;
using System.Diagnostics.Metrics;
using AegisOps.Domain.Jobs;

namespace AegisOps.Infrastructure.Observability;

public static class JobMetrics {
    public const string MeterName = "AegisOps";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Processed = Meter.CreateCounter<long>("aegisops_jobs_processed_total");
    private static readonly Counter<long> LeaseExpired = Meter.CreateCounter<long>("aegisops_jobs_lease_expired_total");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("aegisops_jobs_duration_seconds", unit: "s");
    public static IReadOnlyList<double> DurationBuckets { get; } = Array.AsReadOnly<double>([0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300]);

    public static void Record(JobType type, JobStatus status, double durationSeconds) {
        var tags = new TagList {
            { "type", type.ToString() },
            { "status", status.ToString() },
        };
        Processed.Add(1, tags);
        Duration.Record(durationSeconds, new TagList { { "type", type.ToString() } });
    }

    public static void RecordLeaseExpired(JobType type, JobStatus status) {
        LeaseExpired.Add(1, new TagList {
            { "type", type.ToString() },
            { "status", status.ToString() },
        });
    }
}
