using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AegisOps.Api.Identity;

public static class ApiKeyAuthenticationDefaults {
    public const string Scheme = "ApiKey";
    public const string SmartScheme = "Smart";
}

public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions> {
    private readonly IServiceScopeFactory _scopes;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IServiceScopeFactory scopes
    ) : base(options, logger, encoder) {
        _scopes = scopes;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync() {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) {
            return AuthenticateResult.NoResult();
        }

        var plaintext = header["Bearer ".Length..].Trim();
        if (!ApiKeyGenerator.TryParse(plaintext, out var prefix, out var secret)) {
            return AuthenticateResult.Fail("API key is invalid");
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var cancellationToken = Request.HttpContext.RequestAborted;

        var apiKey = await db.ApiKeys.SingleOrDefaultAsync(
            item =>item.KeyPrefix == prefix,
            cancellationToken
        );


        if (apiKey is null || !HashesMatch(apiKey.KeyHash, ApiKeyHasher.Hash(secret))) {
            return AuthenticateResult.Fail("API key is invalid.");
        }

        var now = time.GetUtcNow();
        if (!apiKey.IsActive(now)) {
            return AuthenticateResult.Fail("API key is expired.");
        }

        apiKey.MarkUsed(now);
        await db.SaveChangesAsync(cancellationToken);

        var claims = new List<Claim> {
            new("sub", $"apiKey:{apiKey.Id}"),
            new("project", apiKey.ProjectId.ToString()),
            new("actor_type", "apiKey"),
        };
        claims.AddRange(apiKey.Scopes.Select(item => new Claim("scope", item)));

        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ApiKeyAuthenticationDefaults.Scheme);
        return AuthenticateResult.Success(ticket);
    }

    private static bool HashesMatch(string stored, string computed) {
        var left = Encoding.UTF8.GetBytes(stored);
        var right = Encoding.UTF8.GetBytes(computed);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}