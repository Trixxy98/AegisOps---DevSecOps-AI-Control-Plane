using System.Text.Json;
using AegisOps.Application.Idempotency;
using StackExchange.Redis;

namespace AegisOps.Infrastructure.Redis;

public sealed class RedisIdempotencyStore : IIdempotencyStore {
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IConnectionMultiplexer? _redis;

    public RedisIdempotencyStore(IConnectionMultiplexer? redis = null) {
        _redis = redis;
    }

    public async Task<StoredResponse?> FindAsync(string principal, string key, CancellationToken cancellationToken) {
        var database = Database();
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

            return new StoredResponse(stored.StatusCode, stored.Body);
        } catch (RedisException) {
            return null;
        }
    }

    public async Task<bool> TryBeginAsync(string principal, string key, CancellationToken cancellationToken) {
        var database = Database();
        if (database is null) {
            return true;
        }

        try {
            return await database.StringSetAsync(
                CacheKey(principal, key),
                JsonSerializer.Serialize(new StoredPayload(0, null, true), Json),
                TimeSpan.FromMinutes(2),
                When.NotExists
            );
        } catch (RedisException) {
            return true;
        }
    }

    public async Task SaveAsync(string principal, string key, StoredResponse response, CancellationToken cancellationToken) {
        var database = Database();
        if (database is null) {
            return;
        }

        try {
            await database.StringSetAsync(
                CacheKey(principal, key),
                JsonSerializer.Serialize(new StoredPayload(response.StatusCode, response.Body, false), Json),
                TimeSpan.FromHours(24)
            );
        } catch (RedisException) {
            // PostgreSQL natural keys remain the fallback when Redis is down.
        }
    }

    private IDatabase? Database() {
        if (_redis is null || !_redis.IsConnected) {
            return null;
        }

        return _redis.GetDatabase();
    }

    private static string CacheKey(string principal, string key) => $"idem:{principal}:{key}";

    private sealed record StoredPayload(int StatusCode, string? Body, bool Pending);
}
