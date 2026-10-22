using System.Text.Json;
using AegisOps.Application.Idempotency;
using StackExchange.Redis;

namespace AegisOps.Infrastructure.Redis;

public sealed class RedisIdempotencyStore : IIdempotencyStore {
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string SaveIfOwner = "if redis.call('GET', KEYS[1]) == ARGV[1] then redis.call('SET', KEYS[1], ARGV[2], 'EX', 86400); return 1 else return 0 end";
    private readonly IConnectionMultiplexer? _redis;

    public RedisIdempotencyStore(IConnectionMultiplexer? redis = null) {
        _redis = redis;
    }

    public async Task<StoredResponse?> FindAsync(string principal, string key, CancellationToken cancellationToken) {
        var database = Database(key);
        if (database is null) {
            return null;
        }

        try {
            var value = await database.StringGetAsync(CacheKey(principal, key));
            if (value.IsNullOrEmpty) {
                return null;
            }

            var stored = JsonSerializer.Deserialize<StoredPayload>(value.ToString(), Json);
            if (stored is null || stored.Pending || stored.Body is null) {
                return null;
            }

            return new StoredResponse(stored.StatusCode, stored.Body, stored.RequestIdentity);
        } catch (Exception exception) when (exception is RedisException or JsonException) {
            if (IsDeployment(key)) throw new IdempotencyUnavailableException(exception);
            return null;
        }
    }

    public async Task<string?> TryBeginAsync(string principal, string key, CancellationToken cancellationToken) {
        var owner = Guid.NewGuid().ToString("N");
        var database = Database(key);
        if (database is null) {
            return owner;
        }

        try {
            return await database.StringSetAsync(
                CacheKey(principal, key),
                Pending(owner),
                TimeSpan.FromMinutes(2),
                When.NotExists
            ) ? owner : null;
        } catch (RedisException exception) {
            if (IsDeployment(key)) throw new IdempotencyUnavailableException(exception);
            return owner;
        }
    }

    public async Task<bool> SaveAsync(string principal, string key, string owner, StoredResponse response, CancellationToken cancellationToken) {
        var database = Database(key);
        if (database is null) {
            return false;
        }

        try {
            var result = await database.ScriptEvaluateAsync(SaveIfOwner,
                [CacheKey(principal, key)],
                [Pending(owner), JsonSerializer.Serialize(new StoredPayload(response.StatusCode, response.Body, false, response.RequestIdentity), Json)]);
            return (int)result == 1;
        } catch (RedisException exception) {
            if (IsDeployment(key)) throw new IdempotencyUnavailableException(exception);
            // Artifact creation retains its best-effort cache behavior.
            return false;
        }
    }

    private IDatabase? Database(string key) {
        if (_redis is null || !_redis.IsConnected) {
            if (IsDeployment(key)) throw new IdempotencyUnavailableException();
            return null;
        }

        try {
            return _redis.GetDatabase();
        } catch (RedisException exception) {
            if (IsDeployment(key)) throw new IdempotencyUnavailableException(exception);
            return null;
        }
    }

    private static bool IsDeployment(string key) => key.StartsWith("deployment:", StringComparison.Ordinal);
    private static string Pending(string owner) => JsonSerializer.Serialize(new StoredPayload(0, null, true, Owner: owner), Json);
    private static string CacheKey(string principal, string key) => $"idem:{principal}:{key}";

    private sealed record StoredPayload(int StatusCode, string? Body, bool Pending, string? RequestIdentity = null, string? Owner = null);
}
