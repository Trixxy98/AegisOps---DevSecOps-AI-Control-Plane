using System.Security.Cryptography;

namespace AegisOps.Infrastructure.GitHub;

/// <summary>GitHub's <c>X-Hub-Signature-256</c>: <c>sha256=</c> + lowercase hex HMAC-SHA256 of the raw request body.</summary>
public static class GitHubWebhookSignature {
    private const string Prefix = "sha256=";

    public static string Compute(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> body) =>
        Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(secret, body));

    /// <summary>Constant-time comparison of the expected and presented signatures. A missing or malformed header fails.</summary>
    public static bool Verify(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> body, string? header) {
        if (secret.IsEmpty || string.IsNullOrEmpty(header) || header.Length != Prefix.Length + 64 ||
            !header.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        byte[] presented;
        try {
            presented = Convert.FromHexString(header.AsSpan(Prefix.Length));
        } catch (FormatException) {
            return false;
        }

        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(secret, body, expected);
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
