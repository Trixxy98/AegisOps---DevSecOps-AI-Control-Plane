using System.Reflection;
using AegisOps.Infrastructure.Deploy;
using StackExchange.Redis;

namespace AegisOps.Api.Tests;

public sealed class DeploymentUpdatesTests {
    [Fact]
    public async Task PublishAsync_timeout_does_not_fail_committed_deployment() {
        var redis = DispatchProxy.Create<IConnectionMultiplexer, TimeoutMultiplexerProxy>();
        await DeploymentUpdates.PublishAsync(redis, Guid.NewGuid());
    }

    public class TimeoutMultiplexerProxy : DispatchProxy {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            if (targetMethod?.Name == nameof(IConnectionMultiplexer.GetSubscriber))
                return DispatchProxy.Create<ISubscriber, TimeoutSubscriberProxy>();
            throw new InvalidOperationException($"Unexpected Redis call: {targetMethod?.Name}");
        }
    }

    public class TimeoutSubscriberProxy : DispatchProxy {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            if (targetMethod?.Name == nameof(ISubscriber.PublishAsync))
                return Task.FromException<long>(new TimeoutException("Redis publish timed out"));
            throw new InvalidOperationException($"Unexpected subscriber call: {targetMethod?.Name}");
        }
    }
}
