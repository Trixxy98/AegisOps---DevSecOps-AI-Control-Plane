using AegisOps.Infrastructure.Observability;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AegisOps.Infrastructure.Ai;

/// <summary>
/// Deletes stored AI prompt/response content (only present when a policy enabled
/// <c>StoreContent</c>) after <c>Ai:ContentRetentionDays</c> (default 30, docs/17).
/// The request row, prompt hash, redacted previews, detections and token counts are kept
/// for governance; only the full content columns are cleared.
/// </summary>
public sealed partial class AiContentRetentionService(
    IServiceScopeFactory scopes,
    TimeProvider time,
    IConfiguration configuration,
    ILogger<AiContentRetentionService> logger
) : BackgroundService {
    public const int DefaultRetentionDays = 30;
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "AI content retention cleared stored content from {Count} request(s) created before {Cutoff}.")]
    private static partial void LogCleared(ILogger logger, int count, DateTimeOffset cutoff);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "AI content retention failed ({ErrorType}, {SafeDetail}).")]
    private static partial void LogFailure(ILogger logger, string errorType, string safeDetail);

    internal TimeSpan Retention =>
        TimeSpan.FromDays(Math.Max(1, configuration.GetValue<int?>("Ai:ContentRetentionDays") ?? DefaultRetentionDays));

    public async Task<int> CleanupAsync(CancellationToken cancellationToken) {
        var cutoff = time.GetUtcNow() - Retention;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
        var cleared = await db.AiRequests
            .Where(request => request.CreatedAt < cutoff && (request.PromptContent != null || request.ResponseContent != null))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(request => request.PromptContent, (string?)null)
                .SetProperty(request => request.ResponseContent, (string?)null), cancellationToken);
        if (cleared > 0) LogCleared(logger, cleared, cutoff);
        return cleared;
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
