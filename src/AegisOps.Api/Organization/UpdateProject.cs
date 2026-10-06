using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public sealed record UpdateProjectRequest(
    string? Name,
    string? Description,
    bool? IsArchived
);

public static class UpdateProject {
    public static async Task<IResult> Handle(
        string slug,
        UpdateProjectRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http
    ) {
        if (request.Name is null && request.Description is null && request.IsArchived is null) {
            return Results.Problem(
                title: "Name, description, or isArchived is required.",
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
        var project = await db.Projects.SingleOrDefaultAsync(item => item.Slug == normalizedSlug, cancellationToken);

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

        try {
            if (request.Name is not null) {
                project.Rename(request.Name);
            }

            if (request.Description is not null) {
                project.Describe(request.Description);
            }

            if (request.IsArchived is true && !project.IsArchived) {
                project.Archive();
            } else if (request.IsArchived is false && project.IsArchived) {
                project.Restore();
            }

            await db.SaveChangesAsync(cancellationToken);
        } catch (ArgumentException exception) {
            return Results.Problem(
                title: exception.Message,
                statusCode: StatusCodes.Status400BadRequest
            );
        } catch (InvalidOperationException exception) {
            return Results.Problem(
                title: exception.Message,
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        return Results.Ok(new ProjectListItem(
            project.Id,
            project.TeamId,
            project.Name,
            project.Slug,
            project.Description,
            project.IsArchived
        ));
    }
}


