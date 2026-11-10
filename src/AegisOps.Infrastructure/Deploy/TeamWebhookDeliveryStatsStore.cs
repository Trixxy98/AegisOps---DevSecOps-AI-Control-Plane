using System.Text.Json;
using AegisOps.Infrastructure.Observability;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Infrastructure.Deploy;

/// <summary>
/// Durable, all-time per-team webhook counters (org.team_webhook_delivery_stats). Each call is one
/// atomic INSERT ... ON CONFLICT DO UPDATE executed inside the caller's transaction, so the counter
/// commits (or rolls back) together with the attempt and job result, and concurrent workers finishing
/// jobs for the same team cannot lose increments. Counters are independent of jobs, so the
/// JobCleanupService purge of Succeeded jobs does not change them.
/// </summary>
public static class TeamWebhookDeliveryStatsStore {
    /// <summary>Returns the event id of a SendNotification payload, or null when it is malformed.</summary>
    public static Guid? TryReadEventId(string payload) {
        try {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("eventId", out var value) && value.TryGetGuid(out var id) ? id : null;
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>
    /// Increments the counters of the team that owns <paramref name="eventId"/>. Returns false when the
    /// event (or its deployment/project) no longer exists, in which case nothing is counted.
    /// </summary>
    public static async Task<bool> RecordAsync(
        AegisOpsDbContext db, Guid eventId, WebhookDeliveryOutcome outcome, DateTimeOffset at, CancellationToken ct
    ) {
        var teamId = await (from item in db.DeploymentEvents.AsNoTracking()
                            join deployment in db.Deployments.AsNoTracking() on item.DeploymentId equals deployment.Id
                            join project in db.Projects.AsNoTracking() on deployment.ProjectId equals project.Id
                            where item.Id == eventId
                            select (Guid?)project.TeamId).FirstOrDefaultAsync(ct);
        if (teamId is null) return false;

        // Only the PostgreSQL table is schema-qualified; SQLite (tests) ignores EF schemas.
        var table = db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true
            ? "org.team_webhook_delivery_stats"
            : "team_webhook_delivery_stats";
        var sql = outcome == WebhookDeliveryOutcome.Succeeded
            ? $$"""
               INSERT INTO {{table}} AS s (team_id, delivered, failed_attempts, dead_lettered, last_success_at, updated_at)
               VALUES ({0}, 1, 0, 0, {1}, {1})
               ON CONFLICT (team_id) DO UPDATE SET delivered = s.delivered + 1,
                   last_success_at = excluded.last_success_at, updated_at = excluded.updated_at
               """
            : $$"""
               INSERT INTO {{table}} AS s (team_id, delivered, failed_attempts, dead_lettered, last_failure_at, updated_at)
               VALUES ({0}, 0, 1, {2}, {1}, {1})
               ON CONFLICT (team_id) DO UPDATE SET failed_attempts = s.failed_attempts + 1,
                   dead_lettered = s.dead_lettered + excluded.dead_lettered,
                   last_failure_at = excluded.last_failure_at, updated_at = excluded.updated_at
               """;
        object[] parameters = outcome == WebhookDeliveryOutcome.Succeeded
            ? [teamId.Value, at.ToUniversalTime()]
            : [teamId.Value, at.ToUniversalTime(), outcome == WebhookDeliveryOutcome.DeadLettered ? 1L : 0L];
        await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
        return true;
    }
}
