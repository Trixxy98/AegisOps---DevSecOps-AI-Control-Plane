using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AegisOps.Application.Audit;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Jobs;
using AegisOps.Infrastructure.GitHub;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Integrations;

/// <summary>
/// <c>POST /api/v1/integrations/github/webhook</c> (docs/github-deployment-gate.md, docs/10 §2.11). Anonymous: the
/// <c>X-Hub-Signature-256</c> HMAC over the raw body is the authentication and is checked before anything is parsed.
/// Only <c>deployment_protection_rule</c> / <c>requested</c> does work: the gate row and a durable
/// <c>GitHubGateEvaluate</c> job are written in one transaction and the answer is 202 immediately; the Worker maps and
/// decides. <c>ping</c> and other events get 204. A replayed <c>X-GitHub-Delivery</c> is a no-op (202).
/// </summary>
public static partial class GitHubWebhook {
    public const string Route = "/api/v1/integrations/github/webhook";
    public const int MaxBodyBytes = 1024 * 1024;

    [GeneratedRegex(@"/repos/[^/]+/[^/]+/actions/runs/(?<run>\d{1,19})/deployment_protection_rule/?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CallbackPath();

    public static async Task<IResult> Handle(
        HttpContext http,
        GitHubAppSettings settings,
        AegisOpsDbContext db,
        IAuditWriter audit,
        TimeProvider time,
        GitHubWebhookRejectionThrottle throttle,
        ILoggerFactory loggers
    ) {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(throttle);
        ArgumentNullException.ThrowIfNull(loggers);
        var logger = loggers.CreateLogger("AegisOps.Api.Integrations.GitHubWebhook");
        var cancellationToken = http.RequestAborted;
        // Disabled integration: the route does not exist as far as callers can tell.
        if (!settings.CanVerifyWebhooks) return Results.NotFound();

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
            sizeFeature.MaxRequestBodySize = MaxBodyBytes;
        if (http.Request.ContentLength > MaxBodyBytes)
            return Results.Problem(title: "Payload too large.", statusCode: StatusCodes.Status413PayloadTooLarge);
        var body = await ReadBodyAsync(http.Request.Body, cancellationToken);
        if (body is null) return Results.Problem(title: "Payload too large.", statusCode: StatusCodes.Status413PayloadTooLarge);

        if (!settings.VerifySignature(body, http.Request.Headers["X-Hub-Signature-256"].ToString())) {
            LogRejected(logger);
            if (throttle.ShouldAudit()) {
                // Throttled so unsigned floods cannot grow the immutable audit table (docs/17).
                audit.Write(AuditEvent.Record(time.GetUtcNow(), "Anonymous", null, "anonymous", "github.webhook_rejected", "Endpoint", null, "Denied", null));
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.Problem(title: "Invalid signature.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var eventName = http.Request.Headers["X-GitHub-Event"].ToString();
        if (!string.Equals(eventName, "deployment_protection_rule", StringComparison.Ordinal)) {
            // ping (sent when the App's webhook is saved) and any other subscribed event: acknowledged, nothing to do.
            return Results.NoContent();
        }

        var deliveryId = http.Request.Headers["X-GitHub-Delivery"].ToString().Trim();
        if (deliveryId.Length is 0 or > 64)
            return Results.Problem(title: "X-GitHub-Delivery is required.", statusCode: StatusCodes.Status400BadRequest);

        Request? request;
        try {
            request = Parse(body);
        } catch (JsonException) {
            request = null;
        }

        if (request is null) return Results.Problem(title: "Payload is not a deployment protection rule request.", statusCode: StatusCodes.Status400BadRequest);
        if (!string.Equals(request.Action, "requested", StringComparison.Ordinal)) return Results.NoContent();
        if (request.Invalid is not null) return Results.Problem(title: request.Invalid, statusCode: StatusCodes.Status400BadRequest);

        if (await db.GitHubDeploymentGates.AsNoTracking().AnyAsync(item => item.DeliveryId == deliveryId, cancellationToken)) {
            LogReplayed(logger);
            return Results.Accepted();
        }

        var now = time.GetUtcNow();
        var gate = GitHubDeploymentGate.Receive(deliveryId, now, request.InstallationId, request.Repository!, request.RunId,
            request.Environment!, request.Sha!, request.Ref, request.CallbackUrl!, request.RunUrl!);
        db.GitHubDeploymentGates.Add(gate);
        db.Jobs.Add(Job.Enqueue(JobType.GitHubGateEvaluate, AegisOpsDbContext.GitHubGatePayload(gate.Id, gate.CorrelationId), now, gate.CorrelationId));
        audit.Write(AuditEvent.Record(now, GitHubGateProcessor.ActorType, null, GitHubGateProcessor.ActorDisplay, "github.webhook_received",
            "GitHubDeploymentGate", gate.Id, "Accepted", gate.CorrelationId));
        try {
            await db.SaveChangesAsync(cancellationToken);
        } catch (DbUpdateException) {
            // A concurrent redelivery won the unique delivery index: that one is the accepted request.
            db.ChangeTracker.Clear();
            if (await db.GitHubDeploymentGates.AsNoTracking().AnyAsync(item => item.DeliveryId == deliveryId, cancellationToken)) return Results.Accepted();
            throw;
        }

        // Routing data only: never the body, signature or installation details.
        LogAccepted(logger, gate.RepositoryFullName, gate.EnvironmentName, gate.CommitSha[..Math.Min(12, gate.CommitSha.Length)]);
        return Results.Accepted();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub webhook rejected: missing or invalid signature.")]
    private static partial void LogRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub delivery replayed; already accepted.")]
    private static partial void LogReplayed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub deployment protection rule accepted for {Repository} environment {Environment} commit {Sha}")]
    private static partial void LogAccepted(ILogger logger, string repository, string environment, string sha);

    private static async Task<byte[]?> ReadBodyAsync(Stream body, CancellationToken cancellationToken) {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0) {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private sealed record Request(string? Action, long InstallationId, string? Repository, string? Environment, string? Sha, string? Ref,
        string? CallbackUrl, long RunId, string? RunUrl, string? Invalid);

    private static Request? Parse(byte[] body) {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var action = Text(root, "action");
        var installation = root.TryGetProperty("installation", out var install) && install.ValueKind == JsonValueKind.Object &&
            install.TryGetProperty("id", out var installId) && installId.TryGetInt64(out var parsedInstall) ? parsedInstall : 0;
        string? repository = null, htmlUrl = null;
        if (root.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object) {
            repository = Text(repo, "full_name");
            htmlUrl = Text(repo, "html_url");
        }

        var environment = Text(root, "environment");
        string? sha = null, reference = null;
        if (root.TryGetProperty("deployment", out var deployment) && deployment.ValueKind == JsonValueKind.Object) {
            sha = Text(deployment, "sha");
            reference = Text(deployment, "ref");
        }

        sha ??= Text(root, "sha");
        reference ??= Text(root, "ref");
        var callback = Text(root, "deployment_callback_url");
        long runId = 0;
        if (callback is not null && Uri.TryCreate(callback, UriKind.Absolute, out var callbackUri) && callbackUri.Scheme == Uri.UriSchemeHttps &&
            CallbackPath().Match(callbackUri.AbsolutePath) is { Success: true } match)
            long.TryParse(match.Groups["run"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out runId);

        string? invalid = null;
        if (installation <= 0) invalid = "installation.id is missing.";
        else if (repository is null || repository.Length > 300 || repository.Count(character => character == '/') != 1) invalid = "repository.full_name is missing.";
        else if (environment is null || environment.Length > 255) invalid = "environment is missing.";
        else if (sha is null || sha.Length is < 7 or > 64 || !sha.All(Uri.IsHexDigit)) invalid = "deployment.sha is missing.";
        else if (runId <= 0 || callback!.Length > 500) invalid = "deployment_callback_url is missing or not a workflow run URL.";

        var runUrl = htmlUrl is not null && Uri.TryCreate(htmlUrl, UriKind.Absolute, out var html) && html.Scheme == Uri.UriSchemeHttps
            ? string.Create(CultureInfo.InvariantCulture, $"{html.AbsoluteUri.TrimEnd('/')}/actions/runs/{runId}")
            : string.Create(CultureInfo.InvariantCulture, $"https://github.com/{repository}/actions/runs/{runId}");
        if (runUrl.Length > 500) invalid ??= "repository.html_url is too long.";
        return new Request(action, installation, repository, environment, sha, reference, callback, runId, runUrl, invalid);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;
}

/// <summary>
/// Audits at most one rejected (bad signature) GitHub webhook per interval. Every rejection is still logged and answered
/// with 401; the throttle only keeps unauthenticated traffic from growing the append-only audit table.
/// </summary>
public sealed class GitHubWebhookRejectionThrottle(TimeProvider time) {
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private long nextAuditTicks;

    public bool ShouldAudit() {
        var now = time.GetUtcNow().UtcTicks;
        var next = Interlocked.Read(ref nextAuditTicks);
        return now >= next && Interlocked.CompareExchange(ref nextAuditTicks, now + Interval.Ticks, next) == next;
    }
}
