namespace AegisOps.Domain.Organization;

public sealed class TeamWebhookSettings {
    public Guid TeamId { get; private set; }
    public string Url { get; private set; }
    public string EncryptedSecret { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private TeamWebhookSettings() {
        Url = string.Empty;
        EncryptedSecret = string.Empty;
    }

    public static TeamWebhookSettings Create(Guid teamId, string url, string encryptedSecret, DateTimeOffset updatedAt) {
        if (teamId == Guid.Empty) throw new ArgumentException("Team ID is required.", nameof(teamId));
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Webhook URL is required.", nameof(url));
        if (string.IsNullOrWhiteSpace(encryptedSecret)) throw new ArgumentException("Encrypted webhook secret is required.", nameof(encryptedSecret));
        return new TeamWebhookSettings {
            TeamId = teamId,
            Url = url.Trim(),
            EncryptedSecret = encryptedSecret,
            UpdatedAt = updatedAt,
        };
    }

    public void Update(string url, string encryptedSecret, DateTimeOffset updatedAt) {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Webhook URL is required.", nameof(url));
        if (string.IsNullOrWhiteSpace(encryptedSecret)) throw new ArgumentException("Encrypted webhook secret is required.", nameof(encryptedSecret));
        Url = url.Trim();
        EncryptedSecret = encryptedSecret;
        UpdatedAt = updatedAt;
    }

    public bool RewrapSecret(string encryptedSecret, DateTimeOffset updatedAt) {
        if (string.IsNullOrWhiteSpace(encryptedSecret)) throw new ArgumentException("Encrypted webhook secret is required.", nameof(encryptedSecret));
        if (string.Equals(EncryptedSecret, encryptedSecret, StringComparison.Ordinal)) return false;
        EncryptedSecret = encryptedSecret;
        UpdatedAt = updatedAt;
        return true;
    }
}
