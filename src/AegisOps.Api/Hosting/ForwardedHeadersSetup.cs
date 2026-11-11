using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace AegisOps.Api.Hosting;

/// <summary>
/// Opt-in <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c> handling behind a reverse proxy (roadmap v1.1 item 3, docs/17).
/// Off by default: then every client is the address that connected (nginx in Compose). When
/// <c>ForwardedHeaders:Enabled</c> is true, the headers are honoured only from <c>ForwardedHeaders:KnownNetworks</c>
/// (CIDRs) and <c>ForwardedHeaders:KnownProxies</c> (addresses), plus loopback; anyone else's headers are ignored.
/// <c>ForwardedHeaders:ForwardLimit</c> (default 1) is the number of proxy hops to unwind, e.g. 2 for Caddy → nginx → API.
/// </summary>
public static class ForwardedHeadersSetup {
    public const string SectionName = "ForwardedHeaders";

    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool>($"{SectionName}:Enabled");

    public static IServiceCollection AddAegisOpsForwardedHeaders(this IServiceCollection services, IConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!IsEnabled(configuration)) {
            return services;
        }

        var section = configuration.GetSection(SectionName);
        var networks = Values(section.GetSection("KnownNetworks"));
        var proxies = Values(section.GetSection("KnownProxies"));
        if (networks.Count == 0 && proxies.Count == 0) {
            throw new InvalidOperationException(
                "ForwardedHeaders:Enabled is true but neither ForwardedHeaders:KnownNetworks nor ForwardedHeaders:KnownProxies is set.");
        }

        var parsedNetworks = networks.Select(value => System.Net.IPNetwork.TryParse(value, out var network)
            ? network
            : throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks entry '{value}' is not a CIDR (e.g. 172.16.0.0/12).")).ToList();
        var parsedProxies = proxies.Select(value => IPAddress.TryParse(value, out var address)
            ? address
            : throw new InvalidOperationException($"ForwardedHeaders:KnownProxies entry '{value}' is not an IP address.")).ToList();
        var limit = section.GetValue("ForwardLimit", 1);
        if (limit is < 1 or > 10) {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"ForwardedHeaders:ForwardLimit must be between 1 and 10 (was {limit})."));
        }

        services.Configure<ForwardedHeadersOptions>(options => {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = limit;
            // Loopback stays trusted (the defaults); everything else must be listed.
            foreach (var network in parsedNetworks) options.KnownIPNetworks.Add(network);
            foreach (var proxy in parsedProxies) options.KnownProxies.Add(proxy);
        });
        return services;
    }

    /// <summary>Must run before request logging, authentication and rate limiting so they see the real client.</summary>
    public static IApplicationBuilder UseAegisOpsForwardedHeaders(this IApplicationBuilder app, IConfiguration configuration) =>
        IsEnabled(configuration) ? app.UseForwardedHeaders() : app;

    private static List<string> Values(IConfigurationSection section) {
        // Accept both an array (KnownNetworks:0, :1 …; env KnownNetworks__0) and one comma-separated value.
        var values = section.GetChildren().Select(child => child.Value).ToList();
        values.Add(section.Value);
        return values
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();
    }
}
