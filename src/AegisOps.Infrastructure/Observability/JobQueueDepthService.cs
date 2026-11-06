using System.Data.Common;
using System.Globalization;
using AegisOps.Domain.Jobs;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AegisOps.Infrastructure.Observability;

public sealed partial class JobQueueDepthService(IServiceScopeFactory scopes, TimeProvider time, ILogger<JobQueueDepthService> logger) : BackgroundService {
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Job queue depth poll failed ({ErrorType}, {SafeDetail}).")]
    private static partial void LogPollFailure(ILogger logger, string errorType, string safeDetail);

    internal static string SafeDetail(Exception exception) => exception switch {
        PostgresException postgres => postgres.SqlState,
        { InnerException: { } inner } => inner.GetType().Name,
        DbException db => db.ErrorCode.ToString(CultureInfo.InvariantCulture),
        _ => "none",
    };

    public async Task PollAsync(CancellationToken cancellationToken) {
        try {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            var now = time.GetUtcNow();
            var counts = await db.Jobs.AsNoTracking()
                .Where(job => job.Status == JobStatus.Pending || job.Status == JobStatus.Running || job.Status == JobStatus.DeadLettered)
                .GroupBy(job => new { job.Status, Due = job.Status == JobStatus.Pending && job.ScheduledAt <= now })
                .Select(group => new { group.Key.Status, group.Key.Due, Count = group.LongCount() })
                .ToListAsync(cancellationToken);
            JobMetrics.SetQueueDepth(
                counts.SingleOrDefault(item => item.Status == JobStatus.Pending && item.Due)?.Count ?? 0,
                counts.SingleOrDefault(item => item.Status == JobStatus.Pending && !item.Due)?.Count ?? 0,
                counts.SingleOrDefault(item => item.Status == JobStatus.Running)?.Count ?? 0,
                counts.SingleOrDefault(item => item.Status == JobStatus.DeadLettered)?.Count ?? 0);
        } catch {
            JobMetrics.ClearQueueDepth();
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try {
            while (!stoppingToken.IsCancellationRequested) {
                try {
                    await PollAsync(stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                } catch (Exception exception) {
                    LogPollFailure(logger, exception.GetType().Name, SafeDetail(exception));
                }
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Normal shutdown.
        } finally {
            JobMetrics.ClearQueueDepth();
        }
    }
}
