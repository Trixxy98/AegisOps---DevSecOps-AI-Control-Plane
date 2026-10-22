using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisOps.Application.Idempotency;

namespace AegisOps.Api.Idempotency;

public static class IdempotencyRequests {
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string DeploymentIdentity(string operation, object request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { operation, request }, Json))));

    public static async Task<IResult?> ReplayAsync(
        HttpContext http,
        ClaimsPrincipal principal,
        IIdempotencyStore store,
        string? deploymentIdentity = null
    ) {
        var key = http.Request.Headers["Idempotency-Key"].ToString().Trim();
        var apiKey = string.Equals(principal.FindFirst("actor_type")?.Value, "apiKey", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(key)) {
            return apiKey
                ? Results.Problem(title: "Idempotency-Key is required.", statusCode: StatusCodes.Status400BadRequest)
                : null;
        }

        if (key.Length > 128) {
            return Results.Problem(title: "Idempotency-Key must be at most 128 characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        var principalId = principal.FindFirst("sub")?.Value ?? "anonymous";
        http.Items["idempotency-principal"] = principalId;
        // Separate both namespaces, even when a caller supplies a key beginning with "deployment:".
        var storeKey = deploymentIdentity is null ? $"artifact:{key}" : $"deployment:{key}";
        http.Items["idempotency-key"] = storeKey;
        if (deploymentIdentity is not null) http.Items["idempotency-identity"] = deploymentIdentity;
        StoredResponse? stored;
        string? owner;
        try {
            stored = await store.FindAsync(principalId, storeKey, http.RequestAborted);
            if (stored is null) {
                owner = await store.TryBeginAsync(principalId, storeKey, http.RequestAborted);
            } else {
                owner = null;
            }
        } catch (IdempotencyUnavailableException) {
            return Unavailable();
        }
        if (stored is not null) {
            if (deploymentIdentity is not null && stored.RequestIdentity != deploymentIdentity) {
                return Results.Problem(title: "Idempotency-Key was used for a different deployment request.", statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Content(stored.Body, "application/json", statusCode: stored.StatusCode);
        }

        if (owner is null) {
            return Results.Problem(title: "Idempotency-Key is already in progress.", statusCode: StatusCodes.Status409Conflict);
        }

        http.Items["idempotency-owner"] = owner;
        return null;
    }

    public static async Task<IResult> FinishAsync(
        HttpContext http,
        IIdempotencyStore store,
        int statusCode,
        object body
    ) {
        var json = JsonSerializer.Serialize(body, Json);
        if (http.Items["idempotency-principal"] is string principal && http.Items["idempotency-key"] is string key
            && http.Items["idempotency-owner"] is string owner) {
            try {
                var saved = await store.SaveAsync(principal, key, owner,
                    new StoredResponse(statusCode, json, http.Items["idempotency-identity"] as string), http.RequestAborted);
                if (!saved && http.Items["idempotency-identity"] is not null) return Unavailable();
            } catch (IdempotencyUnavailableException) {
                return Unavailable();
            }
        }

        return Results.Content(json, "application/json", statusCode: statusCode);
    }

    private static IResult Unavailable() => Results.Problem(
        title: "Deployment idempotency is unavailable. The deployment may already have been created; check deployments before retrying.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
