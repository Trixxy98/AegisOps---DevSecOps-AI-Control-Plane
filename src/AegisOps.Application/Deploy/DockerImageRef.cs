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

        var slash = reference.LastIndexOf('/');
        var colon = reference.LastIndexOf(':');
        var name = colon > slash ? reference[..colon] : reference;
        return $"{name}@{digest.Trim()}";
    }
}
