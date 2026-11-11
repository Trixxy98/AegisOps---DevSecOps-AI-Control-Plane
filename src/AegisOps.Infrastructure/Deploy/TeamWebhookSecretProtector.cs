using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace AegisOps.Infrastructure.Deploy;

/// <summary>Encrypts team webhook signing secrets with the <c>WebhookSecrets</c> key ring, bound to the team ID.</summary>
public sealed class TeamWebhookSecretProtector {
    private const string Version = "v1";
    private readonly AesGcmKeyRing ring;

    public TeamWebhookSecretProtector(IConfiguration configuration) {
        ring = new AesGcmKeyRing(configuration, "WebhookSecrets", new KeyRingWording("webhook", "webhook secret"));
    }

    public bool IsEnabled => ring.IsEnabled;
    public bool IsConfigured => ring.IsConfigured;

    public string Protect(Guid teamId, ReadOnlySpan<byte> plaintext) {
        EnsureConfigured();
        if (teamId == Guid.Empty) throw new ArgumentException("Team ID is required.", nameof(teamId));
        if (plaintext.Length < AesGcmKeyRing.KeySize) throw new ArgumentException("Webhook signing secrets must contain at least 32 bytes.", nameof(plaintext));
        return ring.Protect(plaintext, AssociatedData(teamId));
    }

    public byte[] Unprotect(Guid teamId, string envelope) {
        EnsureConfigured();
        if (teamId == Guid.Empty) throw new ArgumentException("Team ID is required.", nameof(teamId));
        return ring.Unprotect(envelope, AssociatedData(teamId));
    }

    public string Rewrap(Guid teamId, string envelope) {
        var keyId = ring.ReadKeyId(envelope);
        var plaintext = Unprotect(teamId, envelope);
        try {
            return string.Equals(keyId, ring.CurrentKeyId, StringComparison.Ordinal) ? envelope : Protect(teamId, plaintext);
        } finally {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void EnsureConfigured() {
        if (!IsConfigured) throw new InvalidOperationException("Webhook secret encryption is not configured.");
    }

    private static byte[] AssociatedData(Guid teamId) => Encoding.UTF8.GetBytes($"AegisOps.TeamWebhook.{Version}.{teamId:D}");
}
