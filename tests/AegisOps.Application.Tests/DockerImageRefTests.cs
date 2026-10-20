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
}
