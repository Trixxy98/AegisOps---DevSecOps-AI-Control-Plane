using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisOps.Domain.Deploy;

namespace AegisOps.Infrastructure.GitHub;

/// <summary>GitHub's answer to a review call.</summary>
/// <param name="Accepted">2xx: the review was recorded.</param>
/// <param name="StatusCode">The HTTP status (never the body, which is not stored).</param>
public sealed record GitHubReviewResult(bool Accepted, int StatusCode);

/// <summary>A GitHub call that may succeed later (5xx, 429, network error, expired token). The job retries it.</summary>
public sealed class GitHubTransientException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Worker-side GitHub App client (docs/github-deployment-gate.md): signs the App JWT (RS256), exchanges it for an
/// installation access token (cached until shortly before it expires) and posts deployment protection rule reviews.
/// Tokens, JWTs and response bodies are never logged, stored or put in exception messages.
/// </summary>
public sealed class GitHubAppClient {
    /// <summary>Installation tokens are reused until this long before they expire (GitHub issues them for one hour).</summary>
    public static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    private readonly GitHubAppSettings settings;
    private readonly Func<HttpClient> clients;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<long, CachedToken> tokens = new();

    public GitHubAppClient(GitHubAppSettings settings, Func<HttpClient> clients, TimeProvider time) {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.clients = clients ?? throw new ArgumentNullException(nameof(clients));
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public GitHubAppSettings Settings => settings;

    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);

    /// <summary>
    /// The App JWT: header <c>{"alg":"RS256","typ":"JWT"}</c>, claims <c>iat</c> = now - 60 s (clock drift),
    /// <c>exp</c> = now + 9 min (GitHub allows at most 10) and <c>iss</c> = the App ID or client ID.
    /// </summary>
    public string CreateAppJwt() {
        if (!settings.CanCallGitHub) throw new InvalidOperationException("GitHub App is not configured on the Worker (GitHubApp:AppId and PrivateKey).");
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat = now - 60, exp = now + 9 * 60, iss = settings.AppId }));
        var signingInput = header + "." + payload;
        var signature = settings.PrivateKey.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    /// <summary>Returns a cached installation token or exchanges a new App JWT at <c>POST /app/installations/{id}/access_tokens</c>.</summary>
    public async Task<string> GetInstallationTokenAsync(long installationId, CancellationToken cancellationToken) {
        if (tokens.TryGetValue(installationId, out var cached) && cached.ExpiresAt - TokenRefreshMargin > time.GetUtcNow())
            return cached.Value;

        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(settings.ApiBaseUrl, string.Create(CultureInfo.InvariantCulture, $"app/installations/{installationId}/access_tokens")));
        AddHeaders(request, CreateAppJwt());
        // Least privilege: the token can only review deployments.
        request.Content = new StringContent("""{"permissions":{"deployments":"write"}}""", Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) {
            throw new GitHubTransientException(string.Create(CultureInfo.InvariantCulture,
                $"GitHub refused the installation token request (HTTP {(int)response.StatusCode})."));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("token", out var tokenElement) || tokenElement.GetString() is not { Length: > 0 } token)
            throw new GitHubTransientException("GitHub returned no installation token.");
        var expiresAt = root.TryGetProperty("expires_at", out var expires) && expires.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : time.GetUtcNow().AddMinutes(10);
        tokens[installationId] = new CachedToken(token, expiresAt);
        return token;
    }

    /// <summary>
    /// <c>POST /repos/{owner}/{repo}/actions/runs/{run_id}/deployment_protection_rule</c> with
    /// <c>{ environment_name, state, comment }</c>. The URL is built from <see cref="GitHubAppSettings.ApiBaseUrl"/>, never from
    /// the webhook's callback URL, so the token cannot be sent elsewhere. Throws <see cref="GitHubTransientException"/>
    /// when the call should be retried; returns <see cref="GitHubReviewResult.Accepted"/> false for a final refusal.
    /// </summary>
    public async Task<GitHubReviewResult> ReviewAsync(GitHubDeploymentGate gate, string state, string comment, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(gate);
        var token = await GetInstallationTokenAsync(gate.InstallationId, cancellationToken);
        var path = string.Create(CultureInfo.InvariantCulture,
            $"repos/{Uri.EscapeDataString(gate.Owner)}/{Uri.EscapeDataString(gate.Repository)}/actions/runs/{gate.RunId}/deployment_protection_rule");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.ApiBaseUrl, path));
        AddHeaders(request, token);
        request.Content = new StringContent(JsonSerializer.Serialize(new {
            environment_name = gate.EnvironmentName,
            state,
            comment = GitHubDeploymentGate.Truncate(comment) ?? state,
        }), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode) return new GitHubReviewResult(true, status);
        if (response.StatusCode == HttpStatusCode.Unauthorized) {
            // A revoked or expired token: drop it and retry with a fresh one.
            tokens.TryRemove(gate.InstallationId, out _);
            throw new GitHubTransientException("GitHub rejected the installation token (HTTP 401).");
        }

        if (status >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout ||
            response.StatusCode == HttpStatusCode.Forbidden && (response.Headers.RetryAfter is not null ||
                response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0")) {
            throw new GitHubTransientException(string.Create(CultureInfo.InvariantCulture, $"GitHub review call failed (HTTP {status})."));
        }

        return new GitHubReviewResult(false, status);
    }

    private void AddHeaders(HttpRequestMessage request, string bearer) {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AegisOps", "1.2"));
        request.Headers.Add("X-GitHub-Api-Version", settings.ApiVersion);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        try {
            return await clients().SendAsync(request, cancellationToken);
        } catch (HttpRequestException exception) {
            // The exception message can contain the URL; keep only the fact that the call failed.
            throw new GitHubTransientException("GitHub could not be reached.", exception);
        } catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested) {
            throw new GitHubTransientException("GitHub did not answer in time.", exception);
        }
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
