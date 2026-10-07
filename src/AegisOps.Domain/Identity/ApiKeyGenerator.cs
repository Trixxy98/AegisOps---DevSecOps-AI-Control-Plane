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

        var parts = plaintext.Trim().Split('_', 3);
        if (parts.Length != 3
            || !parts[0].Equals("aok", StringComparison.Ordinal)
            || parts[1].Length != 8
            || string.IsNullOrWhiteSpace(parts[2])) {
                return false;
            }

            prefix = parts[1];
            secret = parts[2];
            return true;
    }
}