using System.Diagnostics.Metrics;
using AegisOps.Domain.Jobs;
using AegisOps.Infrastructure.Observability;

namespace AegisOps.Api.Tests;

[CollectionDefinition("WorkerMetrics", DisableParallelization = true)]
public sealed class WorkerMetricsGroup { }

[Collection("WorkerMetrics")]
public sealed class JobMetricsTests {
    [Fact]
    public void Record_emits_bounded_type_status_labels_and_duration() {
        var measurements = new List<(string Name, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener {
            InstrumentPublished = (instrument, current) => {
                if (instrument.Meter.Name == JobMetrics.MeterName) current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, CopyTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, CopyTags(tags))));
        listener.Start();

        JobMetrics.Record(JobType.EvaluateDeployment, JobStatus.Succeeded, 0.25);

        var count = Assert.Single(measurements, item => item.Name == "aegisops_jobs_processed_total");
        Assert.Equal(1, count.Value);
        Assert.Equal("EvaluateDeployment", count.Tags["type"]);
        Assert.Equal("Succeeded", count.Tags["status"]);
        Assert.Equal(2, count.Tags.Count);
        var duration = Assert.Single(measurements, item => item.Name == "aegisops_jobs_duration_seconds");
        Assert.Equal(0.25, duration.Value);
        Assert.Equal("EvaluateDeployment", duration.Tags["type"]);
        Assert.Single(duration.Tags);
    }

    [Fact]
    public void Lease_expiry_uses_separate_counter_without_handler_duration() {
        var counts = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener {
            InstrumentPublished = (instrument, current) => {
                if (instrument.Meter.Name == JobMetrics.MeterName) current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
            if (instrument.Name == "aegisops_jobs_lease_expired_total") counts.Add((value, CopyTags(tags)));
        });
        listener.Start();

        JobMetrics.RecordLeaseExpired(JobType.ExecuteDeployment, JobStatus.DeadLettered);

        var count = Assert.Single(counts);
        Assert.Equal(1, count.Value);
        Assert.Equal("ExecuteDeployment", count.Tags["type"]);
        Assert.Equal("DeadLettered", count.Tags["status"]);
        Assert.Equal(2, count.Tags.Count);
    }

    [Fact]
    public void Duration_buckets_are_in_seconds_and_cover_short_and_long_jobs() {
        var buckets = JobMetrics.DurationBuckets;
        Assert.Contains(0.05, buckets);
        Assert.Contains(0.5, buckets);
        Assert.Contains(5, buckets);
        Assert.Contains(60, buckets);
        Assert.True(buckets.SequenceEqual(buckets.Order()));
    }

    [Fact]
    public void Queue_depth_gauge_reports_all_statuses_including_zeros() {
        var observed = new Dictionary<string, long>();
        using var listener = new MeterListener {
            InstrumentPublished = (instrument, current) => {
                if (instrument.Meter.Name == JobMetrics.MeterName && instrument.Name == "aegisops_jobs_queue_depth")
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
            foreach (var tag in tags)
                if (tag.Key == "status") observed.Add((string)tag.Value!, value);
        });
        listener.Start();
        try {
            JobMetrics.SetQueueDepth(2, 3, 0, 4);
            listener.RecordObservableInstruments();
            Assert.Equal(2, observed["Pending"]);
            Assert.Equal(3, observed["Delayed"]);
            Assert.Equal(0, observed["Running"]);
            Assert.Equal(4, observed["DeadLettered"]);
            Assert.Equal(4, observed.Count);
        } finally {
            JobMetrics.ClearQueueDepth();
        }
    }

    private static Dictionary<string, object?> CopyTags(ReadOnlySpan<KeyValuePair<string, object?>> tags) {
        var result = new Dictionary<string, object?>();
        foreach (var tag in tags) result.Add(tag.Key, tag.Value);
        return result;
    }
}
