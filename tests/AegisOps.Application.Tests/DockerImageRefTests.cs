using AegisOps.Application.Deploy;

namespace AegisOps.Application.Tests;

public sealed class DockerImageRefTests {
    [Fact]
    public void Digest_replaces_the_tag() {
        var reference = DockerImageRef.ForPull(
            "ghcr.io/trixxy98/circuit-service:v0.0.0-main.52181e3",
            "sha256:fd6a8d25541213230d336a32a33b7ba6bb2630f149d4957a7f665592b5033934");
        Assert.Equal(
            "ghcr.io/trixxy98/circuit-service@sha256:fd6a8d25541213230d336a32a33b7ba6bb2630f149d4957a7f665592b5033934",
            reference);
    }

    [Fact]
    public void Missing_digest_keeps_the_tag() {
        Assert.Equal("ghcr.io/example/app:1.2.3", DockerImageRef.ForPull("ghcr.io/example/app:1.2.3", null));
    }

    [Fact]
    public void Digest_replaces_an_existing_digest() {
        Assert.Equal(
            "ghcr.io/example/app@sha256:new",
            DockerImageRef.ForPull("ghcr.io/example/app@sha256:old", "sha256:new"));
    }

    [Fact]
    public void Digest_replaces_both_tag_and_existing_digest() {
        Assert.Equal(
            "ghcr.io/example/app@sha256:new",
            DockerImageRef.ForPull("ghcr.io/example/app:1.2.3@sha256:old", "sha256:new"));
    }

    [Fact]
    public void Digest_preserves_registry_port() {
        Assert.Equal(
            "registry.example:5000/app@sha256:new",
            DockerImageRef.ForPull("registry.example:5000/app:1.2.3@sha256:old", "sha256:new"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Missing_digest_keeps_trimmed_input_unchanged(string? digest) {
        Assert.Equal(
            "registry.example:5000/app:1.2.3@sha256:old",
            DockerImageRef.ForPull("  registry.example:5000/app:1.2.3@sha256:old  ", digest));
    }
}
