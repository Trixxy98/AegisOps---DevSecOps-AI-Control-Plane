using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AegisOps.Infrastructure.Deploy;

/// <summary>Worker-only, operator-configured team webhooks. Never persist URLs, keys or response bodies in jobs.</summary>
public static class TeamWebhookSender {
    public static Task SendAsync(AegisOpsDbContext db, IConfiguration config, Guid eventId, CancellationToken ct) =>
        SendAsync(db, config, eventId, null, ct);

    // Internal override supports local TLS integration tests; the worker always uses the hardened handler below.
    internal static async Task SendAsync(
        AegisOpsDbContext db,
        IConfiguration config,
        Guid eventId,
        HttpMessageHandler? handlerOverride,
        CancellationToken ct
    ) {
        var item = await db.DeploymentEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, ct);
        if (item is null) return;
        var deployment = await db.Deployments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == item.DeploymentId, ct);
        if (deployment is null) return;
        var teamId = await db.Projects.AsNoTracking().Where(p => p.Id == deployment.ProjectId).Select(p => p.TeamId).SingleAsync(ct);
        var storedSettings = await db.TeamWebhookSettings.AsNoTracking().SingleOrDefaultAsync(settings => settings.TeamId == teamId, ct);
        var section = config.GetSection($"Webhooks:Teams:{teamId:D}");
        var address = storedSettings?.Url ?? section["Url"];
        if (string.IsNullOrEmpty(address)) return; // Opt-in only. No untrusted API can configure a destination.
        if (!TryValidateUrl(address, out var uri)) throw new InvalidOperationException("Webhook configuration is invalid.");
        byte[] key;
        if (storedSettings is not null) {
            try {
                key = new TeamWebhookSecretProtector(config).Unprotect(teamId, storedSettings.EncryptedSecret);
            } catch (CryptographicException) {
                throw new InvalidOperationException("Webhook configuration is invalid.");
            } catch (InvalidOperationException) {
                throw new InvalidOperationException("Webhook configuration is invalid.");
            }
        } else {
            try { key = Convert.FromBase64String(section["Secret"] ?? ""); }
            catch (FormatException) { throw new InvalidOperationException("Webhook configuration is invalid."); }
            if (key.Length < 32) {
                CryptographicOperations.ZeroMemory(key);
                throw new InvalidOperationException("Webhook configuration is invalid.");
            }
        }

        // No free-form reason, comments, event message, user data or destination in the outbound payload.
        var body = JsonSerializer.Serialize(new {
            id = item.Id, eventType = item.Type.ToString(), occurredAt = item.Timestamp,
            deploymentId = deployment.Id, projectId = deployment.ProjectId, environmentId = deployment.EnvironmentId,
            artifactId = deployment.ArtifactId, sequence = item.Sequence,
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        string signature;
        try { signature = Sign(key, timestamp, body); }
        finally { CryptographicOperations.ZeroMemory(key); }
        var handler = handlerOverride ?? new SocketsHttpHandler {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            ConnectCallback = async (context, token) => {
                var host = context.DnsEndPoint.Host;
                var addresses = await Dns.GetHostAddressesAsync(host, token);
                // Reject the entire DNS answer set, not just the first address. Pin the socket
                // to a vetted IP while TLS still verifies the original hostname.
                if (addresses.Length == 0 || addresses.Any(ip => !IsPublicIpv4(ip)))
                    throw new InvalidOperationException("Webhook destination is not allowed.");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try {
                    await socket.ConnectAsync(new IPEndPoint(addresses[0], 443), token);
                    return new NetworkStream(socket, ownsSocket: true);
                } catch { socket.Dispose(); throw; }
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-AegisOps-Event-Id", item.Id.ToString("D"));
        request.Headers.Add("X-AegisOps-Timestamp", timestamp);
        request.Headers.Add("X-AegisOps-Signature", "sha256=" + signature);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Webhook delivery failed.");
    }

    public static bool TryValidateUrl(string? value, out Uri uri) {
        uri = null!;
        if (value is null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps || candidate.Port != 443 ||
            !string.IsNullOrEmpty(candidate.UserInfo) || !string.IsNullOrEmpty(candidate.Fragment) ||
            candidate.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4) ||
            (IPAddress.TryParse(candidate.Host, out var ip) && !IsPublicIpv4(ip))) return false;
        uri = candidate;
        return true;
    }

    public static string Sign(byte[] key, string timestamp, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(timestamp + "." + body))).ToLowerInvariant();

    // Conservative: IPv6 and special-purpose IPv4 ranges are not supported by this initial release.
    public static bool IsPublicIpv4(IPAddress address) {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] is >= 1 and <= 223 && b[0] != 10 && b[0] != 127 &&
            !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
            !(b[0] == 169 && b[1] == 254) &&
            !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && (b[1] == 0 || b[1] == 168 || b[1] == 88 && b[2] == 99)) &&
            !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)) &&
            !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
}
