using AegisOps.Domain.Audit;
using AegisOps.Domain.Jobs;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Jobs;

public static class AdminJobs {
    public static async Task<IResult> Summary(AegisOpsDbContext db, HttpContext http) {
        var counts = await db.Jobs.AsNoTracking()
            .GroupBy(item => item.Status)
            .Select(group => new { status = group.Key.ToString(), count = group.Count() })
            .ToListAsync(http.RequestAborted);
        return Results.Ok(new {
            pending = counts.SingleOrDefault(item => item.status == nameof(JobStatus.Pending))?.count ?? 0,
            running = counts.SingleOrDefault(item => item.status == nameof(JobStatus.Running))?.count ?? 0,
            succeeded = counts.SingleOrDefault(item => item.status == nameof(JobStatus.Succeeded))?.count ?? 0,
            deadLettered = counts.SingleOrDefault(item => item.status == nameof(JobStatus.DeadLettered))?.count ?? 0,
        });
    }

    public static async Task<IResult> List(AegisOpsDbContext db, HttpContext http, string? status = null) {
        var query = db.Jobs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<JobStatus>(status, true, out var parsed)) {
            query = query.Where(item => item.Status == parsed);
        }

        var items = await query
            .OrderByDescending(item => item.CreatedAt)
            .Take(100)
            .Select(item => new {
                item.Id,
                type = item.Type.ToString(),
                status = item.Status.ToString(),
                item.Attempts,
                item.MaxAttempts,
                item.ScheduledAt,
                item.LastError,
                item.CorrelationId,
                item.CreatedAt,
            })
            .ToListAsync(http.RequestAborted);
        return Results.Ok(items);
    }

    public static async Task<IResult> Retry(Guid id, AegisOpsDbContext db, TimeProvider time, HttpContext http) {
        var job = await db.Jobs.SingleOrDefaultAsync(item => item.Id == id, http.RequestAborted);
        if (job is null) {
            return Results.Problem(title: "Job was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        try {
            job.Retry(time.GetUtcNow());
        } catch (InvalidOperationException exception) {
            return Results.Problem(title: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }

        db.AuditEvents.Add(AuditEvent.Record(
            time.GetUtcNow(),
            "User",
            null,
            "admin",
            "job.retried",
            "Job",
            job.Id,
            "Pending",
            job.CorrelationId));
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }
}
