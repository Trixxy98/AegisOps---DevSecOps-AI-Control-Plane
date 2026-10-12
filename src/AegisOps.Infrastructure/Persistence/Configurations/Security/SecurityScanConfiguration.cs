using AegisOps.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AegisOps.Infrastructure.Persistence.Configurations.Security;

public sealed class SecurityScanConfiguration : IEntityTypeConfiguration<SecurityScan> {
    public void Configure(EntityTypeBuilder<SecurityScan> builder) {
        builder.ToTable("security_scans", "security", table => {
            table.HasCheckConstraint("ck_security_scans_scanner", "scanner IN ('Gitleaks', 'Trivy', 'Semgrep')");
            table.HasCheckConstraint("ck_security_scans_source", "source IN ('CiUploaded', 'WorkerExecuted')");
            table.HasCheckConstraint("ck_security_scans_status", "status IN ('Queued', 'Running', 'Completed', 'Failed')");
            table.HasCheckConstraint("ck_security_scans_format", "report_format IN ('Sarif', 'TrivyJson', 'GitleaksJson', 'SemgrepJson')");
        });
        builder.HasKey(scan => scan.Id);
        builder.Property(scan => scan.Scanner).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(scan => scan.Source).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(scan => scan.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(scan => scan.ReportFormat).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(scan => scan.ToolVersion).HasMaxLength(100);
        builder.Property(scan => scan.RawReportPath).HasMaxLength(500);
        builder.Property(scan => scan.ErrorMessage).HasMaxLength(2000);
        builder.OwnsOne(scan => scan.Summary, summary => summary.ToJson("summary"));
        builder.HasIndex(scan => new { scan.ArtifactId, scan.Scanner }).IsUnique();
        builder.HasOne<Artifact>().WithMany().HasForeignKey(scan => scan.ArtifactId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SecurityFindingConfiguration : IEntityTypeConfiguration<SecurityFinding> {
    public void Configure(EntityTypeBuilder<SecurityFinding> builder) {
        builder.ToTable("security_findings", "security", table => {
            table.HasCheckConstraint("ck_security_findings_severity", "severity IN ('Unknown', 'Info', 'Low', 'Medium', 'High', 'Critical')");
            table.HasCheckConstraint("ck_security_findings_status", "status IN ('Open', 'Suppressed', 'Resolved')");
        });
        builder.HasKey(finding => finding.Id);
        builder.Property(finding => finding.Severity).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(finding => finding.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(finding => finding.RuleId).HasMaxLength(200).IsRequired();
        builder.Property(finding => finding.Title).HasMaxLength(500).IsRequired();
        builder.Property(finding => finding.Description).HasMaxLength(4000);
        builder.Property(finding => finding.FilePath).HasMaxLength(500);
        builder.Property(finding => finding.PackageName).HasMaxLength(200);
        builder.Property(finding => finding.InstalledVersion).HasMaxLength(100);
        builder.Property(finding => finding.FixedVersion).HasMaxLength(100);
        builder.Property(finding => finding.Cve).HasMaxLength(40);
        builder.Property(finding => finding.Fingerprint).HasMaxLength(128).IsRequired();
        builder.Property(finding => finding.SuppressionReason).HasMaxLength(2000);
        builder.Property(finding => finding.HelpUri).HasMaxLength(500);
        builder.HasIndex(finding => new { finding.ScanId, finding.Severity });
        builder.HasIndex(finding => new { finding.ArtifactId, finding.Status });
        builder.HasIndex(finding => finding.Fingerprint);
        builder.HasOne<SecurityScan>().WithMany().HasForeignKey(finding => finding.ScanId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class FindingSuppressionConfiguration : IEntityTypeConfiguration<FindingSuppression> {
    public void Configure(EntityTypeBuilder<FindingSuppression> builder) {
        builder.ToTable("finding_suppressions", "security");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Fingerprint).HasMaxLength(128).IsRequired();
        builder.Property(item => item.Reason).HasMaxLength(2000).IsRequired();
        builder.HasIndex(item => new { item.ProjectId, item.Fingerprint }).IsUnique();
    }
}
