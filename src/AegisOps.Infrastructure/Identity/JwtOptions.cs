namespace AegisOps.Infrastructure.Identity;

public sealed class JwtOptions {
    public const string SectionName = "Jwt";
    public string Issuer {get; init;} = string.Empty;
    public string Audience {get; init;} = string.Empty;
    public string SigningKey {get; init;} = string.Empty;
    public int AccessTokenMinutes {get; init;} = 15;
}