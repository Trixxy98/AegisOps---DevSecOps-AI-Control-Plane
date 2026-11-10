using System.Diagnostics;
using System.Globalization;
using AegisOps.Application.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Display;

namespace AegisOps.Infrastructure.Observability;

/// <summary>Logging options for one AegisOps process (docs/15-observability.md §1).</summary>
public sealed record AegisOpsLoggingOptions(string ServiceName) {
    /// <summary>
    /// Source contexts whose events skip <see cref="SensitivePropertyMasker"/>, honoured only in the Development
    /// environment. Used for the Development demo seed, whose first-run credentials are only ever shown in its log.
    /// </summary>
    public IReadOnlyCollection<string> DevelopmentDisclosureSources { get; init; } = [];

    /// <summary>Replaces the console sink when set (tests capture the formatted output here).</summary>
    public Action<LoggerSinkConfiguration>? Sinks { get; init; }
}

/// <summary>Serilog configuration shared by the API and the Worker.</summary>
public static class AegisOpsLogging {
    public const string ApiServiceName = "aegisops-api";
    public const string WorkerServiceName = "aegisops-worker";

    internal const string DevelopmentOutputTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {ServiceName} {SourceContext}: {Message:lj} (CorrelationId={CorrelationId}, TraceId={TraceId}){NewLine}{Exception}";

    /// <summary>
    /// Makes Serilog the logging provider without touching the static <see cref="Log.Logger"/>, so several
    /// hosts in one process (parallel test hosts) each keep their own logger. Existing <c>ILogger&lt;T&gt;</c>
    /// and <c>[LoggerMessage]</c> call sites keep working: Serilog is the Microsoft.Extensions.Logging provider.
    /// </summary>
    public static IServiceCollection AddAegisOpsSerilog(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment, AegisOpsLoggingOptions options) {
        ArgumentNullException.ThrowIfNull(environment);
        services.AddHttpContextAccessor();
        services.AddSerilog((provider, logger) => Configure(logger, configuration, environment.EnvironmentName, options,
            provider.GetService<IHttpContextAccessor>()), preserveStaticLogger: true);
        return services;
    }

    /// <summary>Builds the logger pipeline: levels, enrichers, the PII masker (last) and the console sink.</summary>
    public static LoggerConfiguration Configure(LoggerConfiguration logger, IConfiguration configuration, string environmentName,
        AegisOpsLoggingOptions options, IHttpContextAccessor? httpContextAccessor = null) {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        var development = string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);
        // Code defaults; the appsettings "Serilog" section (MinimumLevel/Override) refines them.
        logger.MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ServiceName", options.ServiceName)
            .Enrich.WithProperty("Environment", environmentName)
            .Enrich.WithProperty("MachineName", System.Environment.MachineName)
            .Enrich.With(new CorrelationIdEnricher(httpContextAccessor))
            .Enrich.With<TraceContextEnricher>()
            // Must stay the last enricher so it also sees properties added above and by log scopes.
            .Enrich.With(new SensitivePropertyMasker(development ? options.DevelopmentDisclosureSources : []));
        if (options.Sinks is not null) options.Sinks(logger.WriteTo);
        else logger.WriteTo.Console(ConsoleFormatter(environmentName));
        return logger;
    }

    /// <summary>Compact JSON (CLEF) everywhere except Development, which gets a readable single-line format.</summary>
    public static ITextFormatter ConsoleFormatter(string environmentName) =>
        string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
            ? new MessageTemplateTextFormatter(DevelopmentOutputTemplate, CultureInfo.InvariantCulture)
            : new CompactJsonFormatter();
}

/// <summary>
/// Adds <c>CorrelationId</c>: the validated <c>correlationId</c> HttpContext item set by the API's request tracing,
/// else the <c>correlationId</c> baggage of the current Activity (Worker job spans).
/// </summary>
public sealed class CorrelationIdEnricher(IHttpContextAccessor? httpContextAccessor) : ILogEventEnricher {
    public const string PropertyName = "CorrelationId";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);
        if (logEvent.Properties.ContainsKey(PropertyName)) return;
        var correlation = httpContextAccessor?.HttpContext?.Items["correlationId"] as string
            ?? Activity.Current?.GetBaggageItem("correlationId");
        if (AegisOpsTracing.IsSafeCorrelation(correlation))
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, correlation));
    }
}

/// <summary>Adds <c>TraceId</c>/<c>SpanId</c> properties from the event's captured trace context.</summary>
public sealed class TraceContextEnricher : ILogEventEnricher {
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);
        var traceId = logEvent.TraceId ?? Activity.Current?.TraceId;
        var spanId = logEvent.SpanId ?? Activity.Current?.SpanId;
        if (traceId is { } trace && trace != default)
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("TraceId", trace.ToHexString()));
        if (spanId is { } span && span != default)
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("SpanId", span.ToHexString()));
    }
}

/// <summary>
/// PII policy (docs/17-security-hardening.md §3): replaces the value of every property — top-level, inside
/// destructured objects, dictionaries and sequences — whose name looks like a credential or personal data
/// (password, token, secret, API key, authorization, cookie, email, prompt, connection string) with <c>***</c>.
/// It also drops the raw <c>RequestPath</c>/<c>QueryString</c> that log scopes attach, so only route templates are logged.
/// </summary>
public sealed class SensitivePropertyMasker(IReadOnlyCollection<string> exemptSourceContexts) : ILogEventEnricher {
    public const string Redacted = "***";
    private static readonly ScalarValue RedactedValue = new(Redacted);
    private static readonly string[] SensitiveFragments = [
        "password", "passwd", "secret", "token", "apikey", "authorization", "cookie", "credential",
        "email", "prompt", "connectionstring", "privatekey", "signingkey",
    ];
    private static readonly string[] DroppedProperties = ["RequestPath", "QueryString"];

    public SensitivePropertyMasker() : this([]) { }

    public static bool IsSensitive(string name) {
        ArgumentNullException.ThrowIfNull(name);
        Span<char> buffer = stackalloc char[Math.Min(name.Length, 128)];
        var length = 0;
        foreach (var c in name) {
            if (length == buffer.Length) break;
            if (char.IsAsciiLetterOrDigit(c)) buffer[length++] = char.ToLowerInvariant(c);
        }
        var normalized = buffer[..length];
        foreach (var fragment in SensitiveFragments)
            if (normalized.Contains(fragment, StringComparison.Ordinal)) return true;
        return false;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (exemptSourceContexts.Count > 0 && logEvent.Properties.TryGetValue("SourceContext", out var source) &&
            source is ScalarValue { Value: string context } && exemptSourceContexts.Contains(context)) return;
        foreach (var dropped in DroppedProperties) logEvent.RemovePropertyIfPresent(dropped);
        List<LogEventProperty>? replacements = null;
        foreach (var (name, value) in logEvent.Properties) {
            var masked = IsSensitive(name) ? RedactedValue : Mask(value);
            if (!ReferenceEquals(masked, value)) (replacements ??= []).Add(new LogEventProperty(name, masked));
        }
        if (replacements is null) return;
        foreach (var replacement in replacements) logEvent.AddOrUpdateProperty(replacement);
    }

    private static LogEventPropertyValue Mask(LogEventPropertyValue value) {
        switch (value) {
            case StructureValue structure: {
                var changed = false;
                var properties = new List<LogEventProperty>(structure.Properties.Count);
                foreach (var property in structure.Properties) {
                    var masked = IsSensitive(property.Name) ? RedactedValue : Mask(property.Value);
                    changed |= !ReferenceEquals(masked, property.Value);
                    properties.Add(new LogEventProperty(property.Name, masked));
                }
                return changed ? new StructureValue(properties, structure.TypeTag) : value;
            }
            case DictionaryValue dictionary: {
                var changed = false;
                var elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
                foreach (var (key, element) in dictionary.Elements) {
                    var masked = key.Value is string name && IsSensitive(name) ? RedactedValue : Mask(element);
                    changed |= !ReferenceEquals(masked, element);
                    elements.Add(new(key, masked));
                }
                return changed ? new DictionaryValue(elements) : value;
            }
            case SequenceValue sequence: {
                var changed = false;
                var elements = new List<LogEventPropertyValue>(sequence.Elements.Count);
                foreach (var element in sequence.Elements) {
                    var masked = Mask(element);
                    changed |= !ReferenceEquals(masked, element);
                    elements.Add(masked);
                }
                return changed ? new SequenceValue(elements) : value;
            }
            default:
                return value;
        }
    }
}
