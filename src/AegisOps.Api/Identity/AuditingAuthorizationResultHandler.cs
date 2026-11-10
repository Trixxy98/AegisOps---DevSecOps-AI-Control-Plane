using AegisOps.Application.Audit;
using AegisOps.Domain.Audit;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Identity;

/// <summary>
/// docs/09 section 5: a policy-level denial returns 403 Problem Details and writes one AuditEvent
/// (Action = "authorization.denied", Outcome = "Denied", ResourceType = "Endpoint"). The request body and
/// query string are never recorded. Audit failures are logged and never turn the 403 into a 500.
/// Challenges (401) and successes use the default behaviour.
/// </summary>
public sealed partial class AuditingAuthorizationResultHandler(
    ILogger<AuditingAuthorizationResultHandler> logger
) : IAuthorizationMiddlewareResultHandler {
    public const string DeniedAction = "authorization.denied";
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult
    ) {
        if (!authorizeResult.Forbidden) {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        await TryAuditAsync(context);
        await Results.Problem(
            title: "Forbidden.",
            detail: "You do not have permission to perform this action.",
            statusCode: StatusCodes.Status403Forbidden
        ).ExecuteAsync(context);
    }

    private async Task TryAuditAsync(HttpContext context) {
        try {
            var audit = context.RequestServices.GetService<IAuditWriter>();
            var db = context.RequestServices.GetService<AegisOpsDbContext>();
            if (audit is null || db is null) {
                LogAuditUnavailable(logger);
                return;
            }

            var (actorType, actorId, actorDisplay) = DescribeActor(context);
            var time = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            audit.Write(AuditEvent.Record(time.GetUtcNow(), actorType, actorId, actorDisplay,
                DeniedAction, "Endpoint", null, "Denied", null));
            await db.SaveChangesAsync(context.RequestAborted);
        } catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested) {
            LogAuditFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Authorization denial was not audited: audit services are not registered.")]
    private static partial void LogAuditUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to write the authorization denial audit event.")]
    private static partial void LogAuditFailed(ILogger logger, Exception exception);

    private static (string ActorType, Guid? ActorId, string ActorDisplay) DescribeActor(HttpContext context) {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true) {
            return ("Anonymous", null, "anonymous");
        }

        var subject = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? string.Empty;
        // ApiKeyAuthenticationHandler issues sub = "apiKey:<id>" and actor_type = "apiKey".
        const string apiKeyPrefix = "apiKey:";
        if (string.Equals(user.FindFirst("actor_type")?.Value, "apiKey", StringComparison.OrdinalIgnoreCase)
            || subject.StartsWith(apiKeyPrefix, StringComparison.OrdinalIgnoreCase)) {
            Guid? keyId = subject.StartsWith(apiKeyPrefix, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(subject.AsSpan(apiKeyPrefix.Length), out var parsedKey) ? parsedKey : null;
            return ("ApiKey", keyId, subject.Length == 0 ? "apiKey" : subject);
        }

        var userId = Guid.TryParse(subject, out var parsedUser) ? parsedUser : (Guid?)null;
        var display = user.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? user.FindFirst(JwtRegisteredClaimNames.Name)?.Value
            ?? (subject.Length == 0 ? "unknown" : subject);
        return ("User", userId, display);
    }
}
