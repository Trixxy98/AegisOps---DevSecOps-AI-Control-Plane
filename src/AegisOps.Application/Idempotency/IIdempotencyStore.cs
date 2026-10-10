namespace AegisOps.Application.Idempotency;

public sealed record StoredResponse(int StatusCode, string Body);

public interface IIdempotencyStore {
    Task<StoredResponse?> FindAsync(string principal, string key, CancellationToken cancellationToken);

    Task<bool> TryBeginAsync(string principal, string key, CancellationToken cancellationToken);

    Task SaveAsync(string principal, string key, StoredResponse response, CancellationToken cancellationToken);
}
