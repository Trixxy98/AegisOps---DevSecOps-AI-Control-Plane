namespace AegisOps.Domain.Organization;

public sealed class Team {
    public Guid Id {get; private set;}
    public string Name {get; private set;}
    public string Slug {get; private set;}
    public string? Description {get; private set;}
    public DateTimeOffset CreatedAt {get; private set;}

    private Team() {
        Name = string.Empty;
        Slug = string.Empty;
    }

    public static Team Create(
        string name,
        string slug,
        DateTimeOffset createdAt,
        string? description = null
    ) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(slug)) {
            throw new ArgumentException("Slug is required.", nameof(slug));
        }

        var normalizedSlug = slug.Trim().ToLowerInvariant();
        if (normalizedSlug.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) {
            throw new ArgumentException("Slug may only contain letters, digits, hyphens, and underscores.", nameof(slug));
        } 

        return new Team {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            Slug = normalizedSlug,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
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
}