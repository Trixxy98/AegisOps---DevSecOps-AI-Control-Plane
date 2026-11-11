using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace AegisOps.Infrastructure.Deploy;

/// <summary>
/// Encrypts secret environment variables (docs/17 §5) with the <c>EnvironmentSecrets</c> AES-256-GCM key ring. The
/// environment ID and variable name are the associated data, so a ciphertext copied to another environment (or
/// another variable) does not decrypt.
/// </summary>
public sealed class EnvironmentSecretProtector {
    private const string Version = "v1";
    private readonly AesGcmKeyRing ring;

    public EnvironmentSecretProtector(IConfiguration configuration) {
        ring = new AesGcmKeyRing(configuration, "EnvironmentSecrets", new KeyRingWording("environment secret", "environment secret"));
    }

    /// <summary>True when new secret values may be stored (<c>EnvironmentSecrets:Enabled</c> and a current key).</summary>
    public bool CanProtect => ring.IsEnabled && ring.IsConfigured;
    public bool IsConfigured => ring.IsConfigured;

    public string Protect(Guid environmentId, string name, string value) {
        ArgumentNullException.ThrowIfNull(value);
        if (environmentId == Guid.Empty) throw new ArgumentException("Environment ID is required.", nameof(environmentId));
        var plaintext = Encoding.UTF8.GetBytes(value);
        try {
            return ring.Protect(plaintext, AssociatedData(environmentId, name));
        } finally {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string Unprotect(Guid environmentId, string name, string envelope) {
        if (environmentId == Guid.Empty) throw new ArgumentException("Environment ID is required.", nameof(environmentId));
        var plaintext = ring.Unprotect(envelope, AssociatedData(environmentId, name));
        try {
            return Encoding.UTF8.GetString(plaintext);
        } finally {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] AssociatedData(Guid environmentId, string name) {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Variable name is required.", nameof(name));
        return Encoding.UTF8.GetBytes($"AegisOps.EnvironmentVariable.{Version}.{environmentId:D}.{name}");
    }
}
