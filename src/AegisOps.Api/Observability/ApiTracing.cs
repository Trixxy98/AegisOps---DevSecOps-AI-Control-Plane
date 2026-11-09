using System.Diagnostics;
using AegisOps.Application.Observability;

namespace AegisOps.Api.Observability;

public static class ApiTracing
{
    public static WebApplication UseApiRequestTracing(this WebApplication app)
    {
        app.Use(async (context, next) => {
            if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
            // ASP.NET's own request Activity is not exported (its source is not registered), so a
            // child of it would point at a span no backend ever receives. Parent Api.Request on the
            // caller's remote W3C context instead (or make it a root), and do not inherit baggage
            // the caller put on the ambient Activity. The change is scoped to this async flow.
            var ambient = Activity.Current;
            ActivityContext.TryParse(ambient?.ParentId, ambient?.TraceStateString, isRemote: true, out var remote);
            Activity.Current = null;
            using var activity = AegisOpsTracing.Source.StartActivity("Api.Request", ActivityKind.Server, remote);
            var header = context.Request.Headers["X-Correlation-Id"];
            context.Items["correlationId"] = AegisOpsTracing.SetCorrelation(activity, header.Count == 1 ? header[0] : null);
            await next(context);
        });
        return app;
    }
}
