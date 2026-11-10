using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Serilog;
using Serilog.AspNetCore;
using Serilog.Events;

namespace AegisOps.Api.Observability;

/// <summary>
/// One bounded Serilog completion event per request: method, route template (never the raw path or query string),
/// status, elapsed ms, CorrelationId and principal type/id. Headers, bodies and query strings are never read.
/// </summary>
public static class ApiRequestLogging {
    public const string MessageTemplate = "HTTP {RequestMethod} {RouteTemplate} responded {StatusCode} in {Elapsed:0.0000} ms";
    public const string UnmatchedRoute = "(unmatched)";

    /// <summary>Call after <c>UseApiRequestTracing</c> so the completion event carries the Api.Request trace and CorrelationId.</summary>
    public static WebApplication UseApiRequestLogging(this WebApplication app) {
        ArgumentNullException.ThrowIfNull(app);
        var logger = app.Services.GetRequiredService<Serilog.ILogger>();
        app.UseSerilogRequestLogging(options => Configure(options, logger));
        return app;
    }

    internal static void Configure(RequestLoggingOptions options, Serilog.ILogger logger) {
        options.Logger = logger;
        options.IncludeQueryInRequestPath = false;
        options.MessageTemplate = MessageTemplate;
        options.GetMessageTemplateProperties = static (context, _, elapsed, status) => [
            new LogEventProperty("RequestMethod", new ScalarValue(Method(context.Request.Method))),
            new LogEventProperty("RouteTemplate", new ScalarValue(RouteTemplate(context))),
            new LogEventProperty("StatusCode", new ScalarValue(status)),
            new LogEventProperty("Elapsed", new ScalarValue(elapsed)),
        ];
        options.GetLevel = static (context, _, exception) =>
            IsProbe(context.Request.Path) ? LogEventLevel.Verbose
            : exception is not null || context.Response.StatusCode >= StatusCodes.Status500InternalServerError ? LogEventLevel.Error
            : LogEventLevel.Information;
        options.EnrichDiagnosticContext = static (diagnostics, context) => {
            if (context.Items["correlationId"] is string correlation) diagnostics.Set("CorrelationId", correlation);
            var (type, userId, apiKeyId) = Principal(context.User);
            diagnostics.Set("PrincipalType", type);
            if (userId is not null) diagnostics.Set("UserId", userId);
            if (apiKeyId is not null) diagnostics.Set("ApiKeyId", apiKeyId);
        };
    }

    /// <summary>Health and Prometheus scrapes are logged at Verbose, i.e. dropped at the default Information level.</summary>
    public static bool IsProbe(PathString path) => path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics");

    internal static string RouteTemplate(HttpContext context) =>
        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? UnmatchedRoute;

    private static string Method(string method) =>
        HttpMethods.IsGet(method) ? HttpMethods.Get : HttpMethods.IsPost(method) ? HttpMethods.Post
        : HttpMethods.IsPut(method) ? HttpMethods.Put : HttpMethods.IsDelete(method) ? HttpMethods.Delete
        : HttpMethods.IsPatch(method) ? HttpMethods.Patch : HttpMethods.IsHead(method) ? HttpMethods.Head
        : HttpMethods.IsOptions(method) ? HttpMethods.Options : "OTHER";

    // Only opaque ids: subjects are user GUIDs or "apiKey:{guid}"; names and emails are never read.
    private static (string Type, string? UserId, string? ApiKeyId) Principal(ClaimsPrincipal user) {
        if (user.Identity?.IsAuthenticated != true) return ("Anonymous", null, null);
        var subject = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        const string keyPrefix = "apikey:";
        var actorType = user.FindFirst("actor_type")?.Value;
        if (string.Equals(actorType, "ApiKey", StringComparison.OrdinalIgnoreCase) ||
            subject?.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) == true) {
            var keyId = subject?.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) == true ? subject[keyPrefix.Length..] : null;
            return ("ApiKey", null, Guid.TryParse(keyId, out var key) ? key.ToString() : null);
        }
        return ("User", Guid.TryParse(subject, out var id) ? id.ToString() : null, null);
    }
}
