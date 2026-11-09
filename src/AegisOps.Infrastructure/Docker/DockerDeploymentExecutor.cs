using System.Diagnostics;
using AegisOps.Application.Deploy;
using AegisOps.Application.Observability;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Security;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace AegisOps.Infrastructure.Docker;

public sealed record DockerExecution(bool Succeeded, string Message);

public static class DockerDeploymentExecutor {
    public static async Task<DockerExecution> ExecuteAsync(
        DeploymentTarget target,
        Artifact artifact,
        string projectSlug,
        string environmentName,
        Guid deploymentId,
        Func<DeploymentEventType, string, CancellationToken, Task> record,
        CancellationToken cancellationToken
    ) {
        using var activity = AegisOpsTracing.Source.StartActivity("Docker.Execute");
        activity?.SetTag("outcome", "Failed");
        activity?.SetStatus(ActivityStatusCode.Error);
        if (!string.Equals(target.Type, "docker", StringComparison.OrdinalIgnoreCase)) {
            return new DockerExecution(false, "Target is not a docker target.");
        }

        if (string.IsNullOrWhiteSpace(target.ContainerName) || target.HostPort < 1) {
            return new DockerExecution(false, "Docker target needs a container name and host port.");
        }

        using var client = DockerClientFactory.Create();
        var image = DockerImageRef.ForPull(artifact.ImageReference, artifact.ImageDigest);
        var platform = System.Environment.GetEnvironmentVariable("DOCKER_PLATFORM");
        try {
            await TraceStepAsync("Docker.PullImage", () => PullImageAsync(client, image, platform, cancellationToken));
        } catch (Exception exception) when (string.IsNullOrWhiteSpace(platform) && exception.Message.Contains("no matching manifest", StringComparison.OrdinalIgnoreCase)) {
            platform = "linux/amd64";
            try {
                await TraceStepAsync("Docker.PullImage", () => PullImageAsync(client, image, platform, cancellationToken));
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception retry) {
                return new DockerExecution(false, Clip($"Image pull failed: {retry.Message}"));
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception exception) {
            return new DockerExecution(false, Clip($"Image pull failed: {exception.Message}"));
        }

        await record(DeploymentEventType.ImagePulled, image, cancellationToken);
        var name = target.ContainerName;
        string? previousId = null;
        string? createdId = null;
        try {
            var previous = await FindAsync(client, name, cancellationToken);
            if (previous is not null) {
                if (!OwnedBy(previous, projectSlug, environmentName)) {
                    return new DockerExecution(false, $"Container {name} is not owned by this target; refusing to replace it.");
                }

                if (await FindAsync(client, name + "-previous", cancellationToken) is not null) {
                    return new DockerExecution(false, $"Backup {name}-previous already exists; reconcile it before retrying.");
                }

                await TraceStepAsync("Docker.StopPrevious", async () => {
                    await client.Containers.StopContainerAsync(previous.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, cancellationToken);
                    await client.Containers.RenameContainerAsync(previous.ID, new ContainerRenameParameters { NewName = name + "-previous" }, cancellationToken);
                });
                previousId = previous.ID;
            }

            var containerPort = target.ContainerPort < 1 ? 8080 : target.ContainerPort;
            var binding = $"{containerPort}/tcp";
            var created = await TraceStepAsync("Docker.CreateContainer", () => client.Containers.CreateContainerAsync(new CreateContainerParameters {
                Name = name,
                Image = image,
                Platform = platform,
                Env = ContainerEnvironment(environmentName),
                Labels = new Dictionary<string, string> {
                    ["aegisops.project"] = projectSlug,
                    ["aegisops.environment"] = environmentName,
                    ["aegisops.deployment"] = deploymentId.ToString("D"),
                    ["aegisops.artifact"] = artifact.Id.ToString("D"),
                },
                ExposedPorts = new Dictionary<string, EmptyStruct> { [binding] = default },
                HostConfig = new HostConfig {
                    PortBindings = new Dictionary<string, IList<PortBinding>> {
                        [binding] = [new PortBinding { HostPort = target.HostPort.ToString(System.Globalization.CultureInfo.InvariantCulture) }],
                    },
                },
            }, cancellationToken));
            createdId = created.ID;
            if (!string.IsNullOrWhiteSpace(target.Network)) {
                await TraceStepAsync("Docker.EnsureNetwork", () => EnsureNetworkAsync(client, target.Network, created.ID, cancellationToken));
            }

            await TraceStepAsync("Docker.StartContainer", () => client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken));
            await record(DeploymentEventType.ContainerStarted, name, cancellationToken);
            var path = string.IsNullOrWhiteSpace(target.HealthPath) ? "/health" : target.HealthPath;
            var timeout = target.HealthTimeoutSeconds < 1 ? 60 : target.HealthTimeoutSeconds;
            if (await TraceStepAsync("Docker.HealthCheck", () => HealthyAsync(target.HostPort, path, timeout, cancellationToken), healthy => healthy)) {
                await record(DeploymentEventType.HealthCheckPassed, $"GET {path} returned 200.", cancellationToken);
                if (previousId is not null) {
                    await TraceStepAsync("Docker.RemovePrevious", () => RemoveAsync(client, previousId, cancellationToken));
                }

                activity?.SetTag("outcome", "Succeeded");
                activity?.SetStatus(ActivityStatusCode.Ok);
                return new DockerExecution(true, "Container is healthy.");
            }

            await record(DeploymentEventType.HealthCheckFailed, $"GET {path} did not return 200 within {timeout} seconds.", cancellationToken);
            await TraceStepAsync("Docker.RemoveContainer", () => RemoveAsync(client, created.ID, cancellationToken));
            createdId = null;
            await RestoreAsync(client, previousId, name, record, cancellationToken);
            return new DockerExecution(false, "Health check failed and the new container was removed.");
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // A lost lease must not rollback containers belonging to a replacement owner.
            activity?.SetTag("outcome", "Cancelled");
            throw;
        } catch (Exception exception) {
            if (createdId is not null) {
                await TraceStepAsync("Docker.RemoveContainer", () => RemoveAsync(client, createdId, CancellationToken.None));
            }

            await RestoreAsync(client, previousId, name, record, CancellationToken.None);
            return new DockerExecution(false, Clip(exception.Message));
        }
    }

    // Step names and outcome are fixed; Docker identifiers, image names, URLs and errors
    // must not become trace attributes or exception events.
    private static async Task TraceStepAsync(string name, Func<Task> step) =>
        await TraceStepAsync(name, async () => { await step(); return true; });

    internal static async Task<T> TraceStepAsync<T>(string name, Func<Task<T>> step, Func<T, bool>? succeeded = null) {
        using var activity = AegisOpsTracing.Source.StartActivity(name);
        try {
            var result = await step();
            if (succeeded is not null && !succeeded(result)) activity?.SetStatus(ActivityStatusCode.Error);
            return result;
        } catch {
            // Status only: no exception event or description, which could carry Docker details.
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    private static async Task RestoreAsync(
        DockerClient client,
        string? previousId,
        string name,
        Func<DeploymentEventType, string, CancellationToken, Task> record,
        CancellationToken cancellationToken
    ) {
        if (previousId is null) {
            return;
        }

        await TraceStepAsync("Docker.RestorePrevious", async () => {
            await client.Containers.RenameContainerAsync(previousId, new ContainerRenameParameters { NewName = name }, cancellationToken);
            await client.Containers.StartContainerAsync(previousId, new ContainerStartParameters(), cancellationToken);
            await record(DeploymentEventType.RolledBack, $"Restored {name}.", cancellationToken);
        });
    }

    private static Task PullImageAsync(DockerClient client, string image, string? platform, CancellationToken cancellationToken) {
        var parameters = Pull(image);
        if (!string.IsNullOrWhiteSpace(platform)) {
            parameters.Platform = platform;
        }

        return client.Images.CreateImageAsync(parameters, Auth() ?? new AuthConfig(), new Progress<JSONMessage>(), cancellationToken);
    }

    private static ImagesCreateParameters Pull(string image) {
        var at = image.LastIndexOf('@');
        if (at <= 0 || at == image.Length - 1) {
            return new ImagesCreateParameters { FromImage = image };
        }

        return new ImagesCreateParameters {
            FromImage = image[..at],
            Tag = image[(at + 1)..],
        };
    }

    private static string Clip(string message) {
        var trimmed = message.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private static AuthConfig? Auth() {
        var user = System.Environment.GetEnvironmentVariable("DOCKER_GHCR_USER");
        var token = System.Environment.GetEnvironmentVariable("DOCKER_GHCR_TOKEN");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(token)) {
            return null;
        }

        return new AuthConfig { Username = user, Password = token, ServerAddress = "ghcr.io" };
    }

    public static bool OwnedBy(ContainerListResponse container, string projectSlug, string environmentName) {
        var labels = container.Labels;
        return labels is not null
            && labels.TryGetValue("aegisops.project", out var project) && project == projectSlug
            && labels.TryGetValue("aegisops.environment", out var environment) && environment == environmentName
            && labels.ContainsKey("aegisops.deployment");
    }

    // Use the environment of the deployment, not the Worker's own Development environment.
    // Set both names because DOTNET_ENVIRONMENT takes precedence for WebApplication.
    public static IList<string> ContainerEnvironment(string environmentName) {
        if (string.IsNullOrWhiteSpace(environmentName) || environmentName.Contains('=')) {
            throw new ArgumentException("A valid deployment environment name is required.", nameof(environmentName));
        }

        return [$"DOTNET_ENVIRONMENT={environmentName}", $"ASPNETCORE_ENVIRONMENT={environmentName}"];
    }

    private static async Task<ContainerListResponse?> FindAsync(DockerClient client, string name, CancellationToken cancellationToken) {
        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, cancellationToken);
        return containers.FirstOrDefault(container => container.Names.Any(item => item == "/" + name));
    }

    private static async Task RemoveAsync(DockerClient client, string id, CancellationToken cancellationToken) {
        try {
            await client.Containers.StopContainerAsync(id, new ContainerStopParameters { WaitBeforeKillSeconds = 5 }, cancellationToken);
        } catch (DockerContainerNotFoundException) {
            return;
        } catch (DockerApiException) {
            // The container may already be stopped.
        }

        await client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, cancellationToken);
    }

    private static async Task EnsureNetworkAsync(DockerClient client, string network, string containerId, CancellationToken cancellationToken) {
        try {
            await client.Networks.InspectNetworkAsync(network, cancellationToken);
        } catch (DockerNetworkNotFoundException) {
            await client.Networks.CreateNetworkAsync(new NetworksCreateParameters { Name = network }, cancellationToken);
        }

        await client.Networks.ConnectNetworkAsync(network, new NetworkConnectParameters { Container = containerId }, cancellationToken);
    }

    private static async Task<bool> HealthyAsync(int port, string path, int timeoutSeconds, CancellationToken cancellationToken) {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        // Worker runs inside Compose; published host ports are not on its loopback interface.
        var host = System.Environment.GetEnvironmentVariable("DOCKER_HEALTH_HOST") ?? "127.0.0.1";
        var url = $"http://{host}:{port}{(path.StartsWith('/') ? path : "/" + path)}";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                using var response = await client.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode) {
                    return true;
                }
            } catch (HttpRequestException) {
                // The process is still starting.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return false;
    }
}
