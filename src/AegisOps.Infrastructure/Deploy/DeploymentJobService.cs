using AegisOps.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace AegisOps.Infrastructure.Deploy;

public sealed class DeploymentJobService : BackgroundService {
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly IConnectionMultiplexer? _redis;

    public DeploymentJobService(IServiceScopeFactory scopes, TimeProvider time, IConnectionMultiplexer? redis = null) {
        _scopes = scopes;
        _time = time;
        _redis = redis;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            try {
                await using var scope = _scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
                var worked = await DeploymentProcessor.TryProcessOneAsync(
                    db,
                    _scopes,
                    _time,
                    Environment.MachineName,
                    stoppingToken, _redis);
                if (!worked) {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            } catch {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
