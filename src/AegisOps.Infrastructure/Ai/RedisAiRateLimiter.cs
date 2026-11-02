using StackExchange.Redis;

namespace AegisOps.Infrastructure.Ai;

public sealed class RedisAiRateLimiter(IConnectionMultiplexer? redis, TimeProvider time) : IAiRateLimiter {
    // One atomic script for both windows; no partial consumption on denial.
    private const string Script = """
        local now = tonumber(ARGV[1])
        local id = ARGV[2]
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - 60000)
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now - 86400000)
        local minute = redis.call('ZCARD', KEYS[1])
        local day = redis.call('ZCARD', KEYS[2])
        if minute >= tonumber(ARGV[3]) or day >= tonumber(ARGV[4]) then
            return {0, math.max(0, tonumber(ARGV[3]) - minute), math.max(0, tonumber(ARGV[4]) - day)}
        end
        redis.call('ZADD', KEYS[1], now, id)
        redis.call('ZADD', KEYS[2], now, id)
        redis.call('PEXPIRE', KEYS[1], 60000)
        redis.call('PEXPIRE', KEYS[2], 86400000)
        return {1, tonumber(ARGV[3]) - minute - 1, tonumber(ARGV[4]) - day - 1}
        """;
    public async Task<RateLimitDecision> Check(Guid userId, int minute, int day, CancellationToken ct) {
        if (redis is null) throw new InvalidOperationException("AI rate limiting requires Redis.");
        ct.ThrowIfCancellationRequested();
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var id = $"{now}:{Guid.NewGuid():N}";
        var rawResult = await redis.GetDatabase().ScriptEvaluateAsync(Script,
            [$"ai:rl:m:{userId:N}", $"ai:rl:d:{userId:N}"], [now, id, minute, day]).WaitAsync(ct);
        if (rawResult.IsNull) throw new InvalidOperationException("AI rate limiter returned no decision.");
        var result = (RedisResult[])rawResult!;
        if (result.Length != 3) throw new InvalidOperationException("AI rate limiter returned an invalid decision.");
        return new RateLimitDecision((int)result[0] == 1, (int)result[1], (int)result[2]);
    }
}
