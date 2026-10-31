using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace AegisOps.Infrastructure.Deploy;

public sealed class TeamWebhookSecretProtector {
    private const string Version = "v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private readonly string? currentKeyId;
    private readonly Dictionary<string, byte[]> keys;

    public TeamWebhookSecretProtector(IConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);
        IsEnabled = bool.TryParse(configuration["WebhookSecrets:Enabled"], out var enabled) && enabled;
        currentKeyId = configuration["WebhookSecrets:CurrentKeyId"];
        var configuredKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var setting in configuration.GetSection("WebhookSecrets:Keys").GetChildren()) {
            if (!IsValidKeyId(setting.Key)) {
                throw new InvalidOperationException("Webhook encryption key ID is invalid.");
            }
            if (string.IsNullOrWhiteSpace(setting.Value)) continue;
            byte[] key;
            try {
                key = Convert.FromBase64String(setting.Value ?? string.Empty);
            } catch (FormatException exception) {
                throw new InvalidOperationException("Webhook encryption key configuration is invalid.", exception);
            }
            if (key.Length != KeySize) {
                throw new InvalidOperationException("Webhook encryption keys must contain 32 bytes.");
            }
            configuredKeys.Add(setting.Key, key);
        }

        if (configuredKeys.Count == 0 && !IsEnabled) {
            currentKeyId = null;
            keys = configuredKeys;
            return;
        }
        if (string.IsNullOrWhiteSpace(currentKeyId) || !IsValidKeyId(currentKeyId) || !configuredKeys.ContainsKey(currentKeyId)) {
            throw new InvalidOperationException("The current webhook encryption key is not configured.");
        }

        keys = configuredKeys;
    }

    public bool IsEnabled { get; }
    public bool IsConfigured => currentKeyId is not null && keys.ContainsKey(currentKeyId);

    public string Protect(Guid teamId, ReadOnlySpan<byte> plaintext) {
        EnsureConfigured();
        if (teamId == Guid.Empty) throw new ArgumentException("Team ID is required.", nameof(teamId));
        if (plaintext.Length < KeySize) throw new ArgumentException("Webhook signing secrets must contain at least 32 bytes.", nameof(plaintext));

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(keys[currentKeyId!], TagSize)) {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(teamId));
        }
        return string.Join('.', Version, currentKeyId, Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
    }

    public byte[] Unprotect(Guid teamId, string envelope) {
        EnsureConfigured();
        if (teamId == Guid.Empty) throw new ArgumentException("Team ID is required.", nameof(teamId));
        try {
            var parts = envelope.Split('.');
            if (parts.Length != 5 || parts[0] != Version || !IsValidKeyId(parts[1]) ||
                !keys.TryGetValue(parts[1], out var key)) {
                throw new CryptographicException("Stored webhook secret could not be decrypted.");
            }
            var nonce = Convert.FromBase64String(parts[2]);
            var ciphertext = Convert.FromBase64String(parts[3]);
            var tag = Convert.FromBase64String(parts[4]);
            if (nonce.Length != NonceSize || tag.Length != TagSize) {
                throw new CryptographicException("Stored webhook secret could not be decrypted.");
            }
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(teamId));
            return plaintext;
        } catch (FormatException exception) {
            throw new CryptographicException("Stored webhook secret could not be decrypted.", exception);
        } catch (ArgumentException exception) {
            throw new CryptographicException("Stored webhook secret could not be decrypted.", exception);
        }
    }

    public string Rewrap(Guid teamId, string envelope) {
        var keyId = ReadKeyId(envelope);
        var plaintext = Unprotect(teamId, envelope);
        try {
            return string.Equals(keyId, currentKeyId, StringComparison.Ordinal) ? envelope : Protect(teamId, plaintext);
        } finally {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string ReadKeyId(string envelope) {
        var parts = envelope.Split('.');
        if (parts.Length != 5 || parts[0] != Version) {
            throw new CryptographicException("Stored webhook secret could not be decrypted.");
        }
        return parts[1];
    }

    private void EnsureConfigured() {
        if (!IsConfigured) throw new InvalidOperationException("Webhook secret encryption is not configured.");
    }

    private static byte[] AssociatedData(Guid teamId) => Encoding.UTF8.GetBytes($"AegisOps.TeamWebhook.{Version}.{teamId:D}");

    private static bool IsValidKeyId(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
