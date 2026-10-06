namespace AegisOps.Domain.Organization;

public sealed class Repository {
    public Guid Id {get; private set;}
    public Guid ProjectId {get; private set;}
    public RepositoryProvider Provider {get; private set;}
    public string FullName {get; private set;}
    public string DefaultBranch {get; private set;}
    public string HtmlUrl {get; private set;}

    private Repository() {
        FullName = string.Empty;
        DefaultBranch = string.Empty;
        HtmlUrl = string.Empty;
    }

    public static Repository Create(
        Guid projectId,
        RepositoryProvider provider,
        string fullName,
        string defaultBranch,
        string htmlUrl
    ) {
        if (projectId == Guid.Empty) {
            throw new ArgumentException("Project ID is required.", nameof(projectId));
        }

        if (!Enum.IsDefined(provider)) {
            throw new ArgumentException("Repository provider is invalid.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(fullName) || !fullName.Contains('/', StringComparison.Ordinal)) {
            throw new ArgumentException("Full name must look like owner/repo.", nameof(fullName));
        }

        if (string.IsNullOrWhiteSpace(defaultBranch)) {
            throw new ArgumentException("Default branch is required.", nameof(defaultBranch));
        }

        if (string.IsNullOrWhiteSpace(htmlUrl)
            || !Uri.TryCreate(htmlUrl.Trim(), UriKind.Absolute, out _)) {
            throw new ArgumentException("HTML URL must be an absolute URL.", nameof(htmlUrl));
        }

        return new Repository {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Provider = provider,
            FullName = fullName.Trim(),
            DefaultBranch = defaultBranch.Trim(),
            HtmlUrl = htmlUrl.Trim(),
        };
    }

    public void Replace(
        RepositoryProvider provider,
        string fullName,
        string defaultBranch,
        string htmlUrl
    ) {
        var replacement = Create(ProjectId, provider, fullName, defaultBranch, htmlUrl);
        Provider = replacement.Provider;
        FullName = replacement.FullName;
        DefaultBranch = replacement.DefaultBranch;
        HtmlUrl = replacement.HtmlUrl;
    }
}