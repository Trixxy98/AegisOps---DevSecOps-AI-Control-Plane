namespace AegisOps.Domain.Security;

public sealed class SecurityScan {
    public Guid Id {get; private set;}
    public Guid ArtifactId {get; private set;}
    public ScannerType Scanner {get; private set;}
    public ScanSource Source {get; private set;}
    public ScanStatus Status {get; private set;}
    public ReportFormat ReportFormat {get; private set;}
    public string? ToolVersion {get; private set;}
    public DateTimeOffset? StartedAt {get; private set;}
    public DateTimeOffset? CompletedAt {get; private set;}
    public ScanSummary Summary {get; private set;}
    public string? RawReportPath {get; private set;}
    public string? ErrorMessage {get; private set;}

    private SecurityScan() {
        Summary = ScanSummary.Empty();
    }

    public static SecurityScan Begin(
        Guid artifactId,
        ScannerType scanner,
        ScanSource source,
        ReportFormat format,
        DateTimeOffset startedAt,
        string? toolVersion,
        string reportPath,
        bool parseInline
    ) {
        if (artifactId == Guid.Empty) {
            throw new ArgumentException("Artifact ID is required.", nameof(artifactId));
        }

        if (!Enum.IsDefined(scanner) || !Enum.IsDefined(source) || !Enum.IsDefined(format)) {
            throw new ArgumentException("Scan type is invalid.");
        }

        if (string.IsNullOrWhiteSpace(reportPath)) {
            throw new ArgumentException("Report path is required.", nameof(reportPath));
        }

        return new SecurityScan {
            Id = Guid.CreateVersion7(),
            ArtifactId = artifactId,
            Scanner = scanner,
            Source = source,
            Status = parseInline ? ScanStatus.Running : ScanStatus.Queued,
            ReportFormat = format,
            ToolVersion = string.IsNullOrWhiteSpace(toolVersion) ? null : toolVersion.Trim(),
            StartedAt = startedAt,
            Summary = ScanSummary.Empty(),
            RawReportPath = reportPath.Trim(),
        };
    }

    public void Complete(ScanSummary summary, DateTimeOffset completedAt) {
        Status = ScanStatus.Completed;
        Summary = summary;
        CompletedAt = completedAt;
        ErrorMessage = null;
    }

    public void Fail(string message, DateTimeOffset completedAt) {
        Status = ScanStatus.Failed;
        CompletedAt = completedAt;
        ErrorMessage = string.IsNullOrWhiteSpace(message) ? "Scan parsing failed." : message.Trim();
    }
}
