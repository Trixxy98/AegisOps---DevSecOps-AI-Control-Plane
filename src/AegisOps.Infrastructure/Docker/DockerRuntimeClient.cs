using Docker.DotNet;
using Docker.DotNet.Models;

namespace AegisOps.Infrastructure.Docker;

/// <summary>
/// The read-only Docker operations runtime inspection needs. Kept narrow so the Worker's
/// snapshot logic can be tested without a daemon, and so inspection cannot mutate containers.
/// </summary>
public interface IDockerRuntimeClient : IDisposable {
    Task<IList<ContainerListResponse>> ListContainersAsync(string nameFilter, CancellationToken cancellationToken);
    Task<ContainerInspectResponse> InspectContainerAsync(string id, CancellationToken cancellationToken);
}

public sealed class DockerRuntimeClient(DockerClient client) : IDockerRuntimeClient {
    /// <summary>Returns null when the Worker has no Docker endpoint (for example no mounted socket).</summary>
    public static IDockerRuntimeClient? TryCreate() {
        var socket = DockerClientFactory.Socket();
        return DockerClientFactory.CanInspect(socket)
            ? new DockerRuntimeClient(new DockerClientConfiguration(socket).CreateClient())
            : null;
    }

    public Task<IList<ContainerListResponse>> ListContainersAsync(string nameFilter, CancellationToken cancellationToken) =>
        client.Containers.ListContainersAsync(new ContainersListParameters {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>> {
                ["name"] = new Dictionary<string, bool> { [nameFilter] = true },
            },
        }, cancellationToken);

    public Task<ContainerInspectResponse> InspectContainerAsync(string id, CancellationToken cancellationToken) =>
        client.Containers.InspectContainerAsync(id, cancellationToken);

    public void Dispose() => client.Dispose();
}
