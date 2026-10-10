using System.Security.Claims;
using System.Text.Json;
using AegisOps.Application.Idempotency;

namespace AegisOps.Api.Idempotency;

public static class IdempotencyRequests {
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<IResult?> ReplayAsync(
        HttpContext http,
        ClaimsPrincipal principal,
        IIdempotencyStore store
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
        http.Items["idempotency-key"] = key;
        var stored = await store.FindAsync(principalId, key, http.RequestAborted);
        if (stored is not null) {
            return Results.Content(stored.Body, "application/json", statusCode: stored.StatusCode);
        }

        var claimed = await store.TryBeginAsync(principalId, key, http.RequestAborted);
        if (!claimed) {
            return Results.Problem(title: "Idempotency-Key is already in progress.", statusCode: StatusCodes.Status409Conflict);
        }

        return null;
    }

    public static async Task<IResult> FinishAsync(
        HttpContext http,
        IIdempotencyStore store,
        int statusCode,
        object body
    ) {
        var json = JsonSerializer.Serialize(body, Json);
        if (http.Items["idempotency-principal"] is string principal && http.Items["idempotency-key"] is string key) {
            await store.SaveAsync(principal, key, new StoredResponse(statusCode, json), http.RequestAborted);
        }

        return Results.Content(json, "application/json", statusCode: statusCode);
    }
}
