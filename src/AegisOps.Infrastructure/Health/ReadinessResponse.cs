using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AegisOps.Infrastructure.Health;

public static class ReadinessResponse {
    public static Task WriteAsync(HttpContext context, HealthReport report) {
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(context.Response.Body, new {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(entry => entry.Key, entry => new {
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
            }),
        }, cancellationToken: context.RequestAborted);
    }
}
