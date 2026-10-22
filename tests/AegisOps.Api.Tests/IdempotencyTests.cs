using System.Security.Claims;
using AegisOps.Api.Idempotency;
using AegisOps.Application.Idempotency;
using AegisOps.Infrastructure.Redis;
using Microsoft.AspNetCore.Http;
using StackExchange.Redis;

namespace AegisOps.Api.Tests;

public sealed class IdempotencyTests {
    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity([new Claim("sub", "user")], "test"));

    [Fact]
    public async Task ArtifactAndDeploymentKeysAreDisjointWithUserSuppliedPrefix() {
        var store = new MemoryStore();
        var artifact = Context("deployment:retry");
        var deployment = Context("retry");
        Assert.Null(await IdempotencyRequests.ReplayAsync(artifact, Principal, store));
        Assert.Null(await IdempotencyRequests.ReplayAsync(deployment, Principal, store, "request-1"));
        Assert.Equal(201, ((IStatusCodeHttpResult)await IdempotencyRequests.FinishAsync(artifact, store, 201, new { id = "artifact" })).StatusCode);
        Assert.Equal(202, ((IStatusCodeHttpResult)await IdempotencyRequests.FinishAsync(deployment, store, 202, new { id = "deployment" })).StatusCode);
        Assert.Equal("artifact:deployment:retry", artifact.Items["idempotency-key"]);
        Assert.Equal("deployment:retry", deployment.Items["idempotency-key"]);
        Assert.Equal(201, ((IStatusCodeHttpResult)(await IdempotencyRequests.ReplayAsync(Context("deployment:retry"), Principal, store))!).StatusCode);
        Assert.Equal(202, ((IStatusCodeHttpResult)(await IdempotencyRequests.ReplayAsync(Context("retry"), Principal, store, "request-1"))!).StatusCode);
    }

    [Fact]
    public async Task ExpiredOwnerCannotReplaceNewPendingOwnerOrCompletedResponse() {
        var store = new MemoryStore();
        var first = Context("retry");
        Assert.Null(await IdempotencyRequests.ReplayAsync(first, Principal, store, "request-1"));
        store.Expire("user", "deployment:retry");
        var second = Context("retry");
        Assert.Null(await IdempotencyRequests.ReplayAsync(second, Principal, store, "request-2"));
        Assert.Equal(503, ((IStatusCodeHttpResult)await IdempotencyRequests.FinishAsync(first, store, 202, new { id = "old" })).StatusCode);
        Assert.Equal(409, ((IStatusCodeHttpResult)(await IdempotencyRequests.ReplayAsync(Context("retry"), Principal, store, "request-1"))!).StatusCode);
        Assert.Equal(202, ((IStatusCodeHttpResult)await IdempotencyRequests.FinishAsync(second, store, 202, new { id = "new" })).StatusCode);
        Assert.Equal(503, ((IStatusCodeHttpResult)await IdempotencyRequests.FinishAsync(first, store, 202, new { id = "old" })).StatusCode);
        Assert.Equal("request-2", (await store.FindAsync("user", "deployment:retry", TestContext.Current.CancellationToken))!.RequestIdentity);
    }

    [Fact]
    public async Task DeploymentUnavailableFailsBeforeWorkAndAfterDatabaseSave() {
        var store = new MemoryStore { Unavailable = true };
        Assert.Equal(503, ((IStatusCodeHttpResult)(await IdempotencyRequests.ReplayAsync(Context("retry"), Principal, store, "request"))!).StatusCode);
        store.Unavailable = false;
        var context = Context("retry");
        Assert.Null(await IdempotencyRequests.ReplayAsync(context, Principal, store, "request"));
        store.Unavailable = true;
        Assert.Equal(503, ((IStatusCodeHttpResult)await IdempotencyRequests.FinishAsync(context, store, 202, new { id = "saved-to-db" })).StatusCode);
        store.Unavailable = false;
        Assert.Equal(409, ((IStatusCodeHttpResult)(await IdempotencyRequests.ReplayAsync(Context("retry"), Principal, store, "request"))!).StatusCode);
    }

    [Fact]
    public async Task ArtifactStillWorksWithoutRedis_DeploymentFailsClosed() {
        var store = new RedisIdempotencyStore();
        var artifact = Context("retry");
        Assert.Null(await IdempotencyRequests.ReplayAsync(artifact, Principal, store));
        Assert.Equal(201, ((IStatusCodeHttpResult)await IdempotencyRequests.FinishAsync(artifact, store, 201, new { id = "artifact" })).StatusCode);
        Assert.Equal(503, ((IStatusCodeHttpResult)(await IdempotencyRequests.ReplayAsync(Context("retry"), Principal, store, "request"))!).StatusCode);
    }

    [Fact]
    public async Task RedisSaveRequiresCurrentOwner() {
        var connection = System.Environment.GetEnvironmentVariable("AEGISOPS_TEST_REDIS");
        if (string.IsNullOrEmpty(connection)) return;
        using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
        var store = new RedisIdempotencyStore(redis);
        var key = $"deployment:test:{Guid.NewGuid():N}";
        var cacheKey = $"idem:test:{key}";
        try {
            var first = (await store.TryBeginAsync("test", key, TestContext.Current.CancellationToken))!;
            Assert.Null(await store.TryBeginAsync("test", key, TestContext.Current.CancellationToken));
            await redis.GetDatabase().KeyDeleteAsync(cacheKey); // simulate pending TTL expiry
            var second = (await store.TryBeginAsync("test", key, TestContext.Current.CancellationToken))!;
            Assert.False(await store.SaveAsync("test", key, first, new StoredResponse(202, "old"), TestContext.Current.CancellationToken));
            Assert.True(await store.SaveAsync("test", key, second, new StoredResponse(202, "new"), TestContext.Current.CancellationToken));
            Assert.False(await store.SaveAsync("test", key, first, new StoredResponse(202, "old"), TestContext.Current.CancellationToken));
            Assert.Equal("new", (await store.FindAsync("test", key, TestContext.Current.CancellationToken))!.Body);
        } finally {
            await redis.GetDatabase().KeyDeleteAsync(cacheKey);
        }
    }

    private static DefaultHttpContext Context(string key) {
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = key;
        return context;
    }

    private sealed class MemoryStore : IIdempotencyStore {
        private readonly Dictionary<(string, string), string> pending = new();
        private readonly Dictionary<(string, string), StoredResponse> completed = new();
        public bool Unavailable { get; set; }
        public void Expire(string principal, string key) => pending.Remove((principal, key));
        public Task<StoredResponse?> FindAsync(string principal, string key, CancellationToken cancellationToken) {
            if (Unavailable) throw new IdempotencyUnavailableException();
            return Task.FromResult(completed.GetValueOrDefault((principal, key)));
        }
        public Task<string?> TryBeginAsync(string principal, string key, CancellationToken cancellationToken) {
            if (Unavailable) throw new IdempotencyUnavailableException();
            if (pending.ContainsKey((principal, key)) || completed.ContainsKey((principal, key))) return Task.FromResult<string?>(null);
            var owner = Guid.NewGuid().ToString("N");
            pending[(principal, key)] = owner;
            return Task.FromResult<string?>(owner);
        }
        public Task<bool> SaveAsync(string principal, string key, string owner, StoredResponse response, CancellationToken cancellationToken) {
            if (Unavailable) throw new IdempotencyUnavailableException();
            if (!pending.TryGetValue((principal, key), out var current) || current != owner) return Task.FromResult(false);
            pending.Remove((principal, key));
            completed[(principal, key)] = response;
            return Task.FromResult(true);
        }
    }
}
