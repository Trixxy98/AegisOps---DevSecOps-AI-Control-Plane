using System.Security.Claims;
using AegisOps.Api.Deploy;
using AegisOps.Application.Idempotency;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ProjectEnvironment = AegisOps.Domain.Organization.Environment;

namespace AegisOps.Api.Tests;

public sealed class DeploymentAuthorizationTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserWithoutTeamMembershipCannotRequestOrRollback_EvenWithStoredReplay(bool admin) {
        using var fixture = new Fixture();
        var user = fixture.AddUser(admin);
        var principal = Fixture.UserPrincipal(user);
        fixture.Replay.Stored = new StoredResponse(202, "{\"id\":\"old\"}");

        if (admin) {
            Assert.Equal(409, await fixture.Request(principal)); // legacy unbound responses cannot be replayed safely
            Assert.Equal(409, await fixture.Rollback(principal));
            Assert.Equal(1, fixture.Replay.Lookups);
        } else {
            Assert.Equal(404, await fixture.Request(principal));
            Assert.Equal(404, await fixture.Rollback(principal));
            Assert.Equal(0, fixture.Replay.Lookups);
        }
        Assert.Equal(0, fixture.Replay.Claims);
        Assert.Empty(fixture.Db.Jobs);
    }

    [Fact]
    public async Task MembershipRevocationBlocksReplaysAndDoesNotClaimNewKeys() {
        using var fixture = new Fixture();
        var user = fixture.AddUser();
        fixture.Db.TeamMembers.Add(TeamMember.Create(fixture.Team.Id, user.Id, TeamRole.Member, fixture.Now));
        fixture.Db.SaveChanges();
        var principal = Fixture.UserPrincipal(user);
        fixture.Replay.Stored = new StoredResponse(202, "{\"id\":\"old\"}");
        Assert.Equal(409, await fixture.Request(principal));
        Assert.Equal(409, await fixture.Rollback(principal));

        fixture.Db.TeamMembers.Remove(fixture.Db.TeamMembers.Single());
        fixture.Db.SaveChanges();
        Assert.Equal(404, await fixture.Request(principal));
        Assert.Equal(404, await fixture.Rollback(principal));
        Assert.Equal(1, fixture.Replay.Lookups);
        fixture.Replay.Stored = null;
        Assert.Equal(404, await fixture.Request(principal));
        Assert.Equal(404, await fixture.Rollback(principal));
        Assert.Equal(0, fixture.Replay.Claims);
        Assert.Empty(fixture.Db.Jobs);
    }

    [Fact]
    public async Task ApiKeyMustBelongToProject_AndCannotAccessProductionWhenOptedOut() {
        using var fixture = new Fixture(production: true);
        fixture.Replay.Stored = new StoredResponse(202, "{\"id\":\"old\"}");
        var otherKey = Fixture.KeyPrincipal(Guid.NewGuid());
        Assert.Equal(404, await fixture.Request(otherKey));
        Assert.Equal(404, await fixture.Rollback(otherKey));
        Assert.Equal(0, fixture.Replay.Lookups);

        var key = Fixture.KeyPrincipal(fixture.Project.Id);
        Assert.Equal(403, await fixture.Request(key));
        Assert.Equal(403, await fixture.Rollback(key)); // state is Requested: must not leak 409
        Assert.Equal(0, fixture.Replay.Lookups);
        Assert.Equal(0, fixture.Replay.Claims);
        Assert.Empty(fixture.Db.Jobs);
    }

    [Fact]
    public async Task ApiKeyProductionOptInAllowsReplay_ButOptOutBlocksIt() {
        using var fixture = new Fixture(production: true, allowKeyProduction: true);
        var key = Fixture.KeyPrincipal(fixture.Project.Id);
        fixture.Replay.Stored = new StoredResponse(202, "{\"id\":\"old\"}");
        Assert.Equal(409, await fixture.Request(key));
        Assert.Equal(409, await fixture.Rollback(key));
        fixture.Environment.Retarget(DeploymentTarget.Noop());
        fixture.Db.SaveChanges();
        Assert.Equal(403, await fixture.Request(key));
        Assert.Equal(403, await fixture.Rollback(key));
        Assert.Equal(1, fixture.Replay.Lookups);
        Assert.Empty(fixture.Db.Jobs);
    }

    [Fact]
    public async Task AuthorizedUserCanRequest_AndRollbackStateConflictOccursAfterAuthorization() {
        using var fixture = new Fixture();
        var user = fixture.AddUser();
        fixture.Db.TeamMembers.Add(TeamMember.Create(fixture.Team.Id, user.Id, TeamRole.Member, fixture.Now));
        fixture.Db.SaveChanges();
        var principal = Fixture.UserPrincipal(user);
        Assert.Equal(202, await fixture.Request(principal));
        fixture.Replay.Stored = null;
        Assert.Equal(409, await fixture.Rollback(principal));
        Assert.Single(fixture.Db.Jobs);
    }

    [Fact]
    public async Task TeamMemberCanRollbackSucceededDeploymentToDifferentPredecessor() {
        using var fixture = new Fixture(successfulRollback: true);
        var user = fixture.AddUser();
        fixture.Db.TeamMembers.Add(TeamMember.Create(fixture.Team.Id, user.Id, TeamRole.Member, fixture.Now));
        fixture.Db.SaveChanges();

        Assert.Equal(202, await fixture.Rollback(Fixture.UserPrincipal(user)));

        var rollback = Assert.Single(fixture.Db.Deployments.Where(item => item.Id != fixture.Source.Id));
        Assert.Equal(fixture.Artifact.Id, rollback.ArtifactId);
        Assert.Equal(fixture.Source.ArtifactId, rollback.PreviousArtifactId);
        Assert.Equal(fixture.Source.ProjectId, rollback.ProjectId);
        Assert.Equal(fixture.Environment.Id, rollback.EnvironmentId);
        Assert.Equal(user.Id, rollback.RequestedById);
        Assert.Null(rollback.RequestedByApiKeyId);
        Assert.Equal($"rollback of {fixture.Source.Id:D}", rollback.Reason);
        Assert.Equal(DeploymentStatus.Requested, rollback.Status);
        Assert.Single(fixture.Db.Jobs);
        Assert.Equal(1, fixture.Replay.Claims);
    }

    [Fact]
    public async Task MatchedApiKeyCanRollbackSucceededProductionDeploymentOnlyWithOptIn() {
        using var fixture = new Fixture(production: true, allowKeyProduction: true, successfulRollback: true);
        var key = Fixture.KeyPrincipal(fixture.Project.Id);

        Assert.Equal(202, await fixture.Rollback(key));
        var rollback = Assert.Single(fixture.Db.Deployments.Where(item => item.Id != fixture.Source.Id));
        Assert.Equal(fixture.Artifact.Id, rollback.ArtifactId);
        Assert.Equal(fixture.Source.ArtifactId, rollback.PreviousArtifactId);
        Assert.Null(rollback.RequestedById);
        Assert.NotNull(rollback.RequestedByApiKeyId);
        Assert.Single(fixture.Db.Jobs);
        Assert.Equal(1, fixture.Replay.Claims);

        fixture.Environment.Retarget(DeploymentTarget.Noop());
        fixture.Db.SaveChanges();
        Assert.Equal(403, await fixture.Rollback(key));
        Assert.Equal(1, fixture.Replay.Lookups);
        Assert.Equal(1, fixture.Replay.Claims);
        Assert.Single(fixture.Db.Jobs);
        Assert.Equal(2, fixture.Db.Deployments.Count());
    }

    [Fact]
    public async Task RollbackConflictDoesNotClaimKey_AndRetryCanSucceedAndReplay() {
        using var fixture = new Fixture(withPredecessor: true);
        var user = fixture.AddUser();
        fixture.Db.TeamMembers.Add(TeamMember.Create(fixture.Team.Id, user.Id, TeamRole.Member, fixture.Now));
        fixture.Db.SaveChanges();
        var principal = Fixture.UserPrincipal(user);

        Assert.Equal(409, await fixture.Rollback(principal));
        Assert.Equal(0, fixture.Replay.Claims);
        fixture.Source.BeginEvaluation();
        fixture.Source.ApplyDecision(new PolicyOutcome(PolicyDecision.Allow, Guid.NewGuid(), 0, null), fixture.Now);
        fixture.Source.Start(fixture.Now);
        fixture.Source.Succeed(fixture.Now);
        fixture.Db.SaveChanges();

        Assert.Equal(202, await fixture.Rollback(principal));
        var rollbackId = Assert.Single(fixture.Db.Deployments.Where(item => item.Id != fixture.Source.Id)).Id;
        Assert.Equal(202, await fixture.Rollback(principal));
        Assert.Equal(rollbackId, Assert.Single(fixture.Db.Deployments.Where(item => item.Id != fixture.Source.Id)).Id);
        Assert.Single(fixture.Db.Jobs);
        Assert.Equal(1, fixture.Replay.Claims);
    }

    [Fact]
    public async Task RollbackWithoutPredecessorDoesNotClaimKey() {
        using var fixture = new Fixture();
        var user = fixture.AddUser();
        fixture.Db.TeamMembers.Add(TeamMember.Create(fixture.Team.Id, user.Id, TeamRole.Member, fixture.Now));
        fixture.Source.BeginEvaluation();
        fixture.Source.ApplyDecision(new PolicyOutcome(PolicyDecision.Allow, Guid.NewGuid(), 0, null), fixture.Now);
        fixture.Source.Start(fixture.Now);
        fixture.Source.Succeed(fixture.Now);
        fixture.Db.SaveChanges();

        Assert.Equal(409, await fixture.Rollback(Fixture.UserPrincipal(user)));
        Assert.Equal(0, fixture.Replay.Claims);
        Assert.Equal(0, fixture.Replay.Lookups);
    }

    [Fact]
    public async Task RollbackWithInvalidPredecessorArtifactDoesNotClaimKey() {
        using var fixture = new Fixture();
        var user = fixture.AddUser();
        fixture.Db.TeamMembers.Add(TeamMember.Create(fixture.Team.Id, user.Id, TeamRole.Member, fixture.Now));
        var otherProject = Project.Create(fixture.Team.Id, "Other", "other", fixture.Now);
        var otherArtifact = Artifact.Create(otherProject.Id, "2.0.0", new string('c', 40), "main", "other:tag",
            CheckStatus.Passed, CheckStatus.Passed, fixture.Now, createdById: user.Id);
        var source = Deployment.Request(fixture.Project.Id, fixture.Environment.Id, fixture.Artifact.Id,
            fixture.Now, "invalid-predecessor", user.Id, null, null, otherArtifact.Id);
        source.BeginEvaluation();
        source.ApplyDecision(new PolicyOutcome(PolicyDecision.Allow, Guid.NewGuid(), 0, null), fixture.Now);
        source.Start(fixture.Now);
        source.Succeed(fixture.Now);
        fixture.Db.AddRange(otherProject, otherArtifact, source);
        fixture.Db.SaveChanges();

        Assert.Equal(404, await fixture.Rollback(Fixture.UserPrincipal(user), source.Id));
        Assert.Equal(0, fixture.Replay.Claims);
        Assert.Equal(0, fixture.Replay.Lookups);
    }

    [Fact]
    public async Task SameKeyCannotReplayOrEnqueueDifferentDeploymentOperationOrPayload() {
        using var fixture = new Fixture(successfulRollback: true);
        var user = fixture.AddUser();
        fixture.Db.TeamMembers.Add(TeamMember.Create(fixture.Team.Id, user.Id, TeamRole.Member, fixture.Now));
        fixture.Db.SaveChanges();
        var principal = Fixture.UserPrincipal(user);

        Assert.Equal(202, await fixture.Request(principal));
        Assert.Equal(202, await fixture.Request(principal));
        Assert.Equal(409, await fixture.Rollback(principal));
        Assert.Equal(409, await fixture.Request(principal, new RequestDeploymentRequest(fixture.Artifact.Id, fixture.Environment.Id, null, "different reason")));

        var secondEnvironment = ProjectEnvironment.Create(fixture.Project.Id, "Other", EnvironmentTier.Development, 0, DeploymentTarget.Noop());
        fixture.Db.Environments.Add(secondEnvironment);
        fixture.Db.SaveChanges();
        Assert.Equal(409, await fixture.Request(principal, new RequestDeploymentRequest(fixture.Artifact.Id, secondEnvironment.Id, null, null)));
        Assert.Single(fixture.Db.Jobs);
        Assert.Equal(1, fixture.Replay.Claims);
    }

    [Fact]
    public async Task RollbackKeyCannotBeReusedForAnotherSourceOrRequest() {
        using var fixture = new Fixture(successfulRollback: true);
        var user = fixture.AddUser();
        fixture.Db.TeamMembers.Add(TeamMember.Create(fixture.Team.Id, user.Id, TeamRole.Member, fixture.Now));
        fixture.Db.SaveChanges();
        var principal = Fixture.UserPrincipal(user);

        Assert.Equal(202, await fixture.Rollback(principal));
        Assert.Equal(202, await fixture.Rollback(principal));
        Assert.Equal(409, await fixture.Request(principal));
        var other = Deployment.Request(fixture.Project.Id, fixture.Environment.Id, fixture.Source.ArtifactId,
            fixture.Now, "other", user.Id, null, null, fixture.Artifact.Id);
        other.BeginEvaluation();
        other.ApplyDecision(new PolicyOutcome(PolicyDecision.Allow, Guid.NewGuid(), 0, null), fixture.Now);
        other.Start(fixture.Now);
        other.Succeed(fixture.Now);
        fixture.Db.Deployments.Add(other);
        fixture.Db.SaveChanges();
        Assert.Equal(409, await fixture.Rollback(principal, other.Id));
        Assert.Single(fixture.Db.Jobs);
        Assert.Equal(1, fixture.Replay.Claims);
    }

    private sealed class Fixture : IDisposable {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly ServiceProvider services;
        public readonly AegisOpsDbContext Db;
        public readonly UserManager<User> Users;
        public readonly ReplayStore Replay = new();
        public readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        public readonly Team Team;
        public readonly Project Project;
        public readonly ProjectEnvironment Environment;
        public readonly Artifact Artifact;
        public readonly Deployment Source;

        public Fixture(bool production = false, bool allowKeyProduction = false, bool successfulRollback = false, bool withPredecessor = false) {
            connection.Open();
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection).UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteModelCustomizer>());
            collection.AddIdentityCore<User>().AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AegisOpsDbContext>();
            services = collection.BuildServiceProvider();
            Db = services.GetRequiredService<AegisOpsDbContext>();
            Users = services.GetRequiredService<UserManager<User>>();
            Db.Database.EnsureCreated();
            Team = Team.Create("Team", "team", Now);
            Project = Project.Create(Team.Id, "Project", "project", Now);
            Environment = ProjectEnvironment.Create(Project.Id, "Target", production ? EnvironmentTier.Production : EnvironmentTier.Staging, 0, DeploymentTarget.Noop(allowKeyProduction));
            var creator = AddUser();
            Artifact = Artifact.Create(Project.Id, "1.2.3", new string('a', 40), "main", "image:tag", CheckStatus.Passed, CheckStatus.Passed, Now, createdById: creator.Id);
            var deployedArtifact = successfulRollback || withPredecessor
                ? Artifact.Create(Project.Id, "1.2.4", new string('b', 40), "main", "image:next", CheckStatus.Passed, CheckStatus.Passed, Now, createdById: creator.Id)
                : Artifact;
            Source = Deployment.Request(Project.Id, Environment.Id, deployedArtifact.Id, Now, "source", creator.Id, null, null, Artifact.Id);
            if (successfulRollback) {
                Source.BeginEvaluation();
                Source.ApplyDecision(new PolicyOutcome(PolicyDecision.Allow, Guid.NewGuid(), 0, null), Now);
                Source.Start(Now);
                Source.Succeed(Now);
                Environment.RecordDeployment(deployedArtifact.Id, Source.Id);
            }
            Db.AddRange(Team, Project, Environment, Artifact, Source);
            if (successfulRollback || withPredecessor) Db.Artifacts.Add(deployedArtifact);
            Db.SaveChanges();
        }

        public User AddUser(bool admin = false) {
            var user = User.Create($"{Guid.NewGuid():N}@example.test", "Test", Now);
            Db.Users.Add(user);
            if (admin) {
                var role = new IdentityRole<Guid>("Admin") { Id = Guid.NewGuid(), NormalizedName = "ADMIN" };
                Db.Roles.Add(role);
                Db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });
            }
            Db.SaveChanges();
            return user;
        }

        public static ClaimsPrincipal UserPrincipal(User user) => new(new ClaimsIdentity([new Claim("sub", user.Id.ToString())], "test"));
        public static ClaimsPrincipal KeyPrincipal(Guid projectId) => new(new ClaimsIdentity([
            new Claim("actor_type", "apiKey"),
            new Claim("sub", $"apikey:{Guid.NewGuid()}"),
            new Claim("project", projectId.ToString()),
        ], "test"));

        private DefaultHttpContext Context() {
            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.Headers["Idempotency-Key"] = "retry";
            return http;
        }

        public async Task<int> Request(ClaimsPrincipal principal, RequestDeploymentRequest? request = null) {
            var result = await DeploymentEndpoints.Request(request ?? new RequestDeploymentRequest(Artifact.Id, Environment.Id, null, null), principal, Users, Db, TimeProvider.System, Replay, Context());
            return ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        }

        public async Task<int> Rollback(ClaimsPrincipal principal, Guid? id = null) {
            var result = await DeploymentEndpoints.Rollback(id ?? Source.Id, principal, Users, Db, TimeProvider.System, Replay, Context());
            return ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        }

        public void Dispose() {
            services.Dispose();
            connection.Dispose();
        }
    }

    // The production model stores API-key scopes as a PostgreSQL text array.
    private sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<Policy>().Ignore(policy => policy.AppliesToTiers);
        }
    }

    private sealed class ReplayStore : IIdempotencyStore {
        public StoredResponse? Stored;
        private readonly Dictionary<(string Principal, string Key), StoredResponse> responses = new();
        private readonly Dictionary<(string Principal, string Key), string> pending = new();
        public int Lookups;
        public int Claims;
        public Task<StoredResponse?> FindAsync(string principal, string key, CancellationToken cancellationToken) {
            Lookups++;
            return Task.FromResult(Stored ?? (responses.TryGetValue((principal, key), out var response) ? response : null));
        }
        public Task<string?> TryBeginAsync(string principal, string key, CancellationToken cancellationToken) {
            Claims++;
            var owner = Guid.NewGuid().ToString("N");
            if (pending.ContainsKey((principal, key)) || responses.ContainsKey((principal, key))) return Task.FromResult<string?>(null);
            pending.Add((principal, key), owner);
            return Task.FromResult<string?>(owner);
        }
        public Task<bool> SaveAsync(string principal, string key, string owner, StoredResponse response, CancellationToken cancellationToken) {
            if (!pending.TryGetValue((principal, key), out var current) || current != owner) return Task.FromResult(false);
            responses[(principal, key)] = response;
            pending.Remove((principal, key));
            return Task.FromResult(true);
        }
    }
}
