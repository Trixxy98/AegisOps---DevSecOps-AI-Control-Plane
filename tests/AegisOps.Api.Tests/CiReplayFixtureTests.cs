using System.Text.Json;
using AegisOps.Domain.Security;

namespace AegisOps.Api.Tests;

public sealed class CiReplayFixtureTests {
    private static readonly string[] TargetServices = ["site-service", "circuit-service", "notification-service"];
    private static readonly string[] Tiers = ["Development", "Staging", "Production"];

    [Fact]
    public void Every_target_service_has_a_valid_replay_fixture() {
        var directory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "ci-payloads");
        foreach (var service in TargetServices) {
            var path = Path.Combine(directory, service + ".json");
            Assert.True(File.Exists(path), $"Missing replay fixture for {service}.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            string Text(string name) => root.GetProperty(name).GetString()!;
            Assert.Equal(service, Text("project"));
            Assert.StartsWith($"ghcr.io/aegisops/{service}:", Text("imageReference"), StringComparison.Ordinal);
            Assert.Contains(Text("deployTo"), Tiers);
            var summary = root.GetProperty("testSummary");
            Assert.Equal(summary.GetProperty("total").GetInt32(),
                summary.GetProperty("passed").GetInt32() + summary.GetProperty("failed").GetInt32() + summary.GetProperty("skipped").GetInt32());
            // The same domain validation the registration endpoint applies.
            var artifact = Artifact.Create(Guid.NewGuid(), Text("version"), Text("commitSha"), Text("branch"), Text("imageReference"),
                Enum.Parse<CheckStatus>(Text("buildStatus")), Enum.Parse<CheckStatus>(Text("testStatus")), DateTimeOffset.UtcNow,
                imageDigest: Text("imageDigest"), ciProvider: "ci-replay", createdByApiKeyId: Guid.NewGuid());
            Assert.StartsWith("sha256:", artifact.ImageDigest, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AegisOps.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
