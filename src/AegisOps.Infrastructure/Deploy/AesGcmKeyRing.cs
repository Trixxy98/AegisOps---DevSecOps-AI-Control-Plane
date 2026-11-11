using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace AegisOps.Infrastructure.Deploy;

/// <summary>Wording for the errors a key ring raises, so each protector keeps its own messages.</summary>
/// <param name="KeyNoun">Used in key configuration errors, e.g. "webhook" ("Webhook encryption key ID is invalid.").</param>
/// <param name="SecretNoun">Used in envelope errors, e.g. "webhook secret" ("Stored webhook secret could not be decrypted.").</param>
internal sealed record KeyRingWording(string KeyNoun, string SecretNoun);

/// <summary>
/// AES-256-GCM key ring read from a configuration section (<c>Enabled</c>, <c>CurrentKeyId</c>, <c>Keys:&lt;id&gt;</c> as
/// base64 32-byte keys). Envelopes are <c>v1.&lt;keyId&gt;.&lt;nonce&gt;.&lt;ciphertext&gt;.&lt;tag&gt;</c>; callers bind them to a
/// resource with associated data. Old keys stay usable for decryption so keys can be rotated.
/// </summary>
internal sealed class AesGcmKeyRing {
    private const string Version = "v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    internal const int KeySize = 32;
    private readonly string? currentKeyId;
    private readonly Dictionary<string, byte[]> keys;
    private readonly KeyRingWording wording;

    public AesGcmKeyRing(IConfiguration configuration, string section, KeyRingWording wording) {
        ArgumentNullException.ThrowIfNull(configuration);
        this.wording = wording;
        var keyNoun = Capitalize(wording.KeyNoun);
        IsEnabled = bool.TryParse(configuration[$"{section}:Enabled"], out var enabled) && enabled;
        currentKeyId = configuration[$"{section}:CurrentKeyId"];
        var configuredKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var setting in configuration.GetSection($"{section}:Keys").GetChildren()) {
            if (!IsValidKeyId(setting.Key)) {
                throw new InvalidOperationException($"{keyNoun} encryption key ID is invalid.");
            }
            if (string.IsNullOrWhiteSpace(setting.Value)) continue;
            byte[] key;
            try {
                key = Convert.FromBase64String(setting.Value ?? string.Empty);
            } catch (FormatException exception) {
                throw new InvalidOperationException($"{keyNoun} encryption key configuration is invalid.", exception);
            }
            if (key.Length != KeySize) {
                throw new InvalidOperationException($"{keyNoun} encryption keys must contain 32 bytes.");
            }
            configuredKeys.Add(setting.Key, key);
        }

        if (configuredKeys.Count == 0 && !IsEnabled) {
            currentKeyId = null;
            keys = configuredKeys;
            return;
        }
        if (string.IsNullOrWhiteSpace(currentKeyId) || !IsValidKeyId(currentKeyId) || !configuredKeys.ContainsKey(currentKeyId)) {
            throw new InvalidOperationException($"The current {wording.KeyNoun} encryption key is not configured.");
        }

        keys = configuredKeys;
    }

    public bool IsEnabled { get; }
    public bool IsConfigured => currentKeyId is not null && keys.ContainsKey(currentKeyId);
    public string? CurrentKeyId => currentKeyId;

    public string Protect(ReadOnlySpan<byte> plaintext, byte[] associatedData) {
        EnsureConfigured();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(keys[currentKeyId!], TagSize)) {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }
        return string.Join('.', Version, currentKeyId, Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
    }

    public byte[] Unprotect(string envelope, byte[] associatedData) {
        EnsureConfigured();
        ArgumentNullException.ThrowIfNull(envelope);
        try {
            var parts = envelope.Split('.');
            if (parts.Length != 5 || parts[0] != Version || !IsValidKeyId(parts[1]) ||
                !keys.TryGetValue(parts[1], out var key)) {
                throw DecryptionFailed();
            }
            var nonce = Convert.FromBase64String(parts[2]);
            var ciphertext = Convert.FromBase64String(parts[3]);
            var tag = Convert.FromBase64String(parts[4]);
            if (nonce.Length != NonceSize || tag.Length != TagSize) {
                throw DecryptionFailed();
            }
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return plaintext;
        } catch (FormatException exception) {
            throw DecryptionFailed(exception);
        } catch (ArgumentException exception) {
            throw DecryptionFailed(exception);
        }
    }

    public string ReadKeyId(string envelope) {
        ArgumentNullException.ThrowIfNull(envelope);
        var parts = envelope.Split('.');
        if (parts.Length != 5 || parts[0] != Version) {
            throw DecryptionFailed();
        }
        return parts[1];
    }

    private CryptographicException DecryptionFailed(Exception? inner = null) =>
        new($"Stored {wording.SecretNoun} could not be decrypted.", inner);

    private void EnsureConfigured() {
        if (!IsConfigured) throw new InvalidOperationException($"{Capitalize(wording.SecretNoun)} encryption is not configured.");
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static bool IsValidKeyId(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
