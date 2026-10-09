using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public sealed record UpsertRepositoryRequest(
    string Provider,
    string FullName,
    string DefaultBranch,
    string HtmlUrl
);

public sealed record RepositoryResponse(
    Guid Id,
    Guid ProjectId,
    string Provider,
    string FullName,
    string DefaultBranch,
    string HtmlUrl
);

public static class UpsertRepository {
    public static async Task<IResult> Handle(
        string slug,
        UpsertRepositoryRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http
    ) {
        if (!Enum.TryParse<RepositoryProvider>(request.Provider, ignoreCase: true, out var provider) || !Enum.IsDefined(provider)) {
            return Results.Problem(
                title: "Provider must be GitHub.",
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
                title: "Project not found.",
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
                title: "Project not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        try {
            var existing = await db.Repositories.SingleOrDefaultAsync(
                item => item.ProjectId == project.Id,
                cancellationToken
            );

            if (existing is null) {
                existing = Repository.Create(
                    project.Id,
                    provider,
                    request.FullName,
                    request.DefaultBranch,
                    request.HtmlUrl
                );
                db.Repositories.Add(existing);
            } else {
                existing.Replace(
                    provider,
                    request.FullName,
                    request.DefaultBranch,
                    request.HtmlUrl
                );
            }

            AuditLog.Write(db, TimeProvider.System.GetUtcNow(), user.Id, user.Email ?? user.DisplayName, "repository.upserted", "Repository", existing.Id);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new RepositoryResponse(
                existing.Id,
                existing.ProjectId,
                existing.Provider.ToString(),
                existing.FullName,
                existing.DefaultBranch,
                existing.HtmlUrl
            ));
        } catch (ArgumentException exception) {
            return Results.Problem(
                title: exception.Message,
                statusCode: StatusCodes.Status400BadRequest
            );
        }
    }
}