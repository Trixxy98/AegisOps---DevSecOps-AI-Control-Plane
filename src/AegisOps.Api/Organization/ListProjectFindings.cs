using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Organization;

/// <summary>
/// GET /api/v1/projects/{slug}/findings: paginated findings across a project's artifacts (findings:read).
/// Team-scoped as docs/09 section 6: Admin and Security see every project; other users must be a member of the
/// project's team, otherwise 404 so the project is not revealed.
/// </summary>
public static class ListProjectFindings {
    public const int MaxPageSize = 100;

    public static async Task<IResult> Handle(
        string slug,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http,
        string? severity = null,
        string? scanner = null,
        string? status = null,
        Guid? artifactId = null,
        int page = 1,
        int pageSize = 50
    ) {
        var subject = principal.FindFirst("sub")?.Value;
        if (!Guid.TryParse(subject, out var userId)) {
            return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsActive) {
            return Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (page < 1 || pageSize is < 1 or > MaxPageSize) {
            return Results.Problem(title: "Invalid pagination.", statusCode: StatusCodes.Status400BadRequest);
        }

        Severity? severityFilter = null;
        if (!string.IsNullOrWhiteSpace(severity)) {
            if (!TryParseName<Severity>(severity, out var parsed)) {
                return Results.Problem(title: "Severity is invalid.", statusCode: StatusCodes.Status400BadRequest);
            }
            severityFilter = parsed;
        }

        ScannerType? scannerFilter = null;
        if (!string.IsNullOrWhiteSpace(scanner)) {
            if (!TryParseName<ScannerType>(scanner, out var parsed)) {
                return Results.Problem(title: "Scanner must be Gitleaks, Trivy, or Semgrep.", statusCode: StatusCodes.Status400BadRequest);
            }
            scannerFilter = parsed;
        }

        FindingStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status)) {
            if (!TryParseName<FindingStatus>(status, out var parsed)) {
                return Results.Problem(title: "Status must be Open, Suppressed, or Resolved.", statusCode: StatusCodes.Status400BadRequest);
            }
            statusFilter = parsed;
        }

        var cancellationToken = http.RequestAborted;
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var project = await db.Projects.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Slug == normalizedSlug, cancellationToken);
        if (project is null) {
            return NotFound();
        }

        var roles = await users.GetRolesAsync(user);
        var canReadAll = roles.Contains("Admin", StringComparer.Ordinal) || roles.Contains("Security", StringComparer.Ordinal);
        if (!canReadAll && !await db.TeamMembers.AsNoTracking().AnyAsync(
                member => member.TeamId == project.TeamId && member.UserId == userId, cancellationToken)) {
            return NotFound();
        }

        var artifacts = db.Artifacts.AsNoTracking().Where(item => item.ProjectId == project.Id);
        if (artifactId is Guid selectedArtifact) {
            artifacts = artifacts.Where(item => item.Id == selectedArtifact);
        }

        var query =
            from finding in db.SecurityFindings.AsNoTracking()
            join artifact in artifacts on finding.ArtifactId equals artifact.Id
            join scan in db.SecurityScans.AsNoTracking() on finding.ScanId equals scan.Id
            select new { finding, artifact.Version, scan.Scanner };
        if (severityFilter is Severity severityValue) query = query.Where(row => row.finding.Severity == severityValue);
        if (scannerFilter is ScannerType scannerValue) query = query.Where(row => row.Scanner == scannerValue);
        if (statusFilter is FindingStatus statusValue) query = query.Where(row => row.finding.Status == statusValue);

        var total = await query.CountAsync(cancellationToken);
        // Severity is persisted as a string, so rank it explicitly; ordering the column would be alphabetical.
        var rows = await query
            .OrderByDescending(row =>
                row.finding.Severity == Severity.Critical ? 6 :
                row.finding.Severity == Severity.High ? 5 :
                row.finding.Severity == Severity.Medium ? 4 :
                row.finding.Severity == Severity.Low ? 3 :
                row.finding.Severity == Severity.Info ? 2 : 1)
            .ThenBy(row => row.finding.Title)
            .ThenBy(row => row.finding.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var fingerprints = rows.Select(row => row.finding.Fingerprint).Distinct(StringComparer.Ordinal).ToList();
        var suppressions = (await db.FindingSuppressions.AsNoTracking()
                .Where(item => item.ProjectId == project.Id && fingerprints.Contains(item.Fingerprint))
                .Select(item => new { item.Fingerprint, item.Reason, item.CreatedAt, item.ExpiresAt })
                .ToListAsync(cancellationToken))
            .ToDictionary(item => item.Fingerprint, StringComparer.Ordinal);

        var items = rows.Select(row => {
            var finding = row.finding;
            suppressions.TryGetValue(finding.Fingerprint, out var suppression);
            return new {
                finding.Id,
                finding.ArtifactId,
                artifactVersion = row.Version,
                finding.ScanId,
                scanner = row.Scanner.ToString(),
                severity = finding.Severity.ToString(),
                finding.RuleId,
                finding.Title,
                finding.Description,
                finding.FilePath,
                finding.StartLine,
                finding.PackageName,
                finding.InstalledVersion,
                finding.FixedVersion,
                finding.Cve,
                finding.HelpUri,
                finding.Fingerprint,
                status = finding.Status.ToString(),
                finding.SuppressionReason,
                suppression = suppression is null ? null : new {
                    suppression.Reason, suppression.CreatedAt, suppression.ExpiresAt,
                },
            };
        }).ToList();

        return Results.Ok(new { page, pageSize, total, items });
    }

    private static IResult NotFound() =>
        Results.Problem(title: "Project was not found.", statusCode: StatusCodes.Status404NotFound);

    // A single name only: numbers ("5") and comma lists ("Open,Suppressed") are rejected, although
    // Enum.TryParse would accept them.
    private static bool TryParseName<TEnum>(string value, out TEnum result) where TEnum : struct, Enum {
        var trimmed = value.Trim();
        result = default;
        return trimmed.All(char.IsAsciiLetter) && Enum.TryParse(trimmed, ignoreCase: true, out result) && Enum.IsDefined(result);
    }
}
