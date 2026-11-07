using System.Buffers.Text;
using System.Security.Cryptography;

namespace AegisOps.Domain.Identity;

public sealed record GeneratedApiKey(
    string Prefix,
    string Hash,
    string Plaintext
);

public static class ApiKeyGenerator {
    public static GeneratedApiKey Create() {
        var prefix = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(6));
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var plaintext = $"aok_{prefix}_{secret}";

        return new GeneratedApiKey(
            prefix,
            ApiKeyHasher.Hash(secret),
            plaintext
        );
    }

    public static bool TryParse(string plaintext, out string prefix, out string secret) {
        prefix = string.Empty;
        secret = string.Empty;

        if (string.IsNullOrWhiteSpace(plaintext)) {
            return false;
        }

        var value = plaintext.Trim();
        // Base64URL may contain '_', so locate the separator after the fixed-length prefix.
        if (!value.StartsWith("aok_", StringComparison.Ordinal)
            || value.Length <= 13
            || value[12] != '_') {
            return false;
        }

        prefix = value.Substring(4, 8);
        secret = value[13..];
        return !string.IsNullOrWhiteSpace(secret);
    }
}