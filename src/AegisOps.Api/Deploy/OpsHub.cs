using System.Collections.Concurrent;
using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace AegisOps.Api.Deploy;

// No project groups: a membership/role change must take effect without waiting for reconnect.
[Authorize]
public sealed class OpsHub(DeploymentConnections connections, UserManager<User> users) : Hub {
    public override async Task OnConnectedAsync() {
        var principal = Context.User!;
        if (principal.FindFirst("actor_type") is not null ||
            (await DeploymentAccess.ResolveAsync(principal, users)).Error is not null) {
            Context.Abort();
            return;
        }
        connections.Add(Context.ConnectionId, principal);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception) {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

public sealed class DeploymentConnections {
    private readonly ConcurrentDictionary<string, ClaimsPrincipal> clients = new();
    public void Add(string id, ClaimsPrincipal principal) => clients[id] = principal;
    public void Remove(string id) => clients.TryRemove(id, out _);
    public KeyValuePair<string, ClaimsPrincipal>[] Snapshot() => clients.ToArray();
}

public sealed partial class DeploymentUpdateListener(
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopes,
    IHubContext<OpsHub> hub,
    DeploymentConnections connections,
    ILogger<DeploymentUpdateListener> logger) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var subscriber = redis.GetSubscriber();
        await subscriber.SubscribeAsync(RedisChannel.Literal(DeploymentUpdates.Channel), async (_, value) => {
            if (!Guid.TryParse(value.ToString(), out var id)) return;
            try {
                await DispatchAsync(id, stoppingToken);
            } catch (Exception error) when (!stoppingToken.IsCancellationRequested) {
                DeliveryFailed(logger, error);
            }
        });
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await subscriber.UnsubscribeAsync(RedisChannel.Literal(DeploymentUpdates.Channel)); }
    }

    public async Task DispatchAsync(Guid id, CancellationToken token = default) {
        foreach (var (connectionId, principal) in connections.Snapshot()) {
            if (principal.FindFirst("actor_type") is not null) continue;
            await using var scope = scopes.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            var access = await DeploymentAccess.ResolveAsync(principal, users);
            if (access.Error is not null || !await access.Scope(db.Deployments.AsNoTracking(), db)
                    .AnyAsync(item => item.Id == id, token)) continue;
            await hub.Clients.Client(connectionId).SendCoreAsync("DeploymentChanged", [id], token);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deployment update delivery failed")]
    private static partial void DeliveryFailed(ILogger logger, Exception error);
}
