using AegisOps.Infrastructure.Observability;
using OpenTelemetry.Metrics;

namespace AegisOps.Worker.Observability;

// Start before the job processor so the first committed attempt has a scrape baseline.
public sealed class JobMetricsSeedService : IHostedService {
    public JobMetricsSeedService(MeterProvider provider) => _ = provider;

    public Task StartAsync(CancellationToken cancellationToken) {
        JobMetrics.InitializeZeroSeries();
        DomainMetrics.InitializeZeroSeries();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
