using System.Security.Cryptography;
using System.Text;

namespace AegisOps.Domain.Identity;

public static class RefreshTokenHasher {
    public static string Hash(string token) {
        if (string.IsNullOrWhiteSpace(token)) {
            throw new ArgumentException("Token is required.", nameof(token));
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }
}