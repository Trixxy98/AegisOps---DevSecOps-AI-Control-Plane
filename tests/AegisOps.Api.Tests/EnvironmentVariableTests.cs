using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using AegisOps.Api.Organization;
using AegisOps.Application.Audit;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Docker;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectEnvironment = AegisOps.Domain.Organization.Environment;

namespace AegisOps.Api.Tests;

/// <summary>Roadmap 0a: per-environment variables and write-only secrets injected at deploy time.</summary>
public sealed class EnvironmentVariableTests {
    private const string Slug = "site-service";
    private const string SecretValue = "s3cr3t-database-password-value";
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------- Domain ----------

    [Theory]
    [InlineData("ConnectionStrings__Main")]
    [InlineData("_PRIVATE")]
    [InlineData("a")]
    public void Valid_names_are_accepted(string name) {
        var variable = EnvironmentVariable.CreatePlain(Guid.NewGuid(), name, "value", Guid.NewGuid(), Now);
        Assert.Equal(name, variable.Name);
        Assert.False(variable.IsSecret);
        Assert.Null(variable.ProtectedValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1ABC")]
    [InlineData("WITH-DASH")]
    [InlineData("WITH SPACE")]
    [InlineData("DOTNET_ENVIRONMENT")]
    [InlineData("aspnetcore_environment")]
    [InlineData("AEGISOPS_TOKEN")]
    [InlineData("aegisops_anything")]
    public void Invalid_or_reserved_names_are_rejected(string name) {
        Assert.Throws<ArgumentException>(() => EnvironmentVariable.Validate(name, "value"));
    }

    [Fact]
    public void Name_length_and_value_limits_are_enforced() {
        EnvironmentVariable.Validate("A" + new string('b', 127), new string('x', EnvironmentVariable.MaxValueLength));
        Assert.Throws<ArgumentException>(() => EnvironmentVariable.Validate("A" + new string('b', 128), "v"));
        Assert.Throws<ArgumentException>(() => EnvironmentVariable.Validate("NAME", new string('x', EnvironmentVariable.MaxValueLength + 1)));
        Assert.Throws<ArgumentException>(() => EnvironmentVariable.Validate("NAME", "a\0b"));
    }

    [Fact]
    public void Switching_between_plain_and_secret_clears_the_other_value() {
        var user = Guid.NewGuid();
        var variable = EnvironmentVariable.CreatePlain(Guid.NewGuid(), "NAME", "plain", user, Now);
        variable.SetSecret("v1.k.n.c.t", user, Now);
        Assert.True(variable.IsSecret);
        Assert.Null(variable.Value);
        variable.SetPlain("plain-again", user, Now);
        Assert.False(variable.IsSecret);
        Assert.Null(variable.ProtectedValue);
    }

    // ---------- Secret protector ----------

    [Fact]
    public void Protector_binds_ciphertext_to_environment_and_name() {
        var protector = Protector(("k1", Key(1)));
        var environmentId = Guid.NewGuid();
        var envelope = protector.Protect(environmentId, "DB_PASSWORD", SecretValue);

        Assert.DoesNotContain(SecretValue, envelope, StringComparison.Ordinal);
        Assert.Equal(SecretValue, protector.Unprotect(environmentId, "DB_PASSWORD", envelope));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Guid.NewGuid(), "DB_PASSWORD", envelope));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(environmentId, "OTHER", envelope));
    }

    [Fact]
    public void Protector_is_not_usable_without_configuration_and_rejects_bad_keys() {
        var empty = new EnvironmentSecretProtector(new ConfigurationBuilder().Build());
        Assert.False(empty.CanProtect);
        Assert.False(empty.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => empty.Protect(Guid.NewGuid(), "NAME", "value"));

        Assert.Throws<InvalidOperationException>(() => Protector(("k1", new byte[16])));
        // Enabled without any key is a configuration error, not silent plaintext.
        Assert.Throws<InvalidOperationException>(() => new EnvironmentSecretProtector(Configuration(enabled: true)));
    }

    [Fact]
    public void Protector_keeps_old_keys_for_decryption_after_rotation() {
        var environmentId = Guid.NewGuid();
        var envelope = Protector(("old", Key(2))).Protect(environmentId, "NAME", SecretValue);
        var rotated = new EnvironmentSecretProtector(Configuration(true, "new", ("old", Key(2)), ("new", Key(3))));

        Assert.Equal(SecretValue, rotated.Unprotect(environmentId, "NAME", envelope));
        Assert.Contains(".new.", rotated.Protect(environmentId, "NAME", SecretValue), StringComparison.Ordinal);
    }

    // ---------- Container environment composition ----------

    [Fact]
    public void Composed_environment_keeps_reserved_entries_first_and_never_overridable() {
        var env = DockerDeploymentExecutor.ComposeContainerEnvironment("Staging", [
            new("ZED", "last"),
            new("ConnectionStrings__Main", "Host=db"),
            new("DOTNET_ENVIRONMENT", "Production"),
            new("aspnetcore_environment", "Production"),
            new("AEGISOPS_INTERNAL", "x"),
            new("BAD-NAME", "x"),
            new("EMPTY", ""),
            new("WITH_EQUALS", "a=b=c"),
        ]);

        Assert.Equal([
            "DOTNET_ENVIRONMENT=Staging",
            "ASPNETCORE_ENVIRONMENT=Staging",
            "ConnectionStrings__Main=Host=db",
            "EMPTY=",
            "WITH_EQUALS=a=b=c",
            "ZED=last",
        ], env);
    }

    [Fact]
    public void Composed_environment_without_variables_matches_the_reserved_entries() {
        Assert.Equal(DockerDeploymentExecutor.ContainerEnvironment("Production"),
            DockerDeploymentExecutor.ComposeContainerEnvironment("Production", []));
    }

    // ---------- Worker loading ----------

    [Fact]
    public async Task Worker_loads_plain_and_decrypted_secret_variables() {
        await using var store = await Store.CreateAsync();
        var protector = Protector(("k1", Key(4)));
        await store.AddAsync(EnvironmentVariable.CreatePlain(store.Environment.Id, "PLAIN", "p", Guid.NewGuid(), Now),
            EnvironmentVariable.CreateSecret(store.Environment.Id, "SECRET", protector.Protect(store.Environment.Id, "SECRET", SecretValue), Guid.NewGuid(), Now));

        await using var db = store.Context();
        var loaded = await DeploymentProcessor.LoadVariablesAsync(db, store.Environment.Id, () => protector, Token);

        Assert.Null(loaded.Error);
        Assert.Equal([KeyValuePair.Create("PLAIN", "p"), KeyValuePair.Create("SECRET", SecretValue)], loaded.Values);
    }

    [Fact]
    public async Task Worker_fails_clearly_without_the_value_when_a_secret_cannot_be_decrypted() {
        await using var store = await Store.CreateAsync();
        var original = Protector(("k1", Key(5)));
        await store.AddAsync(EnvironmentVariable.CreateSecret(store.Environment.Id, "SECRET",
            original.Protect(store.Environment.Id, "SECRET", SecretValue), Guid.NewGuid(), Now));

        await using var db = store.Context();
        var missingKey = await DeploymentProcessor.LoadVariablesAsync(db, store.Environment.Id, () => Protector(("k2", Key(6))), Token);
        Assert.Empty(missingKey.Values);
        Assert.Contains("SECRET could not be decrypted", missingKey.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, missingKey.Error!, StringComparison.Ordinal);

        var unconfigured = await DeploymentProcessor.LoadVariablesAsync(db, store.Environment.Id,
            () => new EnvironmentSecretProtector(new ConfigurationBuilder().Build()), Token);
        Assert.Contains("secret storage is not configured", unconfigured.Error, StringComparison.Ordinal);

        var invalidConfig = await DeploymentProcessor.LoadVariablesAsync(db, store.Environment.Id,
            () => new EnvironmentSecretProtector(Configuration(enabled: true)), Token);
        Assert.Contains("secret storage is not configured", invalidConfig.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Variables_cascade_with_their_environment_and_the_model_is_migrated() {
        await using var store = await Store.CreateAsync();
        await store.AddAsync(EnvironmentVariable.CreatePlain(store.Environment.Id, "PLAIN", "p", Guid.NewGuid(), Now));
        await using (var db = store.Context()) {
            db.Environments.Remove(await db.Environments.SingleAsync(item => item.Id == store.Environment.Id, Token));
            await db.SaveChangesAsync(Token);
            Assert.Empty(await db.EnvironmentVariables.ToListAsync(Token));
        }

        var options = new DbContextOptionsBuilder<AegisOpsDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused").UseSnakeCaseNamingConvention()
            .EnableServiceProviderCaching(false).Options;
        using var postgres = new AegisOpsDbContext(options);
        var migrations = postgres.GetService<IMigrationsAssembly>();
        Assert.Contains(migrations.Migrations.Keys, id => id.EndsWith("_EnvironmentVariables", StringComparison.Ordinal));
        var entity = migrations.ModelSnapshot?.Model.FindEntityType(typeof(EnvironmentVariable).FullName!);
        Assert.NotNull(entity);
        Assert.Equal("environment_variables", entity.GetTableName());
        Assert.Equal("org", entity.GetSchema());
    }

    // ---------- API ----------

    [Fact]
    public async Task Owner_sets_plain_and_secret_values_and_secrets_are_never_returned() {
        await using var host = await Host.CreateAsync(secretsEnabled: true);

        var created = await host.PutAsync(host.Owner, "ConnectionStrings__Main", "Host=db;Database=site", false, HttpStatusCode.Created);
        Assert.Equal("Host=db;Database=site", created.GetProperty("value").GetString());
        var updated = await host.PutAsync(host.Owner, "ConnectionStrings__Main", "Host=db2", false, HttpStatusCode.OK);
        Assert.Equal("Host=db2", updated.GetProperty("value").GetString());
        var secret = await host.PutAsync(host.Owner, "DB_PASSWORD", SecretValue, true, HttpStatusCode.Created);
        Assert.True(secret.GetProperty("isSecret").GetBoolean());
        Assert.Equal(JsonValueKind.Null, secret.GetProperty("value").ValueKind);

        foreach (var reader in new[] { host.Member, host.Security, host.Admin }) {
            var raw = await host.SendRawAsync(reader, HttpMethod.Get, null, null, HttpStatusCode.OK);
            Assert.DoesNotContain(SecretValue, raw, StringComparison.Ordinal);
            var list = JsonDocument.Parse(raw).RootElement;
            Assert.Equal(["ConnectionStrings__Main", "DB_PASSWORD"], list.EnumerateArray().Select(item => item.GetProperty("name").GetString()));
            Assert.Equal(JsonValueKind.Null, list[1].GetProperty("value").ValueKind);
            Assert.True(list[1].GetProperty("isSecret").GetBoolean());
        }

        await using var db = host.Store.Context();
        var stored = await db.EnvironmentVariables.AsNoTracking().SingleAsync(item => item.Name == "DB_PASSWORD", Token);
        Assert.Null(stored.Value);
        Assert.DoesNotContain(SecretValue, stored.ProtectedValue!, StringComparison.Ordinal);
        Assert.Equal(host.Owner, stored.UpdatedById);
        var audit = await db.AuditEvents.AsNoTracking().ToListAsync(Token);
        Assert.Equal(3, audit.Count(item => item.Action == "environment.variable_set" && item.ResourceId == host.Store.Environment.Id));
    }

    [Fact]
    public async Task Writes_need_an_owner_or_admin_and_reads_need_team_visibility() {
        await using var host = await Host.CreateAsync(secretsEnabled: true);

        await host.SendRawAsync(host.Member, HttpMethod.Put, "NAME", new { value = "v", isSecret = false }, HttpStatusCode.NotFound);
        await host.SendRawAsync(host.Security, HttpMethod.Put, "NAME", new { value = "v", isSecret = false }, HttpStatusCode.NotFound);
        await host.SendRawAsync(host.Outsider, HttpMethod.Get, null, null, HttpStatusCode.NotFound);
        await host.SendRawAsync(null, HttpMethod.Get, null, null, HttpStatusCode.Unauthorized);
        await host.PutAsync(host.Admin, "NAME", "v", false, HttpStatusCode.Created);
        await host.SendRawAsync(host.Member, HttpMethod.Delete, "NAME", null, HttpStatusCode.NotFound);
        await host.SendRawAsync(host.Owner, HttpMethod.Delete, "NAME", null, HttpStatusCode.NoContent);
        await host.SendRawAsync(host.Owner, HttpMethod.Delete, "NAME", null, HttpStatusCode.NotFound);
        await host.SendRawAsync(host.Owner, HttpMethod.Get, null, null, HttpStatusCode.OK);
        await host.SendRawAsync(host.Owner, HttpMethod.Get, null, null, HttpStatusCode.NotFound, knownEnvironment: false);
        await host.SendRawAsync(host.Owner, HttpMethod.Put, "NAME", new { value = "v", isSecret = false }, HttpStatusCode.NotFound, knownEnvironment: false);
    }

    [Fact]
    public async Task Secret_without_configured_storage_is_a_conflict_but_plain_values_work() {
        await using var host = await Host.CreateAsync(secretsEnabled: false);

        var raw = await host.SendRawAsync(host.Owner, HttpMethod.Put, "DB_PASSWORD", new { value = SecretValue, isSecret = true }, HttpStatusCode.Conflict);
        Assert.Equal("Secret storage is not configured", JsonDocument.Parse(raw).RootElement.GetProperty("title").GetString()!.TrimEnd('.'));
        Assert.DoesNotContain(SecretValue, raw, StringComparison.Ordinal);
        await host.PutAsync(host.Owner, "PLAIN", "value", false, HttpStatusCode.Created);
        await using var db = host.Store.Context();
        Assert.Equal(["PLAIN"], await db.EnvironmentVariables.Select(item => item.Name).ToListAsync(Token));
    }

    [Fact]
    public async Task Invalid_reserved_and_excess_variables_are_rejected() {
        await using var host = await Host.CreateAsync(secretsEnabled: true);

        await host.SendRawAsync(host.Owner, HttpMethod.Put, "ASPNETCORE_ENVIRONMENT", new { value = "Production", isSecret = false }, HttpStatusCode.BadRequest);
        await host.SendRawAsync(host.Owner, HttpMethod.Put, "AEGISOPS_X", new { value = "x", isSecret = false }, HttpStatusCode.BadRequest);
        await host.SendRawAsync(host.Owner, HttpMethod.Put, "BAD-NAME", new { value = "x", isSecret = false }, HttpStatusCode.BadRequest);
        await host.SendRawAsync(host.Owner, HttpMethod.Put, "NAME", new { isSecret = false }, HttpStatusCode.BadRequest);
        await host.SendRawAsync(host.Owner, HttpMethod.Put, "NAME", new { value = new string('x', 8193), isSecret = false }, HttpStatusCode.BadRequest);

        await host.Store.AddAsync(Enumerable.Range(0, EnvironmentVariable.MaxPerEnvironment)
            .Select(index => EnvironmentVariable.CreatePlain(host.Store.Environment.Id, $"V{index:D2}", "x", host.Owner, Now)).ToArray());
        await host.SendRawAsync(host.Owner, HttpMethod.Put, "ONE_TOO_MANY", new { value = "x", isSecret = false }, HttpStatusCode.Conflict);
        // Updating an existing variable is still allowed at the limit.
        await host.PutAsync(host.Owner, "V00", "y", false, HttpStatusCode.OK);
    }

    // ---------- Fixtures ----------

    private static EnvironmentSecretProtector Protector(params (string Id, byte[] Key)[] keys) =>
        new(Configuration(true, keys[0].Id, keys));

    private static IConfiguration Configuration(bool enabled, string currentKeyId = "k1", params (string Id, byte[] Key)[] keys) {
        var values = new Dictionary<string, string?> {
            ["EnvironmentSecrets:Enabled"] = enabled ? "true" : "false",
            ["EnvironmentSecrets:CurrentKeyId"] = currentKeyId,
        };
        foreach (var (id, key) in keys) values[$"EnvironmentSecrets:Keys:{id}"] = Convert.ToBase64String(key);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static byte[] Key(byte value) => Enumerable.Repeat(value, 32).ToArray();

    private sealed class Store : IAsyncDisposable {
        private readonly SqliteConnection _connection;
        public ProjectEnvironment Environment { get; private set; } = null!;
        public Team Team { get; private set; } = null!;

        private Store(SqliteConnection connection) => _connection = connection;

        public static async Task<Store> CreateAsync() {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(Token);
            var store = new Store(connection);
            await using var db = store.Context();
            await db.Database.EnsureCreatedAsync(Token);
            store.Team = Team.Create("Platform", "platform", Now);
            var project = Project.Create(store.Team.Id, "Site", Slug, Now);
            store.Environment = ProjectEnvironment.Create(project.Id, "Staging", EnvironmentTier.Staging, 1);
            db.AddRange(store.Team, project, store.Environment);
            await db.SaveChangesAsync(Token);
            return store;
        }

        public async Task AddAsync(params EnvironmentVariable[] variables) {
            await using var db = Context();
            db.EnvironmentVariables.AddRange(variables);
            await db.SaveChangesAsync(Token);
        }

        public AegisOpsDbContext Context() => new(new DbContextOptionsBuilder<AegisOpsDbContext>()
            .UseSqlite(_connection).UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCustomizer, SqliteModelCustomizer>()
            .EnableServiceProviderCaching(false).Options);

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class Host : IAsyncDisposable {
        private const string Base = "/api/v1/projects/{slug}/environments/{envId:guid}/variables";
        private readonly WebApplication _app;
        private readonly HttpClient _client;
        public Store Store { get; }
        public Guid Owner { get; private set; }
        public Guid Member { get; private set; }
        public Guid Outsider { get; private set; }
        public Guid Security { get; private set; }
        public Guid Admin { get; private set; }

        private Host(WebApplication app, Store store) {
            _app = app;
            Store = store;
            _client = app.GetTestClient();
        }

        public static async Task<Host> CreateAsync(bool secretsEnabled) {
            var store = await Store.CreateAsync();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped(_ => store.Context());
            builder.Services.AddScoped<IAuditWriter, DbAuditWriter>();
            builder.Services.AddSingleton(secretsEnabled
                ? Protector(("k1", Key(9)))
                : new EnvironmentSecretProtector(new ConfigurationBuilder().Build()));
            builder.Services.AddIdentityCore<User>().AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AegisOpsDbContext>();
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet(Base, EnvironmentVariableEndpoints.List).RequireAuthorization();
            app.MapPut(Base + "/{name}", EnvironmentVariableEndpoints.Set).RequireAuthorization();
            app.MapDelete(Base + "/{name}", EnvironmentVariableEndpoints.Remove).RequireAuthorization();
            await app.StartAsync(Token);
            var host = new Host(app, store);
            await host.SeedUsersAsync();
            return host;
        }

        private async Task SeedUsersAsync() {
            await using var scope = _app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            foreach (var role in new[] { "Admin", "Developer", "Security" })
                db.Roles.Add(new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = role, NormalizedName = role.ToUpperInvariant() });
            await db.SaveChangesAsync(Token);
            async Task<Guid> Add(string email, string role) {
                var user = User.Create(email, email, Now);
                Assert.True((await users.CreateAsync(user)).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
                return user.Id;
            }
            Owner = await Add("owner@example.test", "Developer");
            Member = await Add("member@example.test", "Developer");
            Outsider = await Add("outsider@example.test", "Developer");
            Security = await Add("security@example.test", "Security");
            Admin = await Add("admin@example.test", "Admin");
            db.TeamMembers.Add(TeamMember.Create(Store.Team.Id, Owner, TeamRole.Owner, Now));
            db.TeamMembers.Add(TeamMember.Create(Store.Team.Id, Member, TeamRole.Member, Now));
            await db.SaveChangesAsync(Token);
        }

        public async Task<string> SendRawAsync(Guid? user, HttpMethod method, string? name, object? body, HttpStatusCode expected,
            bool knownEnvironment = true) {
            var environmentId = knownEnvironment ? Store.Environment.Id : Guid.NewGuid();
            var path = $"/api/v1/projects/{Slug}/environments/{environmentId}/variables" + (name is null ? "" : "/" + name);
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (user is { } id) request.Headers.Add("X-Test-User", id.ToString());
            using var response = await _client.SendAsync(request, Token);
            var text = await response.Content.ReadAsStringAsync(Token);
            Assert.True(expected == response.StatusCode, $"{method} {path}: expected {expected}, got {response.StatusCode}: {text}");
            return text;
        }

        public async Task<JsonElement> PutAsync(Guid user, string name, string value, bool isSecret, HttpStatusCode expected) =>
            JsonDocument.Parse(await SendRawAsync(user, HttpMethod.Put, name, new { value, isSecret }, expected)).RootElement.Clone();

        public async ValueTask DisposeAsync() {
            _client.Dispose();
            await _app.DisposeAsync();
            await Store.DisposeAsync();
        }
    }

    private sealed class DbAuditWriter(AegisOpsDbContext db) : IAuditWriter {
        public void Write(AuditEvent auditEvent) => db.AuditEvents.Add(auditEvent);
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder) {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() {
            if (!Guid.TryParse(Request.Headers["X-Test-User"], out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("sub", userId.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<Policy>().Ignore(policy => policy.AppliesToTiers);
        }
    }
}
