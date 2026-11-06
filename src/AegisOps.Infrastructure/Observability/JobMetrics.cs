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
    private static Measurement<long>[] _queueDepth = [];
    private static readonly ObservableGauge<long> QueueDepth = Meter.CreateObservableGauge(
        "aegisops_jobs_queue_depth", () => Volatile.Read(ref _queueDepth));
    public static IReadOnlyList<double> DurationBuckets { get; } = Array.AsReadOnly<double>([0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300]);

    public static void Record(JobType type, JobStatus status, double durationSeconds) {
        var tags = new TagList {
            { "type", type.ToString() },
            { "status", status.ToString() },
        };
        Processed.Add(1, tags);
        Duration.Record(durationSeconds, new TagList { { "type", type.ToString() } });
    }

    public static void SetQueueDepth(long pending, long delayed, long running, long deadLettered) {
        Interlocked.Exchange(ref _queueDepth, [
            new Measurement<long>(pending, new KeyValuePair<string, object?>("status", JobStatus.Pending.ToString())),
            new Measurement<long>(delayed, new KeyValuePair<string, object?>("status", "Delayed")),
            new Measurement<long>(running, new KeyValuePair<string, object?>("status", JobStatus.Running.ToString())),
            new Measurement<long>(deadLettered, new KeyValuePair<string, object?>("status", JobStatus.DeadLettered.ToString())),
        ]);
    }

    public static void ClearQueueDepth() => Interlocked.Exchange(ref _queueDepth, []);

    public static void RecordLeaseExpired(JobType type, JobStatus status) {
        LeaseExpired.Add(1, new TagList {
            { "type", type.ToString() },
            { "status", status.ToString() },
        });
    }
}
