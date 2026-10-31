using System.Security.Cryptography;
using System.Text;
using AegisOps.Infrastructure.Deploy;
using Microsoft.Extensions.Configuration;

namespace AegisOps.Api.Tests;

public sealed class TeamWebhookSecretProtectorTests {
    [Fact]
    public void Protect_binds_ciphertext_to_team_and_detects_tampering() {
        var protector = CreateProtector("current", ("current", Key(7)));
        var teamId = Guid.NewGuid();
        var secret = Encoding.UTF8.GetBytes("a sufficiently long webhook signing secret");

        var protectedSecret = protector.Protect(teamId, secret);

        Assert.DoesNotContain(Encoding.UTF8.GetString(secret), protectedSecret, StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(teamId, protectedSecret));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Guid.NewGuid(), protectedSecret));
        var tampered = protectedSecret[..^1] + (protectedSecret[^1] == 'A' ? "B" : "A");
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(teamId, tampered));
    }

    [Fact]
    public void Rewrap_uses_current_key_while_retaining_old_key_for_existing_ciphertext() {
        var teamId = Guid.NewGuid();
        var secret = Key(11);
        var oldProtector = CreateProtector("old", ("old", Key(3)));
        var ciphertext = oldProtector.Protect(teamId, secret);
        var rotatingProtector = CreateProtector("new", ("old", Key(3)), ("new", Key(5)));

        var rewrapped = rotatingProtector.Rewrap(teamId, ciphertext);
        var newOnlyProtector = CreateProtector("new", ("new", Key(5)));

        Assert.NotEqual(ciphertext, rewrapped);
        Assert.Equal(secret, newOnlyProtector.Unprotect(teamId, rewrapped));
    }

    [Fact]
    public void Key_ring_rejects_invalid_key_material() {
        var configuration = Configuration("current", ("current", new byte[16]));

        Assert.Throws<InvalidOperationException>(() => new TeamWebhookSecretProtector(configuration));
    }

    [Fact]
    public void Key_ring_can_decrypt_existing_rows_when_management_is_disabled() {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["WebhookSecrets:CurrentKeyId"] = "current",
            ["WebhookSecrets:Keys:current"] = Convert.ToBase64String(Key(13)),
        }).Build();
        var protector = new TeamWebhookSecretProtector(configuration);
        var secret = Key(14);
        var teamId = Guid.NewGuid();

        Assert.False(protector.IsEnabled);
        Assert.True(protector.IsConfigured);
        Assert.Equal(secret, protector.Unprotect(teamId, protector.Protect(teamId, secret)));
    }

    private static TeamWebhookSecretProtector CreateProtector(string currentKeyId, params (string Id, byte[] Key)[] keys) =>
        new(Configuration(currentKeyId, keys));

    private static IConfiguration Configuration(string currentKeyId, params (string Id, byte[] Key)[] keys) {
        var values = new Dictionary<string, string?> {
            ["WebhookSecrets:Enabled"] = "true",
            ["WebhookSecrets:CurrentKeyId"] = currentKeyId,
            ["WebhookSecrets:Keys:unused"] = "",
        };
        foreach (var (id, key) in keys) {
            values[$"WebhookSecrets:Keys:{id}"] = Convert.ToBase64String(key);
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static byte[] Key(byte value) => Enumerable.Repeat(value, 32).ToArray();
}
