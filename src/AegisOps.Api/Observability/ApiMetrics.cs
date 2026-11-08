using Microsoft.AspNetCore.Http.Features;
using OpenTelemetry.Metrics;
using AegisOps.Infrastructure.Observability;

namespace AegisOps.Api.Observability;

public static class ApiMetrics
{
    public const int ScrapePort = 9464;

    public static IServiceCollection AddApiMetrics(this IServiceCollection services)
    {
        services.AddOpenTelemetry().WithMetrics(metrics => metrics
            .AddMeter("Microsoft.AspNetCore.Hosting")
            .AddMeter(AiMetrics.MeterName)
            .AddPrometheusExporter());
        return services;
    }

    public static WebApplication UseApiMetrics(this WebApplication app)
    {
        // Start the meter provider before seeding: Add(0) is lost without a listener.
        app.Lifetime.ApplicationStarted.Register(() => {
            _ = app.Services.GetRequiredService<MeterProvider>();
            AiMetrics.InitializeZeroSeries();
        });
        app.Use((context, next) => {
            var isScrapePath = context.Request.Path == "/metrics";
            if (isScrapePath) {
                // Scrapes must not generate their own HTTP request measurements.
                var feature = context.Features.Get<IHttpMetricsTagsFeature>();
                if (feature is not null) feature.MetricsDisabled = true;
            }

            // The management listener must not expose the rest of the API.
            if (context.Connection.LocalPort == ScrapePort && !isScrapePath) {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return next(context);
        });
        app.UseOpenTelemetryPrometheusScrapingEndpoint(context =>
            context.Connection.LocalPort == ScrapePort && context.Request.Path == "/metrics");
        return app;
    }
}
