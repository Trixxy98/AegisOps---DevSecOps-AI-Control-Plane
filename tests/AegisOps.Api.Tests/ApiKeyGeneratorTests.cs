using AegisOps.Domain.Identity;

namespace AegisOps.Api.Tests;

public sealed class ApiKeyGeneratorTests
{
    [Theory]
    [InlineData("aok_abcdefg__secret_with_under", "abcdefg_", "secret_with_under")]
    [InlineData("aok_ab_cdefg__secret", "ab_cdefg", "_secret")]
    public void TryParse_accepts_base64url_underscores_in_prefix_and_secret(
        string plaintext, string expectedPrefix, string expectedSecret)
    {
        Assert.True(ApiKeyGenerator.TryParse(plaintext, out var prefix, out var secret));
        Assert.Equal(expectedPrefix, prefix);
        Assert.Equal(expectedSecret, secret);
    }
}
