namespace AegisOps.Application.Idempotency;

public sealed record StoredResponse(int StatusCode, string Body, string? RequestIdentity = null);

public sealed class IdempotencyUnavailableException : Exception {
    public IdempotencyUnavailableException() : base("Deployment idempotency store is unavailable.") { }
    public IdempotencyUnavailableException(Exception inner) : base("Deployment idempotency store is unavailable.", inner) { }
}

public interface IIdempotencyStore {
    Task<StoredResponse?> FindAsync(string principal, string key, CancellationToken cancellationToken);

    // Returns the owner of the pending claim, or null if another request owns the key.
    Task<string?> TryBeginAsync(string principal, string key, CancellationToken cancellationToken);

    // A stale owner must never replace a newer claim or a completed response.
    Task<bool> SaveAsync(string principal, string key, string owner, StoredResponse response, CancellationToken cancellationToken);
}
