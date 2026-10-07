namespace AegisOps.Domain.Identity;

public sealed class ApiKey {
    public static readonly IReadOnlyList<string> AllowedScopes = [
        "artifacts:write",
        "scans:write",
        "deployments:request",
    ];

    public Guid Id {get; private set;}
    public Guid ProjectId {get; private set;}
    public string Name {get; private set;}
    public string KeyPrefix {get; private set;}
    public string KeyHash {get; private set;}
    public IReadOnlyList<string> Scopes {get; private set;}
    public DateTimeOffset ExpiresAt {get; private set;}
    public DateTimeOffset? LastUsedAt {get; private set;}
    public DateTimeOffset? RevokedAt {get; private set;}
    public Guid CreatedById {get; private set;}
    public DateTimeOffset CreatedAt {get; private set;}

    private ApiKey() {
        Name = string.Empty;
        KeyPrefix = string.Empty;
        KeyHash = string.Empty;
        Scopes = [];
    }

    public static ApiKey Create(
        Guid projectId,
        string name,
        string keyPrefix,
        string keyHash,
        IEnumerable<string> scopes,
        DateTimeOffset expiresAt,
        Guid createdById,
        DateTimeOffset createdAt
    ) {
        if (projectId == Guid.Empty) {
            throw new ArgumentException("Project ID is required.", nameof(projectId));
        }

        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(keyPrefix) || keyPrefix.Length != 8) {
            throw new ArgumentException("Key prefix must be 8 characters.", nameof(keyPrefix));
        }

        if (string.IsNullOrWhiteSpace(keyHash)) {
            throw new ArgumentException("Key hash is required.", nameof(keyHash));
        }

        if (createdById == Guid.Empty) {
            throw new ArgumentException("Created-by ID is required.", nameof(createdById));
        }

        if (expiresAt <= createdAt) {
            throw new ArgumentException("Expiry must be after creation.", nameof(expiresAt));
        }

        if (expiresAt > createdAt.AddYears(1)) {
            throw new ArgumentException("Expiry must be within one year.", nameof(expiresAt));
        }

        var normalizedScopes = NormalizeScopes(scopes);

        return new ApiKey {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Name = name.Trim(),
            KeyPrefix = keyPrefix,
            KeyHash = keyHash,
            Scopes = normalizedScopes,
            ExpiresAt = expiresAt,
            CreatedById = createdById,
            CreatedAt = createdAt,
        };
    }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public void Revoke(DateTimeOffset revokedAt) {
        if (RevokedAt is not null) {
            throw new InvalidOperationException("API key is already revoked.");
        }

        if (revokedAt < CreatedAt) {
            throw new ArgumentException("Revocation must not be before creation.", nameof(revokedAt));
        }

        RevokedAt = revokedAt;
    }

    public void MarkUsed(DateTimeOffset usedAt) {
        if (!IsActive(usedAt)) {
            throw new InvalidOperationException("API key is not active.");
        }

        if (LastUsedAt is not null && usedAt < LastUsedAt.Value.AddMinutes(1)) {
            return;
        }

        LastUsedAt = usedAt;
    }

    private static string[] NormalizeScopes(IEnumerable<string> scopes) {
        if (scopes is null) {
            throw new ArgumentException("At least one scope is required.", nameof(scopes));
        }

        var normalized = scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0) {
            throw new ArgumentException("At least one scope is required.", nameof(scopes));
        }

        if (normalized.Any(scope => !AllowedScopes.Contains(scope, StringComparer.Ordinal))) {
            throw new ArgumentException(
                "Scopes must be artifacts:write, scans:write, or deployments:request.",
                nameof(scopes)
            );
        }

        return normalized;
    }
}