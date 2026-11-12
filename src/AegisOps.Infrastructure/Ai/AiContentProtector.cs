using System.Security.Cryptography;
using System.Text;
using AegisOps.Domain.Ai;
using AegisOps.Infrastructure.Deploy;
using Microsoft.Extensions.Configuration;

namespace AegisOps.Infrastructure.Ai;

/// <summary>
/// Encrypts stored AI content (<c>ai.ai_requests.prompt_content</c>/<c>response_content</c>, only written when an AI
/// policy has <c>StoreContent</c>) with the <c>AiContent</c> AES-256-GCM key ring (docs/08, docs/17 §5). The request ID
/// and the column are the associated data, so a ciphertext copied to another row or column does not decrypt.
/// </summary>
public sealed class AiContentProtector {
    public const string PromptField = AiRequest.PromptContentField;
    public const string ResponseField = AiRequest.ResponseContentField;
    private const string Version = "v1";
    private readonly AesGcmKeyRing ring;

    public AiContentProtector(IConfiguration configuration) {
        ring = new AesGcmKeyRing(configuration, "AiContent", new KeyRingWording("AI content", "AI content"));
    }

    /// <summary>True when new content may be stored (<c>AiContent:Enabled</c> and a current key).</summary>
    public bool CanProtect => ring.IsEnabled && ring.IsConfigured;
    public bool IsConfigured => ring.IsConfigured;

    public string Protect(Guid requestId, string field, string content) {
        ArgumentNullException.ThrowIfNull(content);
        var plaintext = Encoding.UTF8.GetBytes(content);
        try {
            return ring.Protect(plaintext, AssociatedData(requestId, field));
        } finally {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string Unprotect(Guid requestId, string field, string envelope) {
        var plaintext = ring.Unprotect(envelope, AssociatedData(requestId, field));
        try {
            return Encoding.UTF8.GetString(plaintext);
        } finally {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>The key ID an envelope was written with (for rotation checks).</summary>
    public string ReadKeyId(string envelope) => ring.ReadKeyId(envelope);

    private static byte[] AssociatedData(Guid requestId, string field) {
        if (requestId == Guid.Empty) throw new ArgumentException("AI request ID is required.", nameof(requestId));
        if (field is not (PromptField or ResponseField)) throw new ArgumentException("AI content field is invalid.", nameof(field));
        return Encoding.UTF8.GetBytes($"AegisOps.AiRequest.{Version}.{requestId:D}.{field}");
    }
}
