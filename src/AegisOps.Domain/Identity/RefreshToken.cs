namespace AegisOps.Domain.Identity;

public sealed class RefreshToken {
    public Guid Id {get; private set;}
    public Guid UserId {get; private set;}
    public Guid FamilyId {get; private set;}
    public string TokenHash {get; private set;}
    public DateTimeOffset ExpiresAt {get; private set;}
    public DateTimeOffset? RevokedAt {get; private set;}
    public Guid? ReplacedByTokenId {get; private set;}
    public DateTimeOffset CreatedAt {get; private set;}
    public string? CreatedByIp {get; private set;}
    
    private RefreshToken() {
        TokenHash = string.Empty;
    }

    public static RefreshToken Create(
        Guid userId,
        Guid familyId,
        string tokenHash,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt,
        string? createdByIp
    ) {
        if (userId == Guid.Empty) {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        if (familyId == Guid.Empty) {
            throw new ArgumentException("Family ID is required.", nameof(familyId));
        }

        if (string.IsNullOrWhiteSpace(tokenHash)) {
            throw new ArgumentException("Token hash is required.", nameof(tokenHash));
        }

        if (expiresAt <= createdAt) {
            throw new ArgumentException("Expiry must be after creation.", nameof(expiresAt));
        }

        return new RefreshToken {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            CreatedAt = createdAt,
            CreatedByIp = createdByIp,
        };
    }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public void Revoke(DateTimeOffset revokedAt, Guid? replacedByTokenId) {
        if (RevokedAt is not null) {
            throw new InvalidOperationException("Refresh token is already revoked.");
        }

        if (revokedAt < CreatedAt) {
            throw new ArgumentException("Revocation must not be before creation.", nameof(revokedAt));
        }

        if (replacedByTokenId == Guid.Empty) {
            throw new ArgumentException("Replacement id is invalid.", nameof(replacedByTokenId));
        }

        if (replacedByTokenId == Id) {
            throw new ArgumentException("A token cannot replace itself.", nameof(replacedByTokenId));
        }

        RevokedAt = revokedAt;
        ReplacedByTokenId = replacedByTokenId;
    }
}