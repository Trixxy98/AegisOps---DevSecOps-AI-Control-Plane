using Microsoft.AspNetCore.Identity;

namespace AegisOps.Domain.Identity;

public sealed class User : IdentityUser<Guid> {
    public string DisplayName {get; private set;}
    public bool IsActive {get; private set;}
    public bool MustChangePassword {get; private set;}
    public DateTimeOffset CreatedAt {get; private set;}

    private User() {
        DisplayName = string.Empty;
    }

    public static User Create(string email, string displayName, DateTimeOffset createdAt) {
        if (string.IsNullOrWhiteSpace(email)) {
            throw new ArgumentException("Email is required.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(displayName)) {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        var normalizedEmail = email.Trim();

        return new User {
            Id = Guid.CreateVersion7(),
            UserName = normalizedEmail,
            Email = normalizedEmail,
            NormalizedUserName = normalizedEmail.ToUpperInvariant(),
            NormalizedEmail = normalizedEmail.ToUpperInvariant(),
            EmailConfirmed = true,
            DisplayName = displayName.Trim(),
            IsActive = true,
            MustChangePassword = true,
            CreatedAt = createdAt,
        };
    }

    public void Deactived() {
        if (!IsActive) {
            throw new InvalidOperationException("User is already inactive.");
        }

        IsActive = false;
        LockoutEnabled = true;
        LockoutEnd = DateTimeOffset.MaxValue;
    }

    public void Active() {
        if (IsActive) {
            throw new InvalidOperationException("User is already active.");
        }

        IsActive = true;
        LockoutEnd = null;
    }

    public void Rename(string displayName) {
        if (string.IsNullOrWhiteSpace(displayName)) {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        DisplayName = displayName.Trim();
    }

    public void MarkPasswordChanged() {
        MustChangePassword = false;
    }
}