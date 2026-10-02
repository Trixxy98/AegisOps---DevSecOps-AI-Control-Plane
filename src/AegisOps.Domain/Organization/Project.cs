namespace AegisOps.Domain.Organization;

public sealed class Project {
    public Guid Id {get; private set;}
    public Guid TeamId {get; private set;}
    public string Name {get; private set;}
    public string Slug {get; private set;}
    public string? Description {get; private set;}
    public bool IsArchived {get; private set;}
    public DateTimeOffset CreatedAt {get; private set;}

    private Project() {
        Name = string.Empty;
        Slug = string.Empty;
    }

    public static Project Create(
        Guid teamId,
        string name,
        string slug,
        DateTimeOffset createdAt,
        string? description = null
    ) {
        if (teamId == Guid.Empty) {
            throw new ArgumentException("Team ID is required.", nameof(teamId));
        }

        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(slug)) {
            throw new ArgumentException("Slug is required.", nameof(slug));
        }

        var normalizedSlug = slug.Trim().ToLowerInvariant();
        if (normalizedSlug.Any(character =>
            !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) {
                throw new ArgumentException(
                    "Slug may only contain letters, digits, hyphens, and underscores.",
                    nameof(slug)
                );
            }

            return new Project {
                Id = Guid.CreateVersion7(),
                TeamId = teamId,
                Name = name.Trim(),
                Slug = normalizedSlug,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                IsArchived = false,
                CreatedAt = createdAt,
            };
    }

    public void Rename(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        Name = name.Trim();
    }

    public void Describe(string? description) {
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    public void Archive() {
        if (IsArchived) {
            throw new InvalidOperationException("Project is already archived.");
        }

        IsArchived = true;
    }

    public void Restore() {
        if (!IsArchived) {
            throw new InvalidOperationException("Project is not archived.");
        }
        
        IsArchived = false;
    }
}