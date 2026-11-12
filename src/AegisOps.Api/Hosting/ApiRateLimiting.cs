using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace AegisOps.Api.Hosting;

/// <summary>
/// ASP.NET rate limiting for the API (roadmap v1.1 item 4, docs/17):
/// <list type="bullet">
/// <item>A global token bucket per principal on <c>/api</c> and, as a separate bucket, on <c>/hubs</c>: the JWT
/// <c>sub</c> for users, the API key id for API keys and the client IP for anonymous requests. Health and metrics
/// endpoints are never limited.</item>
/// <item><see cref="UploadsPolicy"/>: a smaller token bucket per principal for scan uploads.</item>
/// <item><see cref="AuthPolicy"/>: the fixed window per client IP on login and refresh (the real client when
/// forwarded headers are configured).</item>
/// </list>
/// Every rejection is a 429 Problem Details with a <c>Retry-After</c> header in seconds. Limits come from
/// <c>RateLimits:*</c> (docs/14); the state is in memory per API instance.
/// </summary>
public static class ApiRateLimiting {
    public const string SectionName = "RateLimits";
    public const string AuthPolicy = "auth";
    public const string UploadsPolicy = "uploads";

    /// <summary>Used when a limiter does not report how long to wait.</summary>
    public const int FallbackRetryAfterSeconds = 60;

    public static readonly TokenBucketSettings GlobalDefaults = new(TokenLimit: 300, TokensPerPeriod: 50, ReplenishmentPeriodSeconds: 10);
    public static readonly TokenBucketSettings UploadsDefaults = new(TokenLimit: 30, TokensPerPeriod: 5, ReplenishmentPeriodSeconds: 10);
    public static readonly FixedWindowSettings AuthDefaults = new(PermitLimit: 10, WindowSeconds: 60);

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var global = TokenBucketSettings.Read(section.GetSection("Global"), GlobalDefaults);
        var uploads = TokenBucketSettings.Read(section.GetSection("Uploads"), UploadsDefaults);
        var auth = FixedWindowSettings.Read(section.GetSection("Auth"), AuthDefaults);

        services.AddRateLimiter(options => {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRejectionAsync;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => {
                var scope = LimitedScope(http.Request.Path);
                return scope is null
                    ? RateLimitPartition.GetNoLimiter(string.Empty)
                    : RateLimitPartition.GetTokenBucketLimiter($"{scope}|{PrincipalPartition(http)}", _ => global.ToOptions());
            });
            options.AddPolicy(UploadsPolicy, http =>
                RateLimitPartition.GetTokenBucketLimiter(PrincipalPartition(http), _ => uploads.ToOptions()));
            options.AddPolicy(AuthPolicy, http =>
                RateLimitPartition.GetFixedWindowLimiter(ClientAddress(http), _ => auth.ToOptions()));
        });
        return services;
    }

    /// <summary>
    /// The partition of the current principal: <c>user:&lt;sub&gt;</c>, <c>apiKey:&lt;id&gt;</c> (the API key handler's
    /// <c>sub</c>) or <c>ip:&lt;address&gt;</c> when the request is not authenticated.
    /// </summary>
    public static string PrincipalPartition(HttpContext http) {
        ArgumentNullException.ThrowIfNull(http);
        var user = http.User;
        if (user.Identity?.IsAuthenticated == true) {
            var subject = user.FindFirst("sub")?.Value;
            if (!string.IsNullOrEmpty(subject)) {
                return string.Equals(user.FindFirst("actor_type")?.Value, "apiKey", StringComparison.Ordinal)
                    ? subject // already "apiKey:<id>"
                    : "user:" + subject;
            }
        }

        return "ip:" + ClientAddress(http);
    }

    /// <summary><c>api</c> and <c>hubs</c> have separate buckets so a busy API client cannot starve realtime updates
    /// (and a reconnect loop cannot starve the API); everything else (health, metrics) is not limited.</summary>
    private static string? LimitedScope(PathString path) =>
        path.StartsWithSegments("/api") ? "api" : path.StartsWithSegments("/hubs") ? "hubs" : null;

    private static string ClientAddress(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static int RetryAfterSeconds(RateLimitLease lease) {
        ArgumentNullException.ThrowIfNull(lease);
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) && retryAfter > TimeSpan.Zero
            ? (int)Math.Ceiling(retryAfter.TotalSeconds)
            : FallbackRetryAfterSeconds;
    }

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken) {
        var seconds = RetryAfterSeconds(context.Lease);
        var http = context.HttpContext;
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            detail: string.Create(CultureInfo.InvariantCulture, $"Rate limit exceeded. Retry after {seconds} seconds.")
        ).ExecuteAsync(http);
    }

    public sealed record TokenBucketSettings(int TokenLimit, int TokensPerPeriod, int ReplenishmentPeriodSeconds) {
        public static TokenBucketSettings Read(IConfigurationSection section, TokenBucketSettings defaults) {
            ArgumentNullException.ThrowIfNull(section);
            ArgumentNullException.ThrowIfNull(defaults);
            var settings = new TokenBucketSettings(
                section.GetValue(nameof(TokenLimit), defaults.TokenLimit),
                section.GetValue(nameof(TokensPerPeriod), defaults.TokensPerPeriod),
                section.GetValue(nameof(ReplenishmentPeriodSeconds), defaults.ReplenishmentPeriodSeconds));
            if (settings.TokenLimit < 1 || settings.TokensPerPeriod < 1 || settings.TokensPerPeriod > settings.TokenLimit ||
                settings.ReplenishmentPeriodSeconds is < 1 or > 3600) {
                throw new InvalidOperationException(
                    $"{section.Path}: TokenLimit and TokensPerPeriod must be positive with TokensPerPeriod <= TokenLimit, " +
                    "and ReplenishmentPeriodSeconds between 1 and 3600.");
            }

            return settings;
        }

        public TokenBucketRateLimiterOptions ToOptions() => new() {
            TokenLimit = TokenLimit,
            TokensPerPeriod = TokensPerPeriod,
            ReplenishmentPeriod = TimeSpan.FromSeconds(ReplenishmentPeriodSeconds),
            QueueLimit = 0,
            AutoReplenishment = true,
        };
    }

    public sealed record FixedWindowSettings(int PermitLimit, int WindowSeconds) {
        public static FixedWindowSettings Read(IConfigurationSection section, FixedWindowSettings defaults) {
            ArgumentNullException.ThrowIfNull(section);
            ArgumentNullException.ThrowIfNull(defaults);
            var settings = new FixedWindowSettings(
                section.GetValue(nameof(PermitLimit), defaults.PermitLimit),
                section.GetValue(nameof(WindowSeconds), defaults.WindowSeconds));
            if (settings.PermitLimit < 1 || settings.WindowSeconds is < 1 or > 3600) {
                throw new InvalidOperationException(
                    $"{section.Path}: PermitLimit must be positive and WindowSeconds between 1 and 3600.");
            }

            return settings;
        }

        public FixedWindowRateLimiterOptions ToOptions() => new() {
            PermitLimit = PermitLimit,
            Window = TimeSpan.FromSeconds(WindowSeconds),
            QueueLimit = 0,
        };
    }
}
