using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public sealed record UpdateEnvironmentTargetRequest(
    string? Type,
    bool? AllowApiKeyProduction,
    string? ContainerName,
    string? Network,
    int? HostPort,
    int? ContainerPort,
    string? HealthPath,
    int? HealthTimeoutSeconds
);

public sealed record UpdateEnvironmentRequest(
    string? Name,
    int? Order,
    UpdateEnvironmentTargetRequest? Target
);

public sealed record EnvironmentResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Tier,
    int Order,
    string TargetType,
    bool AllowApiKeyProduction,
    string? ContainerName,
    string? Network,
    int HostPort,
    int ContainerPort,
    string? HealthPath,
    int HealthTimeoutSeconds
);

public static class UpdateEnvironment {
    public static async Task<IResult> Handle(
        string slug,
        Guid envId,
        UpdateEnvironmentRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http
    ) {
        if (request.Name is null && request.Order is null && request.Target is null) {
            return Results.Problem(
                title: "Name, order, or target is required.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out var userId)) {
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsActive) {
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var cancellationToken = http.RequestAborted;
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var project = await db.Projects.SingleOrDefaultAsync(
            item => item.Slug == normalizedSlug,
            cancellationToken
        );

        if (project is null) {
            return Results.Problem(
                title: "Project was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var isAdmin = await users.IsInRoleAsync(user, "Admin");
        var isOwner = await db.TeamMembers.AnyAsync(
            member => 
                member.TeamId == project.TeamId
                && member.UserId == userId
                && member.Role == TeamRole.Owner,
            cancellationToken
        );

        if (!isAdmin && !isOwner) {
            return Results.Problem(
                title: "Project was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var environment = await db.Environments.SingleOrDefaultAsync(
            item => item.Id == envId && item.ProjectId == project.Id,
            cancellationToken
        );

        if (environment is null) {
            return Results.Problem(
                title: "Environment was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        try {
            if (request.Name is not null) {
                var name = request.Name.Trim();
                var nameTaken = await db.Environments.AnyAsync(
                    item =>
                        item.ProjectId == project.Id
                        && item.Id != environment.Id
                        && item.Name == name,
                    cancellationToken
                );
                if (nameTaken) {
                    return Results.Problem(
                        title: "An environment with this name already exists.",
                        statusCode: StatusCodes.Status409Conflict
                    );
                }

                environment.Rename(request.Name);
            }

            if (request.Order is not null) {
                environment.Reorder(request.Order.Value);
            }

            if (request.Target is not null) {
                var type = string.IsNullOrWhiteSpace(request.Target.Type)
                    ? environment.Target.Type
                    : request.Target.Type.Trim().ToLowerInvariant();

                var allowApiKeyProduction = request.Target.AllowApiKeyProduction
                    ?? environment.Target.AllowApiKeyProduction;
                if (type == "docker") {
                    environment.Retarget(DeploymentTarget.Docker(
                        request.Target.ContainerName ?? environment.Target.ContainerName ?? "",
                        request.Target.Network ?? environment.Target.Network,
                        request.Target.HostPort ?? environment.Target.HostPort,
                        request.Target.ContainerPort ?? (environment.Target.ContainerPort < 1 ? 8080 : environment.Target.ContainerPort),
                        request.Target.HealthPath ?? environment.Target.HealthPath ?? "/health",
                        request.Target.HealthTimeoutSeconds ?? (environment.Target.HealthTimeoutSeconds < 1 ? 60 : environment.Target.HealthTimeoutSeconds),
                        allowApiKeyProduction
                    ));
                } else if (type == "noop") {
                    environment.Retarget(DeploymentTarget.Noop(allowApiKeyProduction));
                } else {
                    return Results.Problem(
                        title: "Target type must be noop or docker.",
                        statusCode: StatusCodes.Status400BadRequest
                    );
                }
            }

            AuditLog.Write(db, TimeProvider.System.GetUtcNow(), user.Id, user.Email ?? user.DisplayName, "environment.updated", "Environment", environment.Id);
            await db.SaveChangesAsync(cancellationToken);
        } catch (ArgumentException exception) {
            return Results.Problem(
                title: exception.Message,
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        return Results.Ok(new EnvironmentResponse(
            environment.Id,
            environment.ProjectId,
            environment.Name,
            environment.Tier.ToString(),
            environment.Order,
            environment.Target.Type,
            environment.Target.AllowApiKeyProduction,
            environment.Target.ContainerName,
            environment.Target.Network,
            environment.Target.HostPort,
            environment.Target.ContainerPort,
            environment.Target.HealthPath,
            environment.Target.HealthTimeoutSeconds
        ));
    }
}