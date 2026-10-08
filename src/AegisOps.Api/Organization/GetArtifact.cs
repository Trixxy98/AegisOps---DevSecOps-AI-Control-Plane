using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Organization;

public sealed record TestSummaryResponse(
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    double DurationSeconds
);

public sealed record ArtifactDetailResponse(
    Guid Id,
    Guid ProjectId,
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
    TestSummaryResponse? TestSummary,
    DateTimeOffset CreatedAt
);

public static class GetArtifact {
    public static async Task<IResult> Handle(
        Guid id,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http
    ) {
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

        var cancellationToken = http.RequestAborted;
        var artifact = await db.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (artifact is null) {
            return Results.Problem(
                title: "Artifact was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var project = await db.Projects
            .AsNoTracking()
            .SingleAsync(item => item.Id == artifact.ProjectId, cancellationToken);

        var isAdmin = await users.IsInRoleAsync(user, "Admin");
        if (!isAdmin) {
            var isMember = await db.TeamMembers.AnyAsync(
                member => member.TeamId == project.TeamId && member.UserId == userId,
                cancellationToken
            );
            if (!isMember) {
                return Results.Problem(
                    title: "Artifact was not found.",
                    statusCode: StatusCodes.Status404NotFound
                );
            }
        }

        var summary = artifact.TestSummary is null
            ? null
            : new TestSummaryResponse(
                artifact.TestSummary.Total,
                artifact.TestSummary.Passed,
                artifact.TestSummary.Failed,
                artifact.TestSummary.Skipped,
                artifact.TestSummary.DurationSeconds
            );

        return Results.Ok(new ArtifactDetailResponse(
            artifact.Id,
            artifact.ProjectId,
            artifact.Version,
            artifact.CommitSha,
            artifact.Branch,
            artifact.ImageReference,
            artifact.ImageDigest,
            artifact.CiProvider,
            artifact.CiRunId,
            artifact.CiRunUrl,
            artifact.BuildStatus.ToString(),
            artifact.TestStatus.ToString(),
            summary,
            artifact.CreatedAt
        ));
    }
}