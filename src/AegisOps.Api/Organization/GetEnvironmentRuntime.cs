using System.Security.Claims;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Docker;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public static class GetEnvironmentRuntime {
    public static async Task<IResult> Handle(
        string slug,
        Guid envId,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http,
        TimeProvider time
    ) {
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out var userId)) {
            return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsActive) {
            return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var cancellationToken = http.RequestAborted;
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == normalizedSlug, cancellationToken);
        if (project is null) {
            return Results.Problem(title: "Project was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var isAdmin = await users.IsInRoleAsync(user, "Admin");
        if (!isAdmin) {
            var isMember = await db.TeamMembers.AnyAsync(
                member => member.TeamId == project.TeamId && member.UserId == userId,
                cancellationToken);
            if (!isMember) {
                return Results.Problem(title: "Project was not found.", statusCode: StatusCodes.Status404NotFound);
            }
        }

        var environment = await db.Environments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == envId && item.ProjectId == project.Id, cancellationToken);
        if (environment is null) {
            return Results.Problem(title: "Environment was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        if (!string.Equals(environment.Target.Type, "docker", StringComparison.OrdinalIgnoreCase)) {
            return Results.Ok(new {
                type = environment.Target.Type,
                inspectionAvailable = false,
                reason = "noop target",
                observedAt = (DateTimeOffset?)null,
                stale = false,
                containers = Array.Empty<RuntimeContainerResponse>(),
            });
        }

        // The API deliberately has no Docker socket; the Worker records what it observed.
        var snapshot = await db.EnvironmentRuntimeSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(item => item.EnvironmentId == environment.Id, cancellationToken);
        if (snapshot is null) {
            return Results.Ok(new {
                type = "docker",
                environment.Target.ContainerName,
                environment.Target.HostPort,
                environment.Target.HealthPath,
                inspectionAvailable = false,
                reason = "The Worker has not recorded a runtime snapshot for this environment yet.",
                observedAt = (DateTimeOffset?)null,
                stale = true,
                containers = (IReadOnlyList<RuntimeContainerResponse>?)null,
            });
        }

        var stale = time.GetUtcNow() - snapshot.ObservedAt > EnvironmentRuntimeMonitor.StaleAfter;
        return Results.Ok(new {
            type = "docker",
            environment.Target.ContainerName,
            environment.Target.HostPort,
            environment.Target.HealthPath,
            inspectionAvailable = snapshot.InspectionAvailable,
            reason = snapshot.InspectionAvailable
                ? (stale ? "The Worker has not refreshed this snapshot recently." : null)
                : snapshot.Reason,
            observedAt = (DateTimeOffset?)snapshot.ObservedAt,
            stale,
            containers = snapshot.InspectionAvailable
                ? snapshot.Containers.Select(RuntimeContainerResponse.From).ToArray()
                : (IReadOnlyList<RuntimeContainerResponse>?)null,
        });
    }
}

public sealed record RuntimeContainerResponse(
    string Name,
    string State,
    string Status,
    string? Health,
    string Image,
    string? ImageId,
    DateTimeOffset? StartedAt,
    IReadOnlyList<string> Ports
) {
    public static RuntimeContainerResponse From(RuntimeContainer container) => new(
        container.Name, container.State, container.Status, container.Health,
        container.Image, container.ImageId, container.StartedAt, container.Ports.ToArray());
}
