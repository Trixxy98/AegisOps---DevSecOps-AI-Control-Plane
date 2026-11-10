using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using AegisOps.Application.Audit;
using AegisOps.Domain.Audit;
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

public sealed partial class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions> {
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
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var cancellationToken = Request.HttpContext.RequestAborted;

        // Only failures against an existing key are audited (a real key tried with a wrong secret, or
        // expired). Malformed or unknown keys need no credentials to send, so auditing them would let
        // anyone grow the immutable audit table; the 401 is enough.
        if (!ApiKeyGenerator.TryParse(plaintext, out var prefix, out var secret)) {
            return AuthenticateResult.Fail("API key is invalid");
        }

        var apiKey = await db.ApiKeys.SingleOrDefaultAsync(
            item =>item.KeyPrefix == prefix,
            cancellationToken
        );


        if (apiKey is null) {
            return AuthenticateResult.Fail("API key is invalid.");
        }

        if (!HashesMatch(apiKey.KeyHash, ApiKeyHasher.Hash(secret))) {
            await TryAuditFailureAsync(scope.ServiceProvider, db, time, prefix, apiKey.Id);
            return AuthenticateResult.Fail("API key is invalid.");
        }

        var now = time.GetUtcNow();
        if (!apiKey.IsActive(now)) {
            await TryAuditFailureAsync(scope.ServiceProvider, db, time, prefix, apiKey.Id);
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

    /// <summary>
    /// docs/09 §7: apikey.auth_failed. Records at most the parsed 8-character key prefix, never the secret or
    /// the header. A failing audit save is logged and never turns the 401 into a 500.
    /// </summary>
    private async Task TryAuditFailureAsync(IServiceProvider services, AegisOpsDbContext db, TimeProvider time, string prefix, Guid apiKeyId) {
        try {
            var audit = services.GetService<IAuditWriter>();
            if (audit is null) return;
            var display = prefix[..Math.Min(8, prefix.Length)];
            audit.Write(AuditEvent.Record(time.GetUtcNow(), "ApiKey", apiKeyId, display,
                "apikey.auth_failed", "ApiKey", apiKeyId, "Denied", null));
            await db.SaveChangesAsync(Request.HttpContext.RequestAborted);
        } catch (Exception exception) when (exception is not OperationCanceledException || !Request.HttpContext.RequestAborted.IsCancellationRequested) {
            LogAuditFailed(Logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to write the API-key authentication failure audit event.")]
    private static partial void LogAuditFailed(ILogger logger, Exception exception);

    private static bool HashesMatch(string stored, string computed) {
        var left = Encoding.UTF8.GetBytes(stored);
        var right = Encoding.UTF8.GetBytes(computed);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}