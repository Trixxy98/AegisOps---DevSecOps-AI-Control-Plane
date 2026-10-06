using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using ProjectEnvironment = AegisOps.Domain.Organization.Environment;

namespace AegisOps.Api.Organization;

public sealed record CreateProjectRequest(
    Guid TeamId,
    string Name,
    string Slug,
    string? Description
);

public sealed record EnvironmentListItem(
    Guid Id,
    string Name,
    string Tier,
    int Order
);

public sealed record ProjectResponse(
    Guid Id,
    Guid TeamId,
    string Name,
    string Slug,
    string? Description,
    bool IsArchived,
    IReadOnlyList<EnvironmentListItem> Environments,
    RepositoryResponse? Repository
);

public static class CreateProject {
    public static async Task<IResult> Handle(
        CreateProjectRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        HttpContext http
    ) {
        if (request.TeamId == Guid.Empty 
            || string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.Slug)) {
                return Results.Problem(
                    title: "Team ID, name, and slug are required.",
                    statusCode: StatusCodes.Status400BadRequest
                );
            }

        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out _)) {
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
        var teamExists = await db.Teams.AnyAsync(
            team => team.Id == request.TeamId,
            cancellationToken
        );
        if (!teamExists) {
            return Results.Problem(
                title: "Team not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var slug = request.Slug.Trim().ToLowerInvariant();
        var slugTaken = await db.Projects.AnyAsync(
            project => project.Slug == slug,
            cancellationToken
        );
        if (slugTaken) {
            return Results.Problem(
                title: "A project with this slug already exists.",
                statusCode: StatusCodes.Status409Conflict
            );
        }

        try {
            var project = Project.Create(
                request.TeamId,
                request.Name,
                slug,
                time.GetUtcNow(),
                request.Description
            );

            var environments = new[] {
                ProjectEnvironment.Create(project.Id, "Development", EnvironmentTier.Development, 0),
                ProjectEnvironment.Create(project.Id, "Staging", EnvironmentTier.Staging, 1),
                ProjectEnvironment.Create(project.Id, "Production", EnvironmentTier.Production, 2),
            };

            db.Projects.Add(project);
            db.Environments.AddRange(environments);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created(
                $"/api/v1/projects/{project.Slug}",
                new ProjectResponse(
                    project.Id,
                    project.TeamId,
                    project.Name,
                    project.Slug,
                    project.Description,
                    project.IsArchived,
                    environments
                        .OrderBy(item => item.Order)
                        .Select(item => new EnvironmentListItem(
                            item.Id,
                            item.Name,
                            item.Tier.ToString(),
                            item.Order
                        ))
                        .ToArray(),
                    null
                )
            );
        } catch (ArgumentException exception) {
            return Results.Problem(
                title: exception.Message,
                statusCode: StatusCodes.Status400BadRequest
            );
        }
    }
}