using AegisOps.Domain.Jobs;
using AegisOps.Infrastructure.Observability;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AegisOps.Infrastructure.Deploy;

/// <summary>
/// Retention for the durable job table (docs/11-database-design.md): Succeeded jobs are deleted
/// seven days after completion; DeadLettered jobs are kept until an Admin retries them, and
/// Pending/Running jobs are never touched. Webhook delivery attempts cascade with their job.
/// </summary>
public sealed partial class JobCleanupService(IServiceScopeFactory scopes, TimeProvider time, ILogger<JobCleanupService> logger) : BackgroundService {
    public static readonly TimeSpan SucceededRetention = TimeSpan.FromDays(7);
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    internal const int BatchSize = 500;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Job cleanup deleted {Count} succeeded job(s) completed before {Cutoff}.")]
    private static partial void LogDeleted(ILogger logger, int count, DateTimeOffset cutoff);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Job cleanup failed ({ErrorType}, {SafeDetail}).")]
    private static partial void LogFailure(ILogger logger, string errorType, string safeDetail);

    public async Task<int> CleanupAsync(CancellationToken cancellationToken) {
        var cutoff = time.GetUtcNow() - SucceededRetention;
        var deleted = 0;
        while (true) {
            // Short batches keep each delete transaction small; replicas racing on the same ids are harmless.
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            var ids = await db.Jobs.AsNoTracking()
                .Where(job => job.Status == JobStatus.Succeeded && job.CompletedAt != null && job.CompletedAt < cutoff)
                .Select(job => job.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0) break;
            deleted += await db.Jobs
                .Where(job => ids.Contains(job.Id) && job.Status == JobStatus.Succeeded)
                .ExecuteDeleteAsync(cancellationToken);
            if (ids.Count < BatchSize) break;
        }
        if (deleted > 0) LogDeleted(logger, deleted, cutoff);
        return deleted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(Interval);
        try {
            do {
                try {
                    await CleanupAsync(stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                } catch (Exception exception) {
                    LogFailure(logger, exception.GetType().Name, JobQueueDepthService.SafeDetail(exception));
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Normal shutdown.
        }
    }
}
