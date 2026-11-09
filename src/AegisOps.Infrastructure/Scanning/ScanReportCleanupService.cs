using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Observability;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AegisOps.Infrastructure.Scanning;

/// <summary>
/// Removes expired raw scan reports from <c>Storage:ReportsPath</c> (docs/07-security-scanning.md).
/// A report expires when its finished scan is older than <c>Storage:ReportRetentionDays</c>
/// (default 90) or when no scan references it any more (replaced uploads, rejected SARIF).
/// Findings and scan summaries are kept; only the raw file and its path are removed.
/// </summary>
public sealed partial class ScanReportCleanupService(
    IServiceScopeFactory scopes,
    TimeProvider time,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ScanReportCleanupService> logger
) : BackgroundService {
    public const int DefaultRetentionDays = 90;
    // Uploads write the file before the scan row commits; never treat a fresh file as orphaned.
    internal static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(1);
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Scan report cleanup expired {Expired} scan report(s) and deleted {Deleted} file(s).")]
    private static partial void LogCleaned(ILogger logger, int expired, int deleted);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Scan report cleanup failed ({ErrorType}, {SafeDetail}).")]
    private static partial void LogFailure(ILogger logger, string errorType, string safeDetail);

    internal string ReportsRoot {
        get {
            var root = configuration["Storage:ReportsPath"];
            return string.IsNullOrWhiteSpace(root) ? Path.Combine(environment.ContentRootPath, "reports") : root;
        }
    }

    internal TimeSpan Retention {
        get {
            var days = configuration.GetValue<int?>("Storage:ReportRetentionDays") ?? DefaultRetentionDays;
            return TimeSpan.FromDays(Math.Max(1, days));
        }
    }

    public async Task<(int Expired, int Deleted)> CleanupAsync(CancellationToken cancellationToken) {
        var now = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();

        // 1. Detach old finished scans from their files. Queued/Running scans still need the report.
        var cutoff = now - Retention;
        var expired = await db.SecurityScans
            .Where(scan => scan.RawReportPath != null && scan.CompletedAt != null && scan.CompletedAt < cutoff &&
                (scan.Status == ScanStatus.Completed || scan.Status == ScanStatus.Failed))
            .ExecuteUpdateAsync(setters => setters.SetProperty(scan => scan.RawReportPath, (string?)null), cancellationToken);

        // 2. Delete report files that no scan references. Clearing the path first means a failed
        //    delete is simply retried on the next run.
        var deleted = 0;
        var root = ReportsRoot;
        if (Directory.Exists(root)) {
            var orphanBefore = (now - OrphanGrace).UtcDateTime;
            foreach (var directory in Directory.EnumerateDirectories(root)) {
                cancellationToken.ThrowIfCancellationRequested();
                // Only the API's own layout: {root}/{artifactId:N}/{scanner}-{timestamp}.sarif.
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var artifactId)) continue;
                var candidates = Directory.EnumerateFiles(directory, "*.sarif", SearchOption.TopDirectoryOnly)
                    .Where(file => File.GetLastWriteTimeUtc(file) < orphanBefore)
                    .ToArray();
                if (candidates.Length == 0) continue;
                // Compare by file name within the artifact: API and Worker may spell the root differently.
                var referenced = (await db.SecurityScans.AsNoTracking()
                        .Where(scan => scan.ArtifactId == artifactId && scan.RawReportPath != null)
                        .Select(scan => scan.RawReportPath!)
                        .ToListAsync(cancellationToken))
                    .Select(Path.GetFileName)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var file in candidates) {
                    if (referenced.Contains(Path.GetFileName(file))) continue;
                    File.Delete(file);
                    deleted++;
                }
            }
        }

        if (expired > 0 || deleted > 0) LogCleaned(logger, expired, deleted);
        return (expired, deleted);
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
