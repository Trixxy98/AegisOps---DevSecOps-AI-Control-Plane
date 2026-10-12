using System.Text.Json;
using AegisOps.Application.Scanning;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Infrastructure.Scanning;

public static class ScanReportStore {
    public const int InlineLimit = 2 * 1024 * 1024;

    public static async Task<SecurityScan> ReplaceAsync(
        AegisOpsDbContext db,
        Artifact artifact,
        ScannerType scanner,
        ReportFormat format,
        string? toolVersion,
        string reportPath,
        ReadOnlyMemory<byte> report,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = await db.SecurityScans
            .Where(item => item.ArtifactId == artifact.Id && item.Scanner == scanner)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0) {
            var ids = existing.Select(item => item.Id).ToArray();
            var oldFindings = await db.SecurityFindings.Where(item => ids.Contains(item.ScanId)).ToListAsync(cancellationToken);
            db.SecurityFindings.RemoveRange(oldFindings);
            db.SecurityScans.RemoveRange(existing);
            await db.SaveChangesAsync(cancellationToken);
        }

        var inline = report.Length <= InlineLimit;
        var scan = SecurityScan.Begin(artifact.Id, scanner, ScanSource.CiUploaded, format, now, toolVersion, reportPath, inline);
        db.SecurityScans.Add(scan);
        if (inline) {
            await CompleteAsync(db, scan, artifact.ProjectId, report, now, cancellationToken);
        } else {
            db.Jobs.Add(Job.Enqueue(
                JobType.ParseScanReport,
                JsonSerializer.Serialize(new { scanId = scan.Id }),
                now,
                scan.Id.ToString("N")
            ));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return scan;
    }

    public static async Task ParseQueuedAsync(
        AegisOpsDbContext db,
        Guid scanId,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) {
        var scan = await db.SecurityScans.SingleAsync(item => item.Id == scanId, cancellationToken);
        var artifact = await db.Artifacts.AsNoTracking().SingleAsync(item => item.Id == scan.ArtifactId, cancellationToken);
        if (string.IsNullOrWhiteSpace(scan.RawReportPath) || !File.Exists(scan.RawReportPath)) {
            scan.Fail("Stored report was not found.", now);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(scan.RawReportPath, cancellationToken);
        await CompleteAsync(db, scan, artifact.ProjectId, bytes, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task CompleteAsync(
        AegisOpsDbContext db,
        SecurityScan scan,
        Guid projectId,
        ReadOnlyMemory<byte> report,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) {
        var parsed = SarifReportParser.Parse(scan.Scanner, report);
        var suppressions = await db.FindingSuppressions
            .Where(item => item.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        var findings = new List<SecurityFinding>(parsed.Count);
        foreach (var item in parsed) {
            var suppression = suppressions.FirstOrDefault(entry => entry.Fingerprint == item.Fingerprint && entry.IsActive(now));
            findings.Add(SecurityFinding.Create(
                scan.Id,
                scan.ArtifactId,
                item.Severity,
                item.RuleId,
                item.Title,
                item.Fingerprint,
                item.Description,
                item.FilePath,
                item.StartLine,
                item.PackageName,
                item.InstalledVersion,
                item.FixedVersion,
                item.Cve,
                item.HelpUri,
                suppression is null ? FindingStatus.Open : FindingStatus.Suppressed,
                suppression?.CreatedById,
                suppression?.Reason
            ));
        }

        db.SecurityFindings.AddRange(findings);
        scan.Complete(ScanSummary.From(findings), now);
    }
}
