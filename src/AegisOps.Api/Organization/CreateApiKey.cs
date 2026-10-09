using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Organization;

public sealed record CreateApiKeyRequest(
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt
);

public sealed record ApiKeyListItem(
    Guid Id,
    string Name,
    string KeyPrefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt
);

public sealed record CreateApiKeyResponse(
    Guid Id,
    string Name,
    string KeyPrefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    string Plaintext
);

public static class CreateApiKey {
    public static async Task<IResult> Handle(
        string slug,
        CreateApiKeyRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        HttpContext http
    ) {
        if (string.IsNullOrWhiteSpace(request.Name)) {
            return Results.Problem(
                title: "Name is required.",
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

        try {
            var generated = ApiKeyGenerator.Create();
            var now = time.GetUtcNow();
            var apiKey = ApiKey.Create(
                project.Id,
                request.Name,
                generated.Prefix,
                generated.Hash,
                request.Scopes,
                request.ExpiresAt,
                userId,
                now
            );

            db.ApiKeys.Add(apiKey);
            AuditLog.Write(db, now, user.Id, user.Email ?? user.DisplayName, "apikey.created", "ApiKey", apiKey.Id);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created(
                $"/api/v1/projects/{project.Slug}/api-keys",
                new CreateApiKeyResponse(
                    apiKey.Id,
                    apiKey.Name,
                    apiKey.KeyPrefix,
                    apiKey.Scopes,
                    apiKey.ExpiresAt,
                    apiKey.CreatedAt,
                    generated.Plaintext
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