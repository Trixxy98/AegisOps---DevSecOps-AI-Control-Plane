using System.Security.Claims;
using System.Text.Json;
using AegisOps.Api.Organization;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit;

namespace AegisOps.Api.Tests;

public sealed class TeamWebhookManagementTests {
    [Fact]
    public async Task Owner_can_update_settings_but_secret_is_never_returned() {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(cancellationToken);
        var secret = Enumerable.Repeat((byte)41, 32).ToArray();
        var protector = new TeamWebhookSecretProtector(Configuration("new", ("new", Key(1))));
        var response = await TeamWebhookSettingsEndpoints.Set(
            fixture.Team.Slug,
            new SetTeamWebhookRequest("https://example.com/hook", Convert.ToBase64String(secret)),
            Principal(fixture.Owner.Id), fixture.Users, fixture.Db, protector, Context(cancellationToken));

        Assert.Equal(StatusCodes.Status200OK, Status(response));
        var returned = Assert.IsType<TeamWebhookSettingsResponse>(Assert.IsAssignableFrom<IValueHttpResult>(response).Value);
        Assert.True(returned.SecretConfigured);
        Assert.Equal("https://example.com/hook", returned.Url);
        var stored = await fixture.Db.TeamWebhookSettings.SingleAsync(cancellationToken);
        Assert.DoesNotContain(Convert.ToBase64String(secret), stored.EncryptedSecret, StringComparison.Ordinal);

        var get = await TeamWebhookSettingsEndpoints.Get(fixture.Team.Slug, Principal(fixture.Owner.Id), fixture.Users, fixture.Db, Context(cancellationToken));
        var metadata = Assert.IsType<TeamWebhookSettingsResponse>(Assert.IsAssignableFrom<IValueHttpResult>(get).Value);
        Assert.True(metadata.SecretConfigured);
        Assert.DoesNotContain(Convert.ToBase64String(secret), JsonSerializer.Serialize(metadata), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_rejects_query_credentials_and_oversized_secret_input() {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(cancellationToken);
        var protector = new TeamWebhookSecretProtector(Configuration("new", ("new", Key(1))));
        var queryUrl = await TeamWebhookSettingsEndpoints.Set(
            fixture.Team.Slug,
            new SetTeamWebhookRequest("https://example.com/hook?token=do-not-store", Convert.ToBase64String(Key(2))),
            Principal(fixture.Owner.Id), fixture.Users, fixture.Db, protector, Context(cancellationToken));
        var oversizedSecret = await TeamWebhookSettingsEndpoints.Set(
            fixture.Team.Slug,
            new SetTeamWebhookRequest("https://example.com/hook", new string('A', 1369)),
            Principal(fixture.Owner.Id), fixture.Users, fixture.Db, protector, Context(cancellationToken));

        Assert.Equal(StatusCodes.Status400BadRequest, Status(queryUrl));
        Assert.Equal(StatusCodes.Status400BadRequest, Status(oversizedSecret));
        Assert.Empty(await fixture.Db.TeamWebhookSettings.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Non_owner_cannot_read_or_update_team_webhook_settings() {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(cancellationToken);
        var protector = new TeamWebhookSecretProtector(Configuration("new", ("new", Key(2))));
        var get = await TeamWebhookSettingsEndpoints.Get(fixture.Team.Slug, Principal(fixture.Member.Id), fixture.Users, fixture.Db, Context(cancellationToken));
        var set = await TeamWebhookSettingsEndpoints.Set(
            fixture.Team.Slug,
            new SetTeamWebhookRequest("https://example.com/hook", Convert.ToBase64String(Key(3))),
            Principal(fixture.Member.Id), fixture.Users, fixture.Db, protector, Context(cancellationToken));
        var rewrap = await TeamWebhookSettingsEndpoints.Rewrap(Principal(fixture.Owner.Id), fixture.Users, fixture.Db, protector, Context(cancellationToken));

        Assert.Equal(StatusCodes.Status404NotFound, Status(get));
        Assert.Equal(StatusCodes.Status404NotFound, Status(set));
        Assert.IsType<ForbidHttpResult>(rewrap);
        Assert.Empty(await fixture.Db.TeamWebhookSettings.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Admin_rewraps_all_secrets_to_active_key_without_returning_them() {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(cancellationToken);
        var secret = Key(22);
        var teamId = fixture.Team.Id;
        var oldProtector = new TeamWebhookSecretProtector(Configuration("old", ("old", Key(4))));
        fixture.Db.TeamWebhookSettings.Add(TeamWebhookSettings.Create(teamId, "https://example.com/hook", oldProtector.Protect(teamId, secret), DateTimeOffset.UtcNow));
        await fixture.Db.SaveChangesAsync(cancellationToken);
        var newConfig = Configuration("new", ("old", Key(4)), ("new", Key(5)));
        var response = await TeamWebhookSettingsEndpoints.Rewrap(Principal(fixture.Admin.Id), fixture.Users, fixture.Db,
            new TeamWebhookSecretProtector(newConfig), Context(cancellationToken));

        Assert.Equal(StatusCodes.Status200OK, Status(response));
        var metadata = Assert.IsAssignableFrom<IValueHttpResult>(response).Value!;
        Assert.Equal(1, (int)metadata.GetType().GetProperty("Rewrapped")!.GetValue(metadata)!);
        var stored = await fixture.Db.TeamWebhookSettings.SingleAsync(cancellationToken);
        var newOnlyProtector = new TeamWebhookSecretProtector(Configuration("new", ("new", Key(5))));
        Assert.Equal(secret, newOnlyProtector.Unprotect(teamId, stored.EncryptedSecret));
        Assert.DoesNotContain(Convert.ToBase64String(secret), JsonSerializer.Serialize(metadata), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_rewrap_fails_safely_without_mutating_rows_when_ciphertext_is_invalid() {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(cancellationToken);
        var oldProtector = new TeamWebhookSecretProtector(Configuration("old", ("old", Key(4))));
        var ciphertext = oldProtector.Protect(fixture.Team.Id, Key(8));
        var corrupted = ciphertext[..^1] + (ciphertext[^1] == 'A' ? "B" : "A");
        fixture.Db.TeamWebhookSettings.Add(TeamWebhookSettings.Create(fixture.Team.Id, "https://example.com/hook", corrupted, DateTimeOffset.UtcNow));
        await fixture.Db.SaveChangesAsync(cancellationToken);
        var rotatingProtector = new TeamWebhookSecretProtector(Configuration("new", ("old", Key(4)), ("new", Key(5))));

        var response = await TeamWebhookSettingsEndpoints.Rewrap(Principal(fixture.Admin.Id), fixture.Users, fixture.Db,
            rotatingProtector, Context(cancellationToken));

        Assert.Equal(StatusCodes.Status409Conflict, Status(response));
        Assert.Equal(corrupted, (await fixture.Db.TeamWebhookSettings.SingleAsync(cancellationToken)).EncryptedSecret);
    }

    [Fact]
    public async Task Set_fails_closed_when_management_is_disabled_even_if_a_key_remains_available() {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(cancellationToken);
        var disabledConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["WebhookSecrets:CurrentKeyId"] = "old",
            ["WebhookSecrets:Keys:old"] = Convert.ToBase64String(Key(6)),
        }).Build();
        var protector = new TeamWebhookSecretProtector(disabledConfig);
        var result = await TeamWebhookSettingsEndpoints.Set(
            fixture.Team.Slug,
            new SetTeamWebhookRequest("https://example.com/hook", Convert.ToBase64String(Key(9))),
            Principal(fixture.Owner.Id), fixture.Users, fixture.Db, protector, Context(cancellationToken));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Status(result));
        Assert.Empty(await fixture.Db.TeamWebhookSettings.ToListAsync(cancellationToken));
    }

    private static ClaimsPrincipal Principal(Guid userId) => new(new ClaimsIdentity(
        [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "test"));

    private static DefaultHttpContext Context(CancellationToken cancellationToken) => new() { RequestAborted = cancellationToken };

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? StatusCodes.Status200OK;

    private static IConfiguration Configuration(string currentKeyId, params (string Id, byte[] Key)[] keys) {
        var values = new Dictionary<string, string?> {
            ["WebhookSecrets:Enabled"] = "true",
            ["WebhookSecrets:CurrentKeyId"] = currentKeyId,
        };
        foreach (var (id, key) in keys) values[$"WebhookSecrets:Keys:{id}"] = Convert.ToBase64String(key);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static byte[] Key(byte value) => Enumerable.Repeat(value, 32).ToArray();

    private sealed class Fixture : IAsyncDisposable {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly ServiceProvider services;
        public AegisOpsDbContext Db { get; }
        public UserManager<User> Users { get; }
        public Team Team { get; }
        public User Owner { get; }
        public User Member { get; }
        public User Admin { get; }

        private Fixture() {
            connection.Open();
            services = new ServiceCollection()
                .AddLogging()
                .AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection)
                    .UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
                .AddIdentityCore<User>().AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AegisOpsDbContext>()
                .Services.BuildServiceProvider();
            Db = services.GetRequiredService<AegisOpsDbContext>();
            Users = services.GetRequiredService<UserManager<User>>();
            Db.Database.EnsureCreated();
            var now = DateTimeOffset.UtcNow;
            Team = Team.Create("Webhook Team", $"webhook-{Guid.NewGuid():N}", now);
            Owner = User.Create($"owner-{Guid.NewGuid():N}@example.test", "Owner", now);
            Member = User.Create($"member-{Guid.NewGuid():N}@example.test", "Member", now);
            Admin = User.Create($"admin-{Guid.NewGuid():N}@example.test", "Admin", now);
        }

        public static async Task<Fixture> Create(CancellationToken cancellationToken) {
            var fixture = new Fixture();
            await fixture.Users.CreateAsync(fixture.Owner);
            await fixture.Users.CreateAsync(fixture.Member);
            await fixture.Users.CreateAsync(fixture.Admin);
            var role = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "Admin", NormalizedName = "ADMIN" };
            fixture.Db.Roles.Add(role);
            fixture.Db.TeamMembers.AddRange(
                TeamMember.Create(fixture.Team.Id, fixture.Owner.Id, TeamRole.Owner, DateTimeOffset.UtcNow),
                TeamMember.Create(fixture.Team.Id, fixture.Member.Id, TeamRole.Member, DateTimeOffset.UtcNow));
            fixture.Db.Teams.Add(fixture.Team);
            fixture.Db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = fixture.Admin.Id, RoleId = role.Id });
            await fixture.Db.SaveChangesAsync(cancellationToken);
            return fixture;
        }

        public async ValueTask DisposeAsync() {
            await services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<Policy>().Ignore(policy => policy.AppliesToTiers);
            modelBuilder.Entity<Deployment>().Property(item => item.RequestedAt)
                .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<DeploymentEvent>().Property(item => item.Timestamp)
                .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
        }
    }
}
