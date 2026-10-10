using System.Security.Claims;
using AegisOps.Api.Idempotency;
using AegisOps.Application.Idempotency;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Organization;

public sealed record TestSummaryRequest(
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    double DurationSeconds
);

public sealed record CreateArtifactRequest(
    string Version,
    string CommitSha,
    string Branch,
    string ImageReference,
    string? ImageDigest,
    string? CiProvider,
    string? CiRunId,
    string? CiRunUrl,
    string BuildStatus,
    string TestStatus,
    TestSummaryRequest? TestSummary
);

public sealed record ArtifactResponse(
    Guid Id,
    Guid ProjectId,
    string Version,
    string CommitSha,
    string Branch,
    string ImageReference,
    string? ImageDigest,
    string BuildStatus,
    string TestStatus,
    DateTimeOffset CreatedAt
);

public static class CreateArtifact {
    public static async Task<IResult> Handle(
        string slug,
        CreateArtifactRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        IIdempotencyStore idempotency,
        HttpContext http
    ) {
        var replay = await IdempotencyRequests.ReplayAsync(http, principal, idempotency);
        if (replay is not null) {
            return replay;
        }

        if (!Enum.TryParse<CheckStatus>(request.BuildStatus, ignoreCase: true, out var buildStatus)
            || !Enum.IsDefined(buildStatus)
            || !Enum.TryParse<CheckStatus>(request.TestStatus, ignoreCase: true, out var testStatus)
            || !Enum.IsDefined(testStatus)) {
            return Results.Problem(
                title: "Build and test status must be Unknown, Passed, Failed, or Skipped.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var cancellationToken = http.RequestAborted;
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var project = await db.Projects.SingleOrDefaultAsync(
            item => item.Slug == normalizedSlug,
            cancellationToken
        );

        if (project is null || project.IsArchived) {
            return Results.Problem(
                title: "Project was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        Guid? createdById = null;
        Guid? createdByApiKeyId = null;
        var actorDisplay = "api-key";
        var actorType = principal.FindFirst("actor_type")?.Value;
        if (string.Equals(actorType, "apiKey", StringComparison.OrdinalIgnoreCase)) {
            var subject = principal.FindFirst("sub")?.Value;
            var projectClaim = principal.FindFirst("project")?.Value;
            const string prefix = "apikey:";
            if (subject is null
                || !subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(subject[prefix.Length..], out var apiKeyId)
                || !Guid.TryParse(projectClaim, out var apiKeyProjectId)
                || apiKeyProjectId != project.Id) {
                return Results.Problem(
                    title: "Project was not found.",
                    statusCode: StatusCodes.Status404NotFound
                );
            }

            createdByApiKeyId = apiKeyId;
        } else {
            var subject = principal.FindFirst("sub")?.Value;
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

            createdById = userId;
            actorDisplay = user.Email ?? user.DisplayName;
        }

        var versionTaken = await db.Artifacts.AnyAsync(
            item => item.ProjectId == project.Id && item.Version == request.Version.Trim(),
            cancellationToken
        );
        if (versionTaken) {
            return await IdempotencyRequests.FinishAsync(http, idempotency, StatusCodes.Status409Conflict, new {
                title = "An artifact with this version already exists.",
                status = StatusCodes.Status409Conflict,
            });
        }

        try {
            TestSummary? summary = request.TestSummary is null
                ? null
                : TestSummary.Create(
                    request.TestSummary.Total,
                    request.TestSummary.Passed,
                    request.TestSummary.Failed,
                    request.TestSummary.Skipped,
                    request.TestSummary.DurationSeconds
                );

            var artifact = Artifact.Create(
                project.Id,
                request.Version,
                request.CommitSha,
                request.Branch,
                request.ImageReference,
                buildStatus,
                testStatus,
                time.GetUtcNow(),
                request.ImageDigest,
                request.CiProvider,
                request.CiRunId,
                request.CiRunUrl,
                summary,
                createdById,
                createdByApiKeyId
            );

            db.Artifacts.Add(artifact);
            AuditLog.Write(db, time.GetUtcNow(), createdById ?? createdByApiKeyId, actorDisplay, "artifact.created", "Artifact", artifact.Id);
            await db.SaveChangesAsync(cancellationToken);

            return await IdempotencyRequests.FinishAsync(http, idempotency, StatusCodes.Status201Created, new ArtifactResponse(
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
        } catch (ArgumentException exception) {
            return Results.Problem(
                title: exception.Message,
                statusCode: StatusCodes.Status400BadRequest
            );
        }
    }
}