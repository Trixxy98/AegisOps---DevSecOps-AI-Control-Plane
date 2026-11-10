using System.Globalization;
using AegisOps.Domain.Deploy;
using AegisOps.Infrastructure.Observability;
using AegisOps.Infrastructure.Persistence;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AegisOps.Infrastructure.Docker;

/// <summary>
/// Worker-side runtime inspection for Docker-target environments (docs/06 §6). The API has no
/// Docker socket, so the Worker periodically, and right after each Docker deployment, records a
/// bounded snapshot of the containers it owns in <c>deploy.environment_runtime_snapshots</c>.
/// Only the target container (and its <c>-previous</c> backup) carrying this environment's
/// ownership labels is reported; environment variables, labels, mounts and any other container
/// on the host are never stored.
/// </summary>
public sealed partial class EnvironmentRuntimeMonitor(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<EnvironmentRuntimeMonitor> logger
) : BackgroundService {
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
    /// <summary>A snapshot older than two poll intervals means the Worker stopped observing.</summary>
    public static readonly TimeSpan StaleAfter = PollInterval * 2;
    internal static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(10);
    internal const int MaxContainers = 4;
    internal const int MaxPorts = 16;
    internal const int MaxText = 256;
    internal const int MaxEnvironments = 500;
    public const string DockerUnavailable = "Docker is unavailable to the Worker.";
    public const string InspectionFailed = "The Worker could not inspect the Docker daemon.";
    public const string MissingContainerName = "Docker target has no container name.";

    internal Func<IDockerRuntimeClient?> ClientFactory { get; init; } = DockerRuntimeClient.TryCreate;

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Environment runtime inspection failed ({ErrorType}, {SafeDetail}).")]
    private static partial void LogFailure(ILogger logger, string errorType, string safeDetail);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(PollInterval, time);
        try {
            do {
                try {
                    await RefreshAsync(null, stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                } catch (Exception exception) {
                    LogFailure(logger, exception.GetType().Name, JobQueueDepthService.SafeDetail(exception));
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Normal shutdown.
        }
    }

    public async Task<int> RefreshAsync(Guid? environmentId, CancellationToken cancellationToken) {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
        return await RefreshAsync(db, ClientFactory, time, environmentId, cancellationToken);
    }

    /// <summary>Best-effort refresh after a Docker deployment; never fails the deployment job.</summary>
    public static async Task RefreshAfterDeploymentAsync(IServiceScopeFactory scopes, Guid deploymentId, TimeProvider time, CancellationToken cancellationToken) {
        try {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            var environmentId = await db.Deployments.AsNoTracking()
                .Where(item => item.Id == deploymentId)
                .Select(item => (Guid?)item.EnvironmentId)
                .SingleOrDefaultAsync(cancellationToken);
            if (environmentId is null) return;
            await RefreshAsync(db, DockerRuntimeClient.TryCreate, time, environmentId, cancellationToken);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            // The periodic monitor retries; a failed snapshot must not change the deployment outcome.
        }
    }

    /// <summary>Records a snapshot for every Docker-target environment (or just one). Returns the number written.</summary>
    public static async Task<int> RefreshAsync(
        AegisOpsDbContext db,
        Func<IDockerRuntimeClient?> clientFactory,
        TimeProvider time,
        Guid? environmentId,
        CancellationToken cancellationToken
    ) {
        var query = from environment in db.Environments.AsNoTracking()
                    join project in db.Projects.AsNoTracking() on environment.ProjectId equals project.Id
                    select new { environment.Id, environment.Name, environment.Target, project.Slug };
        if (environmentId is { } only) query = query.Where(item => item.Id == only);
        var targets = (await query.Take(MaxEnvironments).ToListAsync(cancellationToken))
            .Where(item => string.Equals(item.Target.Type, "docker", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (targets.Length == 0) return 0;

        IDockerRuntimeClient? client;
        try {
            client = clientFactory();
        } catch (Exception) {
            client = null;
        }

        using (client) {
            foreach (var target in targets) {
                var snapshot = await db.EnvironmentRuntimeSnapshots.SingleOrDefaultAsync(item => item.EnvironmentId == target.Id, cancellationToken);
                if (snapshot is null) {
                    snapshot = EnvironmentRuntimeSnapshot.Create(target.Id);
                    db.EnvironmentRuntimeSnapshots.Add(snapshot);
                }

                if (client is null) {
                    snapshot.Unavailable(time.GetUtcNow(), DockerUnavailable);
                } else if (string.IsNullOrWhiteSpace(target.Target.ContainerName)) {
                    snapshot.Unavailable(time.GetUtcNow(), MissingContainerName);
                } else {
                    try {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(InspectionTimeout);
                        var containers = await InspectAsync(client, target.Slug, target.Name, target.Target.ContainerName, timeout.Token);
                        snapshot.Observe(time.GetUtcNow(), containers);
                    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                        throw;
                    } catch (Exception) {
                        // Docker errors can contain host paths or daemon details; store a fixed reason only.
                        snapshot.Unavailable(time.GetUtcNow(), InspectionFailed);
                    }
                }
            }
        }

        try {
            await db.SaveChangesAsync(cancellationToken);
        } catch (DbUpdateException) {
            // Another Worker replica inserted the same snapshot first; the next poll updates it.
            return 0;
        }

        return targets.Length;
    }

    /// <summary>
    /// Lists the target container and its rollback backup, keeps only those carrying this
    /// environment's ownership labels, and maps each to a redacted snapshot entry.
    /// </summary>
    public static async Task<IReadOnlyList<RuntimeContainer>> InspectAsync(
        IDockerRuntimeClient client,
        string projectSlug,
        string environmentName,
        string containerName,
        CancellationToken cancellationToken
    ) {
        var listed = await client.ListContainersAsync(containerName, cancellationToken);
        var owned = listed
            .Where(container => IsTargetName(container, containerName)
                && DockerDeploymentExecutor.OwnedBy(container, projectSlug, environmentName))
            .Take(MaxContainers)
            .ToArray();
        var result = new List<RuntimeContainer>(owned.Length);
        foreach (var container in owned) {
            ContainerInspectResponse? inspected = null;
            try {
                inspected = await client.InspectContainerAsync(container.ID, cancellationToken);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception) {
                // The container may have been removed between list and inspect; report the list view.
            }

            result.Add(Map(container, inspected, containerName));
        }

        return result.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    internal static bool IsTargetName(ContainerListResponse container, string containerName) =>
        NameOf(container) is { } name
        && (string.Equals(name, containerName, StringComparison.Ordinal)
            || string.Equals(name, containerName + "-previous", StringComparison.Ordinal));

    /// <summary>
    /// Copies only the allow-listed status fields. Environment variables, labels, mounts,
    /// commands and network details are deliberately never read.
    /// </summary>
    internal static RuntimeContainer Map(ContainerListResponse container, ContainerInspectResponse? inspected, string fallbackName) {
        var health = inspected?.State?.Health?.Status;
        return new RuntimeContainer {
            Name = Clip(NameOf(container) ?? fallbackName),
            State = Clip(FirstNonEmpty(inspected?.State?.Status, container.State) ?? "unknown"),
            Status = Clip(container.Status ?? ""),
            Health = string.IsNullOrWhiteSpace(health) || health == "none" ? null : Clip(health),
            Image = Clip(FirstNonEmpty(inspected?.Config?.Image, container.Image) ?? ""),
            ImageId = FirstNonEmpty(inspected?.Image, container.ImageID) is { } id ? Clip(id) : null,
            StartedAt = ParseStartedAt(inspected?.State?.StartedAt),
            Ports = (container.Ports ?? [])
                .Where(port => port.PublicPort > 0)
                // Docker lists IPv4 and IPv6 bindings of the same mapping separately.
                .DistinctBy(port => (port.PublicPort, port.PrivatePort, port.Type))
                .Select(port => Clip(string.Create(CultureInfo.InvariantCulture,
                    $"{(string.IsNullOrEmpty(port.IP) ? "0.0.0.0" : port.IP)}:{port.PublicPort}->{port.PrivatePort}/{port.Type}")))
                .Distinct(StringComparer.Ordinal)
                .Take(MaxPorts)
                .ToList(),
        };
    }

    /// <summary>Parses Docker's RFC 3339 timestamps, which carry nanoseconds (.NET accepts at most 7 digits).</summary>
    internal static DateTimeOffset? ParseStartedAt(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0) {
            var end = dot + 1;
            while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
            if (end - dot - 1 > 7) value = string.Concat(value.AsSpan(0, dot + 8), value.AsSpan(end));
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            && parsed.Year > 1
                ? parsed.ToUniversalTime()
                : null;
    }

    private static string? NameOf(ContainerListResponse container) =>
        container.Names?.Select(name => name.TrimStart('/')).FirstOrDefault(name => name.Length > 0);

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : null;

    private static string Clip(string value) => value.Length <= MaxText ? value : value[..MaxText];
}
