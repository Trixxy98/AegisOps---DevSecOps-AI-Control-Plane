using StackExchange.Redis;

namespace AegisOps.Infrastructure.Deploy;

// Redis is a hint channel, not the timeline store. Clients always reload committed data from REST.
public static class DeploymentUpdates {
    public const string Channel = "aegisops:deployment-updates";

    public static async Task PublishAsync(IConnectionMultiplexer? redis, Guid deploymentId) {
        if (redis is null) return;
        try {
            await redis.GetSubscriber().PublishAsync(RedisChannel.Literal(Channel), deploymentId.ToString("D"));
        } catch (RedisException) {
            // Polling remains available when Redis is unavailable.
        }
    }
}
