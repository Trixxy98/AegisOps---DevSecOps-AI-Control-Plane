using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Organization;

public sealed record BackfillArtifactDigestRequest(string ImageDigest);

public static class BackfillArtifactDigest {
    public static async Task<IResult> Handle(
        Guid id,
        BackfillArtifactDigestRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http
    ) {
        if (string.IsNullOrWhiteSpace(request.ImageDigest)) {
            return Results.Problem(
                title: "Image digest is required.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var cancellationToken = http.RequestAborted;
        var artifact = await db.Artifacts.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken
        );

        if (artifact is null) {
            return Results.Problem(
                title: "Artifact was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var actorDisplay = "api-key";
        Guid? actorId = null;
        var actorType = principal.FindFirst("actor_type")?.Value;
        if (string.Equals(actorType, "apiKey", StringComparison.OrdinalIgnoreCase)) {
            var projectClaim = principal.FindFirst("project")?.Value;
            if (!Guid.TryParse(projectClaim, out var apiKeyProjectId) || apiKeyProjectId != artifact.ProjectId) {
                return Results.Problem(
                    title: "Artifact was not found.",
                    statusCode: StatusCodes.Status404NotFound
                );
            }
        } else {
            var subject = principal.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(subject)) {
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

            actorId = user.Id;
            actorDisplay = user.Email ?? user.DisplayName;
        }

        try {
            artifact.BackfillDigest(request.ImageDigest);
            AuditLog.Write(db, TimeProvider.System.GetUtcNow(), actorId, actorDisplay, "artifact.digest_backfilled", "Artifact", artifact.Id);
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

        return Results.Ok(new ArtifactResponse(
            artifact.Id,
            artifact.ProjectId,
            artifact.Version,
            artifact.CommitSha,
            artifact.Branch,
            artifact.ImageReference,
            artifact.ImageDigest,
            artifact.BuildStatus.ToString(),
            artifact.TestStatus.ToString(),
            artifact.CreatedAt
        ));
    }
}