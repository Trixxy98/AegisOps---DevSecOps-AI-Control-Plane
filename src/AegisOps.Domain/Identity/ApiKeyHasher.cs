using System.Security.Cryptography;
using System.Text;

namespace AegisOps.Domain.Identity;

public static class ApiKeyHasher {
    public static string Hash(string secret) {
        if (string.IsNullOrWhiteSpace(secret)) {
            throw new ArgumentException("Secret is required.", nameof(secret));
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(bytes);
    }
}