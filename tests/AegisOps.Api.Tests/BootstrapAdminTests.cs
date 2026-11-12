using AegisOps.Api.Hosting;
using AegisOps.Api.Identity;
using AegisOps.Application.Audit;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AegisOps.Api.Tests;

/// <summary>
/// Roadmap v1.1 items 1–2: the <c>migrate</c> command's role step and <c>bootstrap-admin</c>, against SQLite with the
/// production Identity registration (same pattern as <see cref="DevelopmentSeedTests"/>).
/// </summary>
public sealed class BootstrapAdminTests : IAsyncLifetime {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync() {
        await _connection.OpenAsync(Token);
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:Postgres"] = "Host=unused.invalid;Database=unused",
        }).Build();
        services.AddIdentityStore(configuration);
        services.RemoveAll<IDbContextOptionsConfiguration<AegisOpsDbContext>>();
        services.RemoveAll<DbContextOptions<AegisOpsDbContext>>();
        services.RemoveAll<DbContextOptions>();
        services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(_connection)
            .UseSnakeCaseNamingConvention().EnableServiceProviderCaching(false)
            .ReplaceService<IModelCustomizer, SqliteModelCustomizer>());
        services.AddScoped<IAuditWriter, EfAuditWriter>();
        _services = services.BuildServiceProvider(validateScopes: true);
    }

    public async ValueTask DisposeAsync() {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private Task<int> MigrateAsync(TextWriter output) =>
        AdminCommands.MigrateAsync(_services, output, static (db, token) => db.Database.EnsureCreatedAsync(token), Token);

    [Fact]
    public async Task MigrateCreatesTheFourRolesIdempotently() {
        using var first = new StringWriter();
        Assert.Equal(0, await MigrateAsync(first));
        Assert.Contains("Created roles: Admin, Developer, Security, Approver.", first.ToString(), StringComparison.Ordinal);
        // Without APP_DB_PASSWORD the restricted database role is left alone (Development keeps the owner connection).
        Assert.Contains("AppDatabase:Password (APP_DB_PASSWORD) is not set", first.ToString(), StringComparison.Ordinal);

        using var second = new StringWriter();
        Assert.Equal(0, await MigrateAsync(second));
        Assert.Contains("Roles already present", second.ToString(), StringComparison.Ordinal);

        await using var scope = _services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        Assert.Equal(["Admin", "Approver", "Developer", "Security"],
            (await roles.Roles.Select(role => role.Name!).ToListAsync(Token)).Order(StringComparer.Ordinal));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<UserManager<User>>().Users.ToListAsync(Token));
    }

    [Fact]
    public async Task BootstrapCreatesAnAdminThatMustChangeThePasswordAndAuditsWithoutIt() {
        Assert.Equal(0, await MigrateAsync(TextWriter.Null));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await BootstrapAdmin.RunAsync(_services, " owner@example.test ", "Platform Owner", output, error, Token);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error.ToString());
        var printed = output.ToString();
        var line = Assert.Single(printed.Split('\n'), item => item.StartsWith("Temporary password (shown only once): ", StringComparison.Ordinal));
        var temporary = line["Temporary password (shown only once): ".Length..].Trim();
        Assert.Equal(24, temporary.Length);
        Assert.Contains("change it at first sign-in", printed, StringComparison.Ordinal);

        await using var scope = _services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.FindByEmailAsync("owner@example.test");
        Assert.NotNull(user);
        Assert.Equal("Platform Owner", user.DisplayName);
        Assert.True(user.IsActive);
        Assert.True(user.MustChangePassword);
        Assert.Equal(["Admin"], await users.GetRolesAsync(user));
        Assert.True(await users.CheckPasswordAsync(user, temporary));

        var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
        var audit = Assert.Single(await db.AuditEvents.AsNoTracking().ToListAsync(Token));
        Assert.Equal(("user.bootstrapped", "System", (Guid?)null, "bootstrap-admin", "User", (Guid?)user.Id, "Succeeded"),
            (audit.Action, audit.ActorType, audit.ActorId, audit.ActorDisplay, audit.ResourceType, audit.ResourceId, audit.Outcome));
        Assert.DoesNotContain(temporary, string.Join("|", audit.ActorDisplay, audit.Action, audit.ResourceType, audit.Outcome,
            audit.CorrelationId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapRefusesWhenAnAdminAlreadyExists() {
        await MigrateAsync(TextWriter.Null);
        Assert.Equal(0, await BootstrapAdmin.RunAsync(_services, "first@example.test", null, TextWriter.Null, TextWriter.Null, Token));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await BootstrapAdmin.RunAsync(_services, "second@example.test", null, output, error, Token);

        Assert.Equal(BootstrapAdmin.Refused, exit);
        Assert.Contains("An Admin already exists", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
        await using var scope = _services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.Null(await users.FindByEmailAsync("second@example.test"));
        var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
        Assert.Equal(1, await db.AuditEvents.CountAsync(Token));
    }

    [Fact]
    public async Task BootstrapStillRefusesWhenTheOnlyAdminWasCreatedByTheSeed() {
        await MigrateAsync(TextWriter.Null);
        await using (var scope = _services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var existing = User.Create("seeded@example.test", "Seeded", DateTimeOffset.UtcNow);
            Assert.True((await users.CreateAsync(existing, "Correct-Horse-Battery-9!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(existing, IdentityRoles.Admin)).Succeeded);
        }

        Assert.Equal(BootstrapAdmin.Refused,
            await BootstrapAdmin.RunAsync(_services, "new@example.test", null, TextWriter.Null, TextWriter.Null, Token));
    }

    [Fact]
    public async Task BootstrapRefusesInvalidEmail() {
        await MigrateAsync(TextWriter.Null);
        using var error = new StringWriter();
        Assert.Equal(BootstrapAdmin.Refused, await BootstrapAdmin.RunAsync(_services, "not-an-email", null, TextWriter.Null, error, Token));
        Assert.Contains("valid email", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "--email", "a@example.test" }, "a@example.test", null)]
    [InlineData(new[] { "--email=a@example.test", "--display-name", "Ann Admin" }, "a@example.test", "Ann Admin")]
    public void BootstrapOptionsParse(string[] options, string email, string? displayName) {
        Assert.True(AdminCommands.TryParseBootstrapOptions(options, out var parsedEmail, out var parsedName, out var problem), problem);
        Assert.Equal((email, displayName), (parsedEmail, parsedName));
    }

    [Theory]
    [InlineData(new string[0], "--email is required.")]
    [InlineData(new[] { "--email" }, "--email needs a value.")]
    [InlineData(new[] { "--password", "x" }, "unknown option '--password'.")]
    public void BootstrapOptionsRejectBadInput(string[] options, string expected) {
        Assert.False(AdminCommands.TryParseBootstrapOptions(options, out _, out _, out var problem));
        Assert.Equal(expected, problem);
    }

    [Fact]
    public void OnlyKnownCommandsBypassTheWebHost() {
        Assert.True(AdminCommands.IsCommand(["migrate"]));
        Assert.True(AdminCommands.IsCommand(["bootstrap-admin", "--email", "a@example.test"]));
        Assert.False(AdminCommands.IsCommand([]));
        Assert.False(AdminCommands.IsCommand(["--urls=http://127.0.0.1:0"]));
        Assert.False(AdminCommands.IsCommand(["--environment=Production"]));
    }

    [Fact]
    public async Task MigrateRejectsExtraArgumentsWithAUsageError() {
        using var error = new StringWriter();
        Assert.Equal(AdminCommands.UsageError, await AdminCommands.RunAsync(["migrate", "--force"], TextWriter.Null, error));
        Assert.Contains("unexpected argument '--force'", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<Policy>().Property(policy => policy.AppliesToTiers)
                .HasConversion(new ValueConverter<IReadOnlyList<EnvironmentTier>, string>(
                    tiers => string.Join(",", tiers),
                    text => text.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(tier => Enum.Parse<EnvironmentTier>(tier)).ToArray()))
                .HasColumnType("TEXT");
        }
    }
}
