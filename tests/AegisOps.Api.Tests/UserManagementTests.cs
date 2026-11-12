using System.Security.Claims;
using AegisOps.Api.Hosting;
using AegisOps.Api.Identity;
using AegisOps.Application.Audit;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Tests;

/// <summary>
/// docs/09 §3 user lifecycle: <c>POST /admin/users</c>, <c>PATCH /admin/users/{id}</c> and
/// <c>PUT /admin/users/{id}/roles</c> against SQLite with the production Identity registration. Route permissions are
/// covered by <see cref="AuthorizationMatrixTests"/>, audit rows by <see cref="AuditCoverageTests"/>.
/// </summary>
public sealed class UserManagementTests : IAsyncLifetime {
    private const string Password = "Correct-Horse-Battery-9!";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly JwtOptions Jwt = new() {
        Issuer = "aegisops-user-management-tests",
        Audience = "aegisops-user-management-tests",
        SigningKey = "user-management-tests-signing-key-0123456789",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 7,
    };

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
        Assert.Equal(0, await AdminCommands.MigrateAsync(_services, TextWriter.Null,
            static (db, token) => db.Database.EnsureCreatedAsync(token), Token));
    }

    public async ValueTask DisposeAsync() {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---------- Create ----------

    [Fact]
    public async Task CreatedUserGetsRolesTeamMembershipAndATemporaryPasswordThatMustBeChangedAtFirstSignIn() {
        var admin = await SeedUserAsync("admin@example.test", IdentityRoles.Admin);
        var team = await SeedTeamAsync("platform");

        var (result, http) = await CallAsync((services, context) => CreateUser.Handle(
            new CreateUserRequest(" Aina@Example.test ", " Aina ", ["developer", "Approver", "Developer"], new CreateUserTeamRequest(null, "Platform", "owner")),
            Principal(admin), Users(services), Db(services), Audit(services), TimeProvider.System, context));

        var created = Assert.IsType<Created<CreateUserResponse>>(result);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        var body = created.Value!;
        Assert.Equal($"/api/v1/admin/users/{body.Id}", created.Location);
        Assert.Equal(("Aina@Example.test", "Aina", true), (body.Email, body.DisplayName, body.MustChangePassword));
        Assert.Equal(["Developer", "Approver"], body.Roles);
        Assert.Equal(new CreateUserTeamMembership(team, "platform", "Owner"), body.Team);
        Assert.Equal(24, body.TemporaryPassword.Length);

        await using (var scope = _services.CreateAsyncScope()) {
            var users = Users(scope.ServiceProvider);
            var user = (await users.FindByIdAsync(body.Id.ToString()))!;
            Assert.True(user.IsActive && user.MustChangePassword && user.LockoutEnabled);
            Assert.Equal(["Approver", "Developer"], (await users.GetRolesAsync(user)).Order(StringComparer.Ordinal));
            Assert.True(await users.CheckPasswordAsync(user, body.TemporaryPassword));
            var membership = await Db(scope.ServiceProvider).TeamMembers.SingleAsync(item => item.UserId == user.Id, Token);
            Assert.Equal((team, TeamRole.Owner), (membership.TeamId, membership.Role));
        }

        var audit = Assert.Single(await AuditRowsAsync());
        Assert.Equal(("user.created", "User", (Guid?)admin.Id, "admin@example.test", "User", (Guid?)body.Id, "Succeeded"),
            (audit.Action, audit.ActorType, audit.ActorId, audit.ActorDisplay, audit.ResourceType, audit.ResourceId, audit.Outcome));
        Assert.DoesNotContain(body.TemporaryPassword, string.Join("|", audit.ActorDisplay, audit.Action, audit.ResourceType,
            audit.Outcome, audit.CorrelationId), StringComparison.Ordinal);

        // First sign-in: the access token carries pwd_change, so PasswordChangeGate allows only the account endpoints.
        var first = await LoginAsync(body.Email, body.TemporaryPassword);
        Assert.Equal("true", new JsonWebTokenHandler().ReadJsonWebToken(first).GetClaim(JwtAccessTokenFactory.PasswordChangeClaim).Value);
        Assert.False(PasswordChangeGate.IsAllowed("/api/v1/projects"));

        var changed = await CallAsync((services, _) => ChangePassword.Handle(
            new ChangePasswordRequest(body.TemporaryPassword, "Another-Strong-Passw0rd!"),
            Principal(body.Id), Users(services), Db(services), Audit(services), TimeProvider.System));
        Assert.Equal(StatusCodes.Status204NoContent, Status(changed.Result));
        var settled = await LoginAsync(body.Email, "Another-Strong-Passw0rd!");
        Assert.False(new JsonWebTokenHandler().ReadJsonWebToken(settled).TryGetClaim(JwtAccessTokenFactory.PasswordChangeClaim, out _));
    }

    [Fact]
    public async Task CreateAddsAMemberByTeamIdAndWorksWithoutATeam() {
        var admin = await SeedUserAsync("admin@example.test", IdentityRoles.Admin);
        var team = await SeedTeamAsync("circuit");

        var member = await CreateAsync(admin, new CreateUserRequest("m@example.test", "Member", ["Security"], new CreateUserTeamRequest(team, null, "Member")));
        var solo = await CreateAsync(admin, new CreateUserRequest("s@example.test", "Solo", ["Approver"], null));

        Assert.Equal(new CreateUserTeamMembership(team, "circuit", "Member"), member.Team);
        Assert.Null(solo.Team);
        await WithDbAsync(async db => {
            Assert.Equal(TeamRole.Member, (await db.TeamMembers.SingleAsync(item => item.UserId == member.Id, Token)).Role);
            Assert.False(await db.TeamMembers.AnyAsync(item => item.UserId == solo.Id, Token));
        });
    }

    public static TheoryData<string?, string?, string[]?, string?, string?, int> InvalidCreateRequests() => new() {
        { "not-an-email", "Name", ["Developer"], null, null, 400 },
        { "", "Name", ["Developer"], null, null, 400 },
        { "a@example.test", "  ", ["Developer"], null, null, 400 },
        { "a@example.test", new string('x', 101), ["Developer"], null, null, 400 },
        { "a@example.test", "Name", ["Auditor"], null, null, 400 },
        { "a@example.test", "Name", [], null, null, 400 },
        { "a@example.test", "Name", null, null, null, 400 },
        { "a@example.test", "Name", ["Developer"], "platform", "Lead", 400 },
        { "a@example.test", "Name", ["Developer"], "no-such-team", "Member", 400 },
        { "ADMIN@example.test", "Name", ["Developer"], null, null, 409 },
    };

    [Theory]
    [MemberData(nameof(InvalidCreateRequests))]
    public async Task CreateRejectsInvalidInputAndDuplicateEmailsWithoutSideEffects(string? email, string? displayName,
        string[]? roles, string? teamSlug, string? teamRole, int status) {
        var admin = await SeedUserAsync("admin@example.test", IdentityRoles.Admin);
        await SeedTeamAsync("platform");
        var team = teamSlug is null ? null : new CreateUserTeamRequest(null, teamSlug, teamRole);

        var (result, http) = await CallAsync((services, context) => CreateUser.Handle(new CreateUserRequest(email, displayName, roles, team),
            Principal(admin), Users(services), Db(services), Audit(services), TimeProvider.System, context));

        Assert.Equal(status, Status(result));
        Assert.IsType<ProblemHttpResult>(result);
        Assert.NotEqual("no-store", http.Response.Headers.CacheControl.ToString());
        Assert.Equal(1, await WithDbAsync(db => db.Users.CountAsync(Token)));
        Assert.Empty(await AuditRowsAsync());
    }

    // ---------- Update / deactivate ----------

    [Fact]
    public async Task DeactivationRevokesRefreshTokensAndBlocksSignInUntilReactivated() {
        var admin = await SeedUserAsync("admin@example.test", IdentityRoles.Admin);
        var dev = await SeedUserAsync("dev@example.test", IdentityRoles.Developer);
        await LoginAsync(dev.Email!, Password);
        await LoginAsync(dev.Email!, Password);
        Assert.Equal(2, await ActiveRefreshTokensAsync(dev.Id));

        var deactivated = await PatchAsync(admin, dev.Id, new UpdateUserRequest(null, false));
        Assert.False(Assert.IsType<Ok<UserListItem>>(deactivated).Value!.IsActive);
        Assert.Equal(0, await ActiveRefreshTokensAsync(dev.Id));
        Assert.Equal(["user.deactivated"], (await AuditRowsAsync()).Select(row => row.Action));
        Assert.Equal(StatusCodes.Status401Unauthorized, await LoginStatusAsync(dev.Email!, Password));

        var activated = await PatchAsync(admin, dev.Id, new UpdateUserRequest("Dev Renamed", true));
        var item = Assert.IsType<Ok<UserListItem>>(activated).Value!;
        Assert.True(item.IsActive);
        Assert.Equal("Dev Renamed", item.DisplayName);
        Assert.Equal(["user.activated", "user.deactivated", "user.updated"],
            (await AuditRowsAsync()).Select(row => row.Action).Order(StringComparer.Ordinal));
        Assert.Equal(StatusCodes.Status200OK, await LoginStatusAsync(dev.Email!, Password));

        // Nothing changes: no audit row.
        var before = (await AuditRowsAsync()).Count;
        Assert.Equal(StatusCodes.Status200OK, Status(await PatchAsync(admin, dev.Id, new UpdateUserRequest("Dev Renamed", true))));
        Assert.Equal(before, (await AuditRowsAsync()).Count);
    }

    [Fact]
    public async Task PatchValidatesInputAndUnknownUsers() {
        var admin = await SeedUserAsync("admin@example.test", IdentityRoles.Admin);
        var dev = await SeedUserAsync("dev@example.test", IdentityRoles.Developer);

        Assert.Equal(400, Status(await PatchAsync(admin, dev.Id, new UpdateUserRequest(null, null))));
        Assert.Equal(400, Status(await PatchAsync(admin, dev.Id, new UpdateUserRequest(" ", null))));
        Assert.Equal(400, Status(await PatchAsync(admin, dev.Id, new UpdateUserRequest(new string('x', 101), null))));
        Assert.Equal(404, Status(await PatchAsync(admin, Guid.NewGuid(), new UpdateUserRequest(null, false))));
        Assert.Empty(await AuditRowsAsync());
    }

    [Fact]
    public async Task AnAdminCannotDeactivateThemselfAndTheLastActiveAdminCannotBeDeactivated() {
        var admin = await SeedUserAsync("admin@example.test", IdentityRoles.Admin);
        var other = await SeedUserAsync("other-admin@example.test", IdentityRoles.Admin);
        await LoginAsync(admin.Email!, Password);

        var self = await PatchAsync(admin, admin.Id, new UpdateUserRequest(null, false));
        Assert.Equal(409, Status(self));
        Assert.Equal(1, await ActiveRefreshTokensAsync(admin.Id));

        // With two active Admins, one may deactivate the other.
        Assert.Equal(200, Status(await PatchAsync(admin, other.Id, new UpdateUserRequest(null, false))));
        Assert.Equal(200, Status(await PatchAsync(admin, other.Id, new UpdateUserRequest(null, true))));

        // A caller whose Admin role was removed after their token was issued must not deactivate the last active Admin.
        await using (var scope = _services.CreateAsyncScope()) {
            var users = Users(scope.ServiceProvider);
            Assert.True((await users.RemoveFromRoleAsync((await users.FindByIdAsync(admin.Id.ToString()))!, IdentityRoles.Admin)).Succeeded);
        }

        var last = await PatchAsync(admin, other.Id, new UpdateUserRequest(null, false));
        Assert.Equal(409, Status(last));
        Assert.Contains("last active Admin", Assert.IsType<ProblemHttpResult>(last).ProblemDetails.Title, StringComparison.Ordinal);
        Assert.True(await WithDbAsync(db => db.Users.Where(user => user.Id == other.Id).Select(user => user.IsActive).SingleAsync(Token)));
    }

    // ---------- Roles ----------

    [Fact]
    public async Task RoleChangesReplaceTheSetRevokeRefreshTokensAndAreAudited() {
        var admin = await SeedUserAsync("admin@example.test", IdentityRoles.Admin);
        var dev = await SeedUserAsync("dev@example.test", IdentityRoles.Developer);
        await LoginAsync(dev.Email!, Password);

        var changed = await PutRolesAsync(admin, dev.Id, ["security", "Approver"]);
        Assert.Equal(["Approver", "Security"], Assert.IsType<Ok<UserListItem>>(changed).Value!.Roles.Order(StringComparer.Ordinal));
        Assert.Equal(0, await ActiveRefreshTokensAsync(dev.Id));
        var audit = Assert.Single(await AuditRowsAsync());
        Assert.Equal(("user.roles_changed", (Guid?)admin.Id, (Guid?)dev.Id), (audit.Action, audit.ActorId, audit.ResourceId));

        // The same set again: no change, no revocation, no audit.
        await LoginAsync(dev.Email!, Password);
        Assert.Equal(200, Status(await PutRolesAsync(admin, dev.Id, ["Approver", "Security"])));
        Assert.Equal(1, await ActiveRefreshTokensAsync(dev.Id));
        Assert.Single(await AuditRowsAsync());

        Assert.Equal(400, Status(await PutRolesAsync(admin, dev.Id, ["Auditor"])));
        Assert.Equal(400, Status(await PutRolesAsync(admin, dev.Id, [])));
        Assert.Equal(404, Status(await PutRolesAsync(admin, Guid.NewGuid(), ["Developer"])));
        Assert.Single(await AuditRowsAsync());
    }

    [Fact]
    public async Task AnAdminCannotRemoveTheirOwnAdminRoleAndTheLastActiveAdminKeepsIt() {
        var admin = await SeedUserAsync("admin@example.test", IdentityRoles.Admin);
        var other = await SeedUserAsync("other-admin@example.test", IdentityRoles.Admin);

        Assert.Equal(409, Status(await PutRolesAsync(admin, admin.Id, ["Developer"])));
        // Adding a role to oneself while keeping Admin is fine.
        Assert.Equal(200, Status(await PutRolesAsync(admin, admin.Id, ["Admin", "Security"])));

        // A deactivated Admin does not count: once the other Admin is inactive, "admin" is the last active Admin.
        Assert.Equal(200, Status(await PatchAsync(admin, other.Id, new UpdateUserRequest(null, false))));
        await using (var scope = _services.CreateAsyncScope()) {
            var users = Users(scope.ServiceProvider);
            Assert.True((await users.RemoveFromRoleAsync((await users.FindByIdAsync(admin.Id.ToString()))!, IdentityRoles.Admin)).Succeeded);
        }

        // Stale caller (Admin in the token only) vs. the inactive Admin: removing Admin from an inactive user is allowed.
        Assert.Equal(200, Status(await PutRolesAsync(admin, other.Id, ["Developer"])));

        var third = await SeedUserAsync("third-admin@example.test", IdentityRoles.Admin);
        var last = await PutRolesAsync(admin, third.Id, ["Developer"]);
        Assert.Equal(409, Status(last));
        Assert.Contains("last active Admin", Assert.IsType<ProblemHttpResult>(last).ProblemDetails.Title, StringComparison.Ordinal);
        await using (var scope = _services.CreateAsyncScope()) {
            var users = Users(scope.ServiceProvider);
            Assert.Contains(IdentityRoles.Admin, await users.GetRolesAsync((await users.FindByIdAsync(third.Id.ToString()))!));
        }
    }

    // ---------- Helpers ----------

    private static AegisOpsDbContext Db(IServiceProvider services) => services.GetRequiredService<AegisOpsDbContext>();
    private static UserManager<User> Users(IServiceProvider services) => services.GetRequiredService<UserManager<User>>();
    private static IAuditWriter Audit(IServiceProvider services) => services.GetRequiredService<IAuditWriter>();

    /// <summary>The handlers only read <c>sub</c>; route permissions are enforced (and tested) by the authorization middleware.</summary>
    private static ClaimsPrincipal Principal(User user) => Principal(user.Id);

    private static ClaimsPrincipal Principal(Guid id) =>
        new(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, id.ToString())], "test"));

    private static int Status(IResult result) => result switch {
        IStatusCodeHttpResult withStatus => withStatus.StatusCode ?? StatusCodes.Status200OK,
        _ => StatusCodes.Status200OK,
    };

    private async Task<(IResult Result, HttpContext Http)> CallAsync(Func<IServiceProvider, HttpContext, Task<IResult>> call) {
        await using var scope = _services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = Token };
        return (await call(scope.ServiceProvider, http), http);
    }

    private async Task<CreateUserResponse> CreateAsync(User admin, CreateUserRequest request) {
        var (result, _) = await CallAsync((services, http) => CreateUser.Handle(request, Principal(admin), Users(services), Db(services),
            Audit(services), TimeProvider.System, http));
        return Assert.IsType<Created<CreateUserResponse>>(result).Value!;
    }

    private async Task<IResult> PatchAsync(User actor, Guid id, UpdateUserRequest request) =>
        (await CallAsync((services, http) => UpdateUser.Handle(id, request, Principal(actor), Users(services), Db(services),
            Audit(services), TimeProvider.System, http))).Result;

    private async Task<IResult> PutRolesAsync(User actor, Guid id, string[] roles) =>
        (await CallAsync((services, http) => SetUserRoles.Handle(id, new SetUserRolesRequest(roles), Principal(actor), Users(services),
            Db(services), Audit(services), TimeProvider.System, http))).Result;

    private async Task<int> LoginStatusAsync(string email, string password) =>
        Status((await CallAsync((services, http) => Login.Handle(new LoginRequest(email, password), Users(services), Options.Create(Jwt),
            TimeProvider.System, Db(services), Audit(services), http))).Result);

    private async Task<string> LoginAsync(string email, string password) {
        var (result, _) = await CallAsync((services, http) => Login.Handle(new LoginRequest(email, password), Users(services),
            Options.Create(Jwt), TimeProvider.System, Db(services), Audit(services), http));
        return Assert.IsType<Ok<LoginResponse>>(result).Value!.AccessToken;
    }

    private async Task<User> SeedUserAsync(string email, string role) {
        await using var scope = _services.CreateAsyncScope();
        var users = Users(scope.ServiceProvider);
        var user = User.Create(email, email.Split('@')[0], DateTimeOffset.UtcNow);
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        user.MarkPasswordChanged();
        Assert.True((await users.UpdateAsync(user)).Succeeded);
        return user;
    }

    private Task<Guid> SeedTeamAsync(string slug) => WithDbAsync(async db => {
        var team = Team.Create(slug, slug, DateTimeOffset.UtcNow);
        db.Teams.Add(team);
        await db.SaveChangesAsync(Token);
        return team.Id;
    });

    private Task<int> ActiveRefreshTokensAsync(Guid userId) =>
        WithDbAsync(db => db.RefreshTokens.CountAsync(token => token.UserId == userId && token.RevokedAt == null, Token));

    /// <summary>Audit rows other than sign-in bookkeeping (auth.*).</summary>
    private async Task<IReadOnlyList<AuditEvent>> AuditRowsAsync() =>
        (await WithDbAsync(db => db.AuditEvents.AsNoTracking().ToListAsync(Token)))
        .Where(row => !row.Action.StartsWith("auth.", StringComparison.Ordinal)).ToList();

    private async Task<T> WithDbAsync<T>(Func<AegisOpsDbContext, Task<T>> action) {
        await using var scope = _services.CreateAsyncScope();
        return await action(Db(scope.ServiceProvider));
    }

    private async Task WithDbAsync(Func<AegisOpsDbContext, Task> action) {
        await using var scope = _services.CreateAsyncScope();
        await action(Db(scope.ServiceProvider));
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
