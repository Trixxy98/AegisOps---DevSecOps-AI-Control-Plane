using System.Buffers.Text;
using System.Security.Cryptography;

namespace AegisOps.Domain.Identity;

public static class RefreshTokenGenerator {
    public static string Create() {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url.EncodeToString(bytes);
    }
}