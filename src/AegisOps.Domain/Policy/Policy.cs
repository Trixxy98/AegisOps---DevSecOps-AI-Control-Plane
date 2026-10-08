using AegisOps.Domain.Organization;

namespace AegisOps.Domain.Policy;

public sealed class Policy {
    public Guid Id {get; private set;}
    public string Name {get; private set;}
    public string? Description {get; private set;}
    public PolicyScope Scope {get; private set;}
    public Guid? ScopeId {get; private set;}
    public IReadOnlyList<EnvironmentTier> AppliesToTiers {get; private set;}
    public bool IsEnabled {get; private set;}
    public int Version {get; private set;}
    public Guid CreatedById {get; private set;}
    public Guid UpdatedById {get; private set;}
    public DateTimeOffset CreatedAt {get; private set;}
    public DateTimeOffset UpdatedAt {get; private set;}
    public DateTimeOffset? ArchivedAt {get; private set;}

    private Policy() {
        Name = string.Empty;
        AppliesToTiers = [];
    }

    public static Policy Create(
        string name,
        PolicyScope scope,
        IEnumerable<EnvironmentTier> appliesToTiers,
        Guid createdById,
        DateTimeOffset createdAt,
        Guid? scopeId = null,
        string? description = null
    ) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (!Enum.IsDefined(scope)) {
            throw new ArgumentException("Policy scope is invalid.", nameof(scope));
        }

        if (createdById == Guid.Empty) {
            throw new ArgumentException("Created-by ID is required.", nameof(createdById));
        }

        var normalizedScopeId = NormalizeScopeId(scope, scopeId);
        var tiers = NormalizeTiers(appliesToTiers);

        return new Policy {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Scope = scope,
            ScopeId = normalizedScopeId,
            AppliesToTiers = tiers,
            IsEnabled = true,
            Version = 1,
            CreatedById = createdById,
            UpdatedById = createdById,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    public void Rename(string name, Guid updatedById, DateTimeOffset updatedAt) {
        EnsureActive();
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        RequireActor(updatedById);
        Name = name.Trim();
        Touch(updatedById, updatedAt, bumpVersion: false);
    }

    public void Describe(string? description, Guid updatedById, DateTimeOffset updatedAt) {
        EnsureActive();
        RequireActor(updatedById);
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Touch(updatedById, updatedAt, bumpVersion: false);
    }

    public void SetTiers(IEnumerable<EnvironmentTier> appliesToTiers, Guid updatedById, DateTimeOffset updatedAt) {
        EnsureActive();
        RequireActor(updatedById);
        AppliesToTiers = NormalizeTiers(appliesToTiers);
        Touch(updatedById, updatedAt, bumpVersion: true);
    }

    public void Enable(Guid updatedById, DateTimeOffset updatedAt) {
        EnsureActive();
        RequireActor(updatedById);
        IsEnabled = true;
        Touch(updatedById, updatedAt, bumpVersion: false);
    }

    public void Disable(Guid updatedById, DateTimeOffset updatedAt) {
        EnsureActive();
        RequireActor(updatedById);
        IsEnabled = false;
        Touch(updatedById, updatedAt, bumpVersion: false);
    }

    public void Archive(Guid updatedById, DateTimeOffset archivedAt) {
        if (ArchivedAt is not null) {
            throw new InvalidOperationException("Policy is already archived.");
        }

        RequireActor(updatedById);
        if (archivedAt < CreatedAt) {
            throw new ArgumentException("Archive time must not be before creation.", nameof(archivedAt));
        }

        IsEnabled = false;
        ArchivedAt = archivedAt;
        Touch(updatedById, archivedAt, bumpVersion: false);
    }

    internal void BumpVersion(Guid updatedById, DateTimeOffset updatedAt) {
        EnsureActive();
        RequireActor(updatedById);
        Touch(updatedById, updatedAt, bumpVersion: true);
    }

    private static Guid? NormalizeScopeId(PolicyScope scope, Guid? scopeId) {
        if (scope == PolicyScope.Global) {
            if (scopeId is not null) {
                throw new ArgumentException("A global policy must not have a scope ID.", nameof(scopeId));
            }

            return null;
        }

        if (scopeId is null || scopeId == Guid.Empty) {
            throw new ArgumentException("A team or project policy requires a scope ID.", nameof(scopeId));
        }

        return scopeId;
    }

    private static EnvironmentTier[] NormalizeTiers(IEnumerable<EnvironmentTier> appliesToTiers) {
        if (appliesToTiers is null) {
            throw new ArgumentException("At least one environment tier is required.", nameof(appliesToTiers));
        }

        var tiers = appliesToTiers.Distinct().OrderBy(tier => tier).ToArray();
        if (tiers.Length == 0 || tiers.Any(tier => !Enum.IsDefined(tier))) {
            throw new ArgumentException("At least one valid environment tier is required.", nameof(appliesToTiers));
        }

        return tiers;
    }

    private void EnsureActive() {
        if (ArchivedAt is not null) {
            throw new InvalidOperationException("Policy is archived.");
        }
    }

    private static void RequireActor(Guid updatedById) {
        if (updatedById == Guid.Empty) {
            throw new ArgumentException("Updated-by ID is required.", nameof(updatedById));
        }
    }

    private void Touch(Guid updatedById, DateTimeOffset updatedAt, bool bumpVersion) {
        if (updatedAt < CreatedAt) {
            throw new ArgumentException("Update time must not be before creation.", nameof(updatedAt));
        }

        UpdatedById = updatedById;
        UpdatedAt = updatedAt;
        if (bumpVersion) {
            Version++;
        }
    }
}