namespace AegisOps.Domain.Security;

public sealed class SecurityFinding {
    public Guid Id {get; private set;}
    public Guid ScanId {get; private set;}
    public Guid ArtifactId {get; private set;}
    public Severity Severity {get; private set;}
    public string RuleId {get; private set;}
    public string Title {get; private set;}
    public string? Description {get; private set;}
    public string? FilePath {get; private set;}
    public int? StartLine {get; private set;}
    public string? PackageName {get; private set;}
    public string? InstalledVersion {get; private set;}
    public string? FixedVersion {get; private set;}
    public string? Cve {get; private set;}
    public string Fingerprint {get; private set;}
    public FindingStatus Status {get; private set;}
    public Guid? SuppressedById {get; private set;}
    public string? SuppressionReason {get; private set;}
    public string? HelpUri {get; private set;}

    private SecurityFinding() {
        RuleId = string.Empty;
        Title = string.Empty;
        Fingerprint = string.Empty;
    }

    public static SecurityFinding Create(
        Guid scanId,
        Guid artifactId,
        Severity severity,
        string ruleId,
        string title,
        string fingerprint,
        string? description = null,
        string? filePath = null,
        int? startLine = null,
        string? packageName = null,
        string? installedVersion = null,
        string? fixedVersion = null,
        string? cve = null,
        string? helpUri = null,
        FindingStatus status = FindingStatus.Open,
        Guid? suppressedById = null,
        string? suppressionReason = null
    ) {
        if (scanId == Guid.Empty || artifactId == Guid.Empty) {
            throw new ArgumentException("Scan and artifact IDs are required.");
        }

        if (!Enum.IsDefined(severity) || !Enum.IsDefined(status)) {
            throw new ArgumentException("Finding status is invalid.");
        }

        if (string.IsNullOrWhiteSpace(ruleId) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(fingerprint)) {
            throw new ArgumentException("Rule, title, and fingerprint are required.");
        }

        return new SecurityFinding {
            Id = Guid.CreateVersion7(),
            ScanId = scanId,
            ArtifactId = artifactId,
            Severity = severity,
            RuleId = Trim(ruleId, 200),
            Title = Trim(title, 500),
            Description = Limit(description, 4000),
            FilePath = Limit(filePath, 500),
            StartLine = startLine is > 0 ? startLine : null,
            PackageName = Limit(packageName, 200),
            InstalledVersion = Limit(installedVersion, 100),
            FixedVersion = Limit(fixedVersion, 100),
            Cve = Limit(cve, 40),
            Fingerprint = Trim(fingerprint, 128),
            Status = status,
            SuppressedById = suppressedById,
            SuppressionReason = Limit(suppressionReason, 2000),
            HelpUri = Limit(helpUri, 500),
        };
    }

    public void Suppress(Guid actorId, string reason) {
        if (actorId == Guid.Empty) {
            throw new ArgumentException("Actor ID is required.", nameof(actorId));
        }

        if (string.IsNullOrWhiteSpace(reason)) {
            throw new ArgumentException("Suppression reason is required.", nameof(reason));
        }

        Status = FindingStatus.Suppressed;
        SuppressedById = actorId;
        SuppressionReason = reason.Trim();
    }

    public void Reopen() {
        Status = FindingStatus.Open;
        SuppressedById = null;
        SuppressionReason = null;
    }

    private static string Trim(string value, int max) {
        var text = value.Trim();
        return text.Length <= max ? text : text[..max];
    }

    private static string? Limit(string? value, int max) {
        if (string.IsNullOrWhiteSpace(value)) {
            return null;
        }

        var text = value.Trim();
        return text.Length <= max ? text : text[..max];
    }
}
