using System.Security.Claims;
using System.Text;
using AegisOps.Domain.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AegisOps.Infrastructure.Identity;

public static class JwtAccessTokenFactory {
    public static string Create(
        User user,
        IReadOnlyCollection<string> roles,
        JwtOptions options,
        DateTimeOffset now
    ) {
        if (!user.IsActive) {
            throw new InvalidOperationException("Inactive users cannot receive an access token.");
        }

        if (string.IsNullOrWhiteSpace(user.Email)) {
            throw new ArgumentException("User email is required.", nameof(user));
        }

        if (string.IsNullOrWhiteSpace(options.Issuer) || string.IsNullOrWhiteSpace(options.Audience)) {
            throw new ArgumentException("Issuer and audience are required.", nameof(options));
        }

        if (options.AccessTokenMinutes <= 0) {
            throw new ArgumentException("Access token lifetime must be positive.", nameof(options));
        }

        var keyBytes = Encoding.UTF8.GetBytes(options.SigningKey);
        if (keyBytes.Length < 32) {
            throw new ArgumentException("Signing key must be at least 32 bytes.", nameof(options));
        }

        var claims = new List<Claim> {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
        };

        foreach (var role in roles.Where(role => !string.IsNullOrWhiteSpace(role)).Distinct(StringComparer.Ordinal)) {
            claims.Add(new Claim("role", role));
        }

        var descriptor = new SecurityTokenDescriptor {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(options.AccessTokenMinutes).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(keyBytes),
                SecurityAlgorithms.HmacSha256
            ),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}