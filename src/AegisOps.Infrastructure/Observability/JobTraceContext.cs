using System.Diagnostics;
using System.Text.Json;
using AegisOps.Application.Observability;
using AegisOps.Domain.Jobs;

namespace AegisOps.Infrastructure.Observability;

public static class JobTraceContext {
    // Persist only trace context and the already-existing opaque correlation key; never a request body.
    public static string DeploymentPayload(Guid deploymentId, string correlationId) => JsonSerializer.Serialize(new {
        deploymentId, correlationId, traceparent = Activity.Current?.Id,
    });

    public static string ScanPayload(Guid scanId) => JsonSerializer.Serialize(new {
        scanId, traceparent = Activity.Current?.Id,
    });

    public static Activity? StartJob(Job job) {
        ActivityLink[] links = [];
        string? correlationId = null;
        try {
            using var document = JsonDocument.Parse(job.Payload);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object) {
                if (root.TryGetProperty("traceparent", out var trace) && trace.ValueKind == JsonValueKind.String &&
                    ActivityContext.TryParse(trace.GetString(), null, out var context) && context.TraceId != default && context.SpanId != default)
                    links = [new ActivityLink(context)];
                if (root.TryGetProperty("correlationId", out var correlation) && correlation.ValueKind == JsonValueKind.String)
                    correlationId = correlation.GetString();
            }
        } catch (JsonException) {
            // Legacy or malformed payloads still follow the existing job failure path.
        }
        var activity = AegisOpsTracing.Source.StartActivity(job.Type.ToString(), ActivityKind.Consumer,
            default(ActivityContext), links: links);
        if (activity is not null) {
            activity.SetTag("job.type", job.Type.ToString());
            // Do not trust arbitrary legacy payload values as exported correlation identifiers.
            if (AegisOpsTracing.IsSafeCorrelation(correlationId) && correlationId == job.CorrelationId) {
                activity.SetTag("correlationId", correlationId);
                activity.AddBaggage("correlationId", correlationId);
            }
        }
        return activity;
    }

}
