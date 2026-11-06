using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AegisOps.Infrastructure.Health;

public sealed class WorkerPollHeartbeat(TimeProvider time) {
    private long _lastSuccessfulPollUtcTicks;

    public void MarkSuccessfulPoll() => Interlocked.Exchange(ref _lastSuccessfulPollUtcTicks, time.GetUtcNow().UtcTicks);

    public bool IsFresh() {
        var last = Interlocked.Read(ref _lastSuccessfulPollUtcTicks);
        return last != 0 && time.GetUtcNow() - new DateTimeOffset(last, TimeSpan.Zero) < TimeSpan.FromSeconds(30);
    }
}

public sealed class WorkerPollReadinessCheck(WorkerPollHeartbeat heartbeat) : IHealthCheck {
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(heartbeat.IsFresh()
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Worker has not completed a job poll in the last 30 seconds."));
}
