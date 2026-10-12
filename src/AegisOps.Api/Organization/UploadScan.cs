using System.Security.Claims;
using System.Text.Json;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Persistence;
using AegisOps.Infrastructure.Scanning;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using AegisOps.Api.Deploy;
using AegisOps.Domain.Identity;

namespace AegisOps.Api.Organization;

public static class UploadScan {
    public const int MaxBytes = 20 * 1024 * 1024;

    public static async Task<IResult> Handle(
        Guid id,
        HttpRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        IWebHostEnvironment environment,
        IConfiguration configuration
    ) {
        if (!request.HasFormContentType) {
            return Results.Problem(title: "Scan upload must be multipart form data.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = await ActorResult.ResolveAsync(principal, users);
        if (actor.Error is not null) {
            return actor.Error;
        }

        var cancellationToken = request.HttpContext.RequestAborted;
        var artifact = await db.Artifacts.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (artifact is null) {
            return Results.Problem(title: "Artifact was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        if (actor.ApiKeyProjectId is Guid projectId && projectId != artifact.ProjectId) {
            return Results.Problem(title: "Artifact was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var form = await request.ReadFormAsync(cancellationToken);
        if (!Enum.TryParse<ScannerType>(form["scanner"].ToString(), true, out var scanner) || !Enum.IsDefined(scanner)) {
            return Results.Problem(title: "Scanner must be Gitleaks, Trivy, or Semgrep.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!Enum.TryParse<ReportFormat>(form["format"].ToString(), true, out var format) || format != ReportFormat.Sarif) {
            return Results.Problem(title: "Only SARIF reports are accepted.", statusCode: StatusCodes.Status400BadRequest);
        }

        var file = form.Files.GetFile("report");
        if (file is null || file.Length == 0) {
            return Results.Problem(title: "Report file is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > MaxBytes) {
            return Results.Problem(title: "Report must be 20 MB or smaller.", statusCode: StatusCodes.Status400BadRequest);
        }

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var root = configuration["Storage:ReportsPath"];
        if (string.IsNullOrWhiteSpace(root)) {
            root = Path.Combine(environment.ContentRootPath, "reports");
        }

        var directory = Path.Combine(root, artifact.Id.ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{scanner}-{time.GetUtcNow():yyyyMMddHHmmssfff}.sarif");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);

        try {
            var scan = await ScanReportStore.ReplaceAsync(
                db,
                artifact,
                scanner,
                format,
                form["toolVersion"].ToString(),
                path,
                bytes,
                time.GetUtcNow(),
                cancellationToken
            );
            AuditLog.Write(db, time.GetUtcNow(), actor.UserId ?? actor.ApiKeyId, actor.Display, "scan.uploaded", "SecurityScan", scan.Id);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Json(new {
                scan.Id,
                status = scan.Status.ToString(),
                summary = new {
                    scan.Summary.Critical,
                    scan.Summary.High,
                    scan.Summary.Medium,
                    scan.Summary.Low,
                    scan.Summary.Info,
                    scan.Summary.Unknown,
                    scan.Summary.Open,
                    scan.Summary.Suppressed,
                },
                findingsUrl = $"/api/v1/artifacts/{artifact.Id}/findings",
            }, statusCode: StatusCodes.Status201Created);
        } catch (JsonException) {
            return Results.Problem(title: "Report is not valid SARIF.", statusCode: StatusCodes.Status400BadRequest);
        }
    }
}

public static class ListFindings {
    public static async Task<IResult> Handle(Guid id, AegisOpsDbContext db, HttpContext http) {
        var findings = await db.SecurityFindings.AsNoTracking()
            .Where(item => item.ArtifactId == id)
            .OrderByDescending(item => item.Severity)
            .ThenBy(item => item.Title)
            .Select(item => new {
                item.Id,
                severity = item.Severity.ToString(),
                item.RuleId,
                item.Title,
                item.FilePath,
                item.StartLine,
                item.PackageName,
                item.InstalledVersion,
                item.FixedVersion,
                item.Cve,
                item.Fingerprint,
                status = item.Status.ToString(),
                item.SuppressionReason,
            })
            .ToListAsync(http.RequestAborted);
        return Results.Ok(findings);
    }
}

public sealed record SuppressFindingRequest(string Fingerprint, string Reason, DateTimeOffset? ExpiresAt);

public static class SuppressFinding {
    public static async Task<IResult> Handle(
        string slug,
        SuppressFindingRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        HttpContext http
    ) {
        var actor = await ActorResult.ResolveAsync(principal, users);
        if (actor.Error is not null || actor.UserId is null) {
            return actor.Error ?? Results.Problem(title: "A user must suppress a finding.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrWhiteSpace(request.Fingerprint) || string.IsNullOrWhiteSpace(request.Reason)) {
            return Results.Problem(title: "Fingerprint and reason are required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var cancellationToken = http.RequestAborted;
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var project = await db.Projects.SingleOrDefaultAsync(item => item.Slug == normalizedSlug, cancellationToken);
        if (project is null) {
            return Results.Problem(title: "Project was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var now = time.GetUtcNow();
        var suppression = await db.FindingSuppressions.SingleOrDefaultAsync(
            item => item.ProjectId == project.Id && item.Fingerprint == request.Fingerprint.Trim(),
            cancellationToken
        );
        if (suppression is null) {
            suppression = FindingSuppression.Create(project.Id, request.Fingerprint, request.Reason, actor.UserId.Value, now, request.ExpiresAt);
            db.FindingSuppressions.Add(suppression);
        }

        var fingerprint = request.Fingerprint.Trim();
        var artifactIds = await db.Artifacts
            .Where(item => item.ProjectId == project.Id)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var findings = await db.SecurityFindings
            .Where(item => item.Fingerprint == fingerprint && artifactIds.Contains(item.ArtifactId))
            .ToListAsync(cancellationToken);
        foreach (var finding in findings) {
            finding.Suppress(actor.UserId.Value, request.Reason);
        }

        AuditLog.Write(db, now, actor.UserId, actor.Display, "finding.suppressed", "FindingSuppression", suppression.Id);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { suppression.Id, suppression.Fingerprint });
    }
}
