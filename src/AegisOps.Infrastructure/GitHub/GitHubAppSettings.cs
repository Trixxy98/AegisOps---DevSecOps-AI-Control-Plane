using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace AegisOps.Infrastructure.GitHub;

/// <summary>
/// The <c>GitHubApp</c> configuration section (docs/github-deployment-gate.md, docs/14). Disabled by default. When
/// <c>Enabled</c> is true the settings are validated at startup and the process refuses to start on a problem, like the
/// other key rings. Errors name the setting, never its value. The API needs only the webhook secret; the Worker needs
/// the App ID and private key to answer GitHub.
/// </summary>
public sealed class GitHubAppSettings : IDisposable {
    public const string SectionName = "GitHubApp";
    public const int MinimumWebhookSecretLength = 32;
    public static readonly Uri DefaultApiBaseUrl = new("https://api.github.com/");

    private readonly RSA? privateKey;
    private readonly byte[] webhookSecret;

    private GitHubAppSettings(bool enabled, string? appId, RSA? privateKey, byte[] webhookSecret, Uri apiBaseUrl, Uri? publicBaseUrl, string apiVersion) {
        Enabled = enabled;
        AppId = appId;
        this.privateKey = privateKey;
        this.webhookSecret = webhookSecret;
        ApiBaseUrl = apiBaseUrl;
        PublicBaseUrl = publicBaseUrl;
        ApiVersion = apiVersion;
    }

    public bool Enabled { get; }

    /// <summary>The JWT issuer: the App ID (numeric) or the App's client ID.</summary>
    public string? AppId { get; }

    public Uri ApiBaseUrl { get; }

    /// <summary>Optional public URL of the AegisOps web UI, used for the "details" link in GitHub comments.</summary>
    public Uri? PublicBaseUrl { get; }

    /// <summary>The <c>X-GitHub-Api-Version</c> header value.</summary>
    public string ApiVersion { get; }

    public bool CanVerifyWebhooks => Enabled && webhookSecret.Length > 0;
    public bool CanCallGitHub => Enabled && privateKey is not null && !string.IsNullOrWhiteSpace(AppId);

    internal RSA PrivateKey => privateKey ?? throw new InvalidOperationException("GitHubApp:PrivateKey is not configured.");

    public bool VerifySignature(ReadOnlySpan<byte> body, string? signatureHeader) =>
        CanVerifyWebhooks && GitHubWebhookSignature.Verify(webhookSecret, body, signatureHeader);

    public static GitHubAppSettings Disabled() =>
        new(false, null, null, [], DefaultApiBaseUrl, null, DefaultApiVersion);

    public const string DefaultApiVersion = "2022-11-28";

    /// <summary>Reads and validates the section. Throws <see cref="InvalidOperationException"/> when enabled and invalid.</summary>
    /// <param name="requireWebhookSecret">True in the API, which verifies webhook signatures.</param>
    /// <param name="requirePrivateKey">True in the Worker, which signs the App JWT.</param>
    public static GitHubAppSettings Load(IConfiguration configuration, bool requireWebhookSecret, bool requirePrivateKey) {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var enabled = bool.TryParse(section["Enabled"], out var parsed) && parsed;
        if (!enabled) return Disabled();

        var problems = new List<string>();
        var apiBaseUrl = DefaultApiBaseUrl;
        if (!string.IsNullOrWhiteSpace(section["ApiBaseUrl"])) {
            if (!Uri.TryCreate(EnsureSlash(section["ApiBaseUrl"]!.Trim()), UriKind.Absolute, out var api) ||
                api.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(api.UserInfo) || !string.IsNullOrEmpty(api.Query)) {
                problems.Add("GitHubApp:ApiBaseUrl must be an absolute https URL.");
            } else apiBaseUrl = api;
        }

        Uri? publicBaseUrl = null;
        if (!string.IsNullOrWhiteSpace(section["PublicBaseUrl"])) {
            if (!Uri.TryCreate(EnsureSlash(section["PublicBaseUrl"]!.Trim()), UriKind.Absolute, out var site) ||
                (site.Scheme != Uri.UriSchemeHttps && site.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(site.UserInfo)) {
                problems.Add("GitHubApp:PublicBaseUrl must be an absolute http(s) URL.");
            } else publicBaseUrl = site;
        }

        var secretText = section["WebhookSecret"];
        byte[] secret = [];
        if (!string.IsNullOrEmpty(secretText)) {
            if (secretText.Length < MinimumWebhookSecretLength)
                problems.Add($"GitHubApp:WebhookSecret must be at least {MinimumWebhookSecretLength} characters.");
            else secret = Encoding.UTF8.GetBytes(secretText);
        } else if (requireWebhookSecret) {
            problems.Add("GitHubApp:WebhookSecret (GITHUB_APP_WEBHOOK_SECRET) is missing.");
        }

        var appId = section["AppId"]?.Trim();
        if (string.IsNullOrEmpty(appId)) appId = section["ClientId"]?.Trim();
        if (!string.IsNullOrEmpty(appId) && (appId.Length > 100 || !appId.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
            problems.Add("GitHubApp:AppId must be the App ID or client ID.");
        else if (string.IsNullOrEmpty(appId) && requirePrivateKey)
            problems.Add("GitHubApp:AppId (GITHUB_APP_ID) is missing.");

        RSA? key = null;
        var pem = ReadPem(section, problems);
        if (pem is not null) {
            key = RSA.Create();
            try {
                key.ImportFromPem(pem);
                if (key.KeySize < 2048) problems.Add("GitHubApp:PrivateKey must be an RSA key of at least 2048 bits.");
            } catch (Exception exception) when (exception is ArgumentException or CryptographicException) {
                problems.Add("GitHubApp:PrivateKey is not a valid RSA private key in PEM format.");
            }
        } else if (requirePrivateKey && !problems.Any(problem => problem.StartsWith("GitHubApp:PrivateKey", StringComparison.Ordinal))) {
            problems.Add("GitHubApp:PrivateKey or GitHubApp:PrivateKeyBase64 (GITHUB_APP_PRIVATE_KEY_BASE64) is missing.");
        }

        var apiVersion = string.IsNullOrWhiteSpace(section["ApiVersion"]) ? DefaultApiVersion : section["ApiVersion"]!.Trim();
        if (apiVersion.Length > 20 || !apiVersion.All(character => char.IsAsciiDigit(character) || character == '-'))
            problems.Add("GitHubApp:ApiVersion must look like 2022-11-28.");

        if (problems.Count > 0) {
            key?.Dispose();
            throw new InvalidOperationException("GitHub App configuration is invalid: " + string.Join(" ", problems));
        }

        return new GitHubAppSettings(true, string.IsNullOrEmpty(appId) ? null : appId, key, secret, apiBaseUrl, publicBaseUrl, apiVersion);
    }

    private static string? ReadPem(IConfigurationSection section, List<string> problems) {
        var pem = section["PrivateKey"];
        if (!string.IsNullOrWhiteSpace(pem)) return pem.Replace("\\n", "\n", StringComparison.Ordinal);
        var encoded = section["PrivateKeyBase64"];
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim()));
        } catch (FormatException) {
            problems.Add("GitHubApp:PrivateKeyBase64 is not valid base64.");
            return null;
        }
    }

    private static string EnsureSlash(string value) => value.EndsWith('/') ? value : value + "/";

    public void Dispose() {
        privateKey?.Dispose();
        CryptographicOperations.ZeroMemory(webhookSecret);
    }
}
