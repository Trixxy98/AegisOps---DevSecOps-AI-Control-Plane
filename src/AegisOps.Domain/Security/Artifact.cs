namespace AegisOps.Domain.Security;

public sealed class Artifact {
    public Guid Id {get; private set;}
    public Guid ProjectId {get; private set;}
    public string Version {get; private set;}
    public string CommitSha {get; private set;}
    public string Branch {get; private set;}
    public string ImageReference {get; private set;}
    public string? ImageDigest {get; private set;}
    public string? CiProvider {get; private set;}
    public string? CiRunId {get; private set;}
    public string? CiRunUrl {get; private set;}
    public CheckStatus BuildStatus {get; private set;}
    public CheckStatus TestStatus {get; private set;}
    public TestSummary? TestSummary {get; private set;}
    public Guid? CreatedById {get; private set;}
    public Guid? CreatedByApiKeyId {get; private set;}
    public DateTimeOffset CreatedAt {get; private set;}

    private Artifact() {
        Version = string.Empty;
        CommitSha = string.Empty;
        Branch = string.Empty;
        ImageReference = string.Empty;
    }

    public static Artifact Create(
        Guid projectId,
        string version,
        string commitSha,
        string branch,
        string imageReference,
        CheckStatus buildStatus,
        CheckStatus testStatus,
        DateTimeOffset createdAt,
        string? imageDigest = null,
        string? ciProvider = null,
        string? ciRunId = null,
        string? ciRunUrl = null,
        TestSummary? testSummary = null,
        Guid? createdById = null,
        Guid? createdByApiKeyId = null
    ) {
        if (projectId == Guid.Empty) {
            throw new ArgumentException("Project ID is required.", nameof(projectId));
        }

        if (createdById is null && createdByApiKeyId is null) {
            throw new ArgumentException("A user or an API key must create the artifact.");
        }

        if (createdById == Guid.Empty || createdByApiKeyId == Guid.Empty) {
            throw new ArgumentException("Creator ID is invalid.");
        }

        if (!Enum.IsDefined(buildStatus) || !Enum.IsDefined(testStatus)) {
            throw new ArgumentException("Build and test status must be known values.");
        }

        return new Artifact {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Version = RequireVersion(version),
            CommitSha = RequireCommitSha(commitSha),
            Branch = RequireText(branch, "Branch", nameof(branch)),
            ImageReference = RequireText(imageReference, "Image reference", nameof(imageReference)),
            ImageDigest = NormalizeDigest(imageDigest),
            CiProvider = EmptyToNull(ciProvider),
            CiRunId = EmptyToNull(ciRunId),
            CiRunUrl = NormalizeUrl(ciRunUrl),
            BuildStatus = buildStatus,
            TestStatus = testStatus,
            TestSummary = testSummary,
            CreatedById = createdById,
            CreatedByApiKeyId = createdByApiKeyId,
            CreatedAt = createdAt,
        };
    }

    public void BackfillDigest(string imageDigest) {
        if (ImageDigest is not null) {
            throw new InvalidOperationException("Image digest is already set.");
        }

        ImageDigest = NormalizeDigest(imageDigest)
            ?? throw new ArgumentException("Image digest is required.", nameof(imageDigest));
    }

    private static string RequireVersion(string version) {
        var value = RequireText(version, "Version", nameof(version));
        var body = value.StartsWith('v') ? value[1..] : value;
        var parts = body.Split('.');
        if (parts.Length < 3 || parts.Take(3).Any(part => part.Length == 0 || part.Any(character => !char.IsAsciiDigit(character)))) {
            throw new ArgumentException("Version must look like 1.2.3 or v1.2.3.", nameof(version));
        }

        return value;
    }

    private static string RequireCommitSha(string commitSha) {
        var value = RequireText(commitSha, "Commit SHA", nameof(commitSha));
        if (value.Length is < 7 or > 64 || value.Any(character => !Uri.IsHexDigit(character))) {
            throw new ArgumentException("Commit SHA must be 7 to 64 hexadecimal characters.", nameof(commitSha));
        }

        return value.ToLowerInvariant();
    }

    private static string? NormalizeDigest(string? imageDigest) {
        if (string.IsNullOrWhiteSpace(imageDigest)) {
            return null;
        }

        var value = imageDigest.Trim().ToLowerInvariant();
        const string prefix = "sha256:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + 64) {
            throw new ArgumentException("Image digest must look like sha256:<64 hex characters>.", nameof(imageDigest));
        }

        if (value[prefix.Length..].Any(character => !Uri.IsHexDigit(character))) {
            throw new ArgumentException("Image digest must look like sha256:<64 hex characters>.", nameof(imageDigest));
        }

        return value;
    }

    private static string? NormalizeUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url)) {
            return null;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out _)) {
            throw new ArgumentException("CI run URL must be an absolute URL.", nameof(url));
        }

        return url.Trim();
    }

    private static string RequireText(string value, string label, string name) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException($"{label} is required.", name);
        }

        return value.Trim();
    }

    private static string? EmptyToNull(string? value) {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}