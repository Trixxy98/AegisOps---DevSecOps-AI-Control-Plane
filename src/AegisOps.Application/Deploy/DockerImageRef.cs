namespace AegisOps.Application.Deploy;

public static class DockerImageRef {
    public static string ForPull(string imageReference, string? digest) {
        if (string.IsNullOrWhiteSpace(imageReference)) {
            throw new ArgumentException("Image reference is required.", nameof(imageReference));
        }

        var reference = imageReference.Trim();
        if (string.IsNullOrWhiteSpace(digest)) {
            return reference;
        }

        var at = reference.IndexOf('@');
        var withoutDigest = at >= 0 ? reference[..at] : reference;
        var slash = withoutDigest.LastIndexOf('/');
        var colon = withoutDigest.LastIndexOf(':');
        var name = colon > slash ? withoutDigest[..colon] : withoutDigest;
        return $"{name}@{digest.Trim()}";
    }
}
