using System.Security.Claims;
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
        HttpContext http
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
                containers = Array.Empty<ContainerRuntime>(),
            });
        }

        try {
            var containers = await DockerRuntimeInspector.ListAsync(
                project.Slug,
                environment.Name,
                environment.Target.ContainerName,
                cancellationToken);
            return Results.Ok(new {
                type = "docker",
                environment.Target.ContainerName,
                environment.Target.HostPort,
                environment.Target.HealthPath,
                containers,
            });
        } catch (Exception exception) {
            return Results.Problem(
                title: "Docker runtime could not be read.",
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
