using AegisOps.Api.Organization;
using AegisOps.Domain.Security;

namespace AegisOps.Api.Tests;

public sealed class ArtifactReplayTests {
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void SameBuildFromAnotherAttemptCanReuseArtifact() {
        var existing = Artifact.Create(Guid.NewGuid(), "v0.0.0-main.0123456", Sha, "main",
            "ghcr.io/example/site:v0.0.0-main.0123456", CheckStatus.Passed, CheckStatus.Passed,
            DateTimeOffset.UtcNow, Digest, "github-actions", "first-run", null,
            TestSummary.Create(2, 2, 0, 0, 1), createdByApiKeyId: Guid.NewGuid());
        var request = Request() with { CiRunId = "second-run", TestSummary = new(2, 2, 0, 0, 4) };

        Assert.True(CreateArtifact.MatchesBuild(existing, request, CheckStatus.Passed, CheckStatus.Passed));
        Assert.False(CreateArtifact.MatchesBuild(existing, request with { CommitSha = new string('a', 40) }, CheckStatus.Passed, CheckStatus.Passed));
        Assert.False(CreateArtifact.MatchesBuild(existing, request with { ImageDigest = "sha256:" + new string('a', 64) }, CheckStatus.Passed, CheckStatus.Passed));
        Assert.False(CreateArtifact.MatchesBuild(existing, request with { ImageReference = "ghcr.io/example/other:v1" }, CheckStatus.Passed, CheckStatus.Passed));
        Assert.False(CreateArtifact.MatchesBuild(existing, request with { Branch = "release" }, CheckStatus.Passed, CheckStatus.Passed));
        Assert.False(CreateArtifact.MatchesBuild(existing, request with { CiProvider = "other" }, CheckStatus.Passed, CheckStatus.Passed));
        Assert.False(CreateArtifact.MatchesBuild(existing, request with { TestSummary = new(2, 1, 1, 0, 4) }, CheckStatus.Passed, CheckStatus.Passed));
        Assert.False(CreateArtifact.MatchesBuild(existing, request, CheckStatus.Failed, CheckStatus.Passed));
        Assert.False(CreateArtifact.MatchesBuild(existing, request, CheckStatus.Passed, CheckStatus.Failed));
    }

    [Fact]
    public void MissingDigestCannotEstablishSameBuild() {
        var existing = Artifact.Create(Guid.NewGuid(), "v1.0.0", Sha, "main", "ghcr.io/example/site:v1",
            CheckStatus.Passed, CheckStatus.Passed, DateTimeOffset.UtcNow,
            ciProvider: "github-actions", createdByApiKeyId: Guid.NewGuid());
        Assert.False(CreateArtifact.MatchesBuild(existing, Request() with { ImageDigest = null }, CheckStatus.Passed, CheckStatus.Passed));
    }

    private static CreateArtifactRequest Request() => new("v0.0.0-main.0123456", Sha, "main",
        "ghcr.io/example/site:v0.0.0-main.0123456", Digest, "github-actions", "first-run", null,
        "Passed", "Passed", new(2, 2, 0, 0, 1));
}
