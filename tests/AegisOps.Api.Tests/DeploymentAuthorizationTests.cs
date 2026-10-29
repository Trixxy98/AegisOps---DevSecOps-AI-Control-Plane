using System.Security.Claims;
using AegisOps.Api.Deploy;
using AegisOps.Application.Idempotency;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Jobs;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.SignalR;
using ProjectEnvironment = AegisOps.Domain.Organization.Environment;

namespace AegisOps.Api.Tests;

public sealed class DeploymentAuthorizationTests {
    [Fact]
    public async Task LiveDeliveryRechecksMembershipRolesAndActiveUserBeforeSending() {
        using var fixture = new Fixture();
        var member = fixture.AddUser("Developer");
        var outsider = fixture.AddUser("Developer");
        var security = fixture.AddUser("Security");
        fixture.Join(member);
        var connections = new DeploymentConnections();
        connections.Add("member", Fixture.UserPrincipal(member));
        connections.Add("outsider", Fixture.UserPrincipal(outsider));
        connections.Add("security", Fixture.UserPrincipal(security));
        connections.Add("key", Fixture.KeyPrincipal(fixture.Project.Id, "deployments:read"));
        var hub = new RecordingHub();
        var listener = new DeploymentUpdateListener(null!, fixture.Services.GetRequiredService<IServiceScopeFactory>(), hub,
            connections, NullLogger<DeploymentUpdateListener>.Instance);

        await listener.DispatchAsync(fixture.Source.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, hub.Sent.Count);
        Assert.Contains("member", hub.Sent);
        Assert.Contains("security", hub.Sent);

        hub.Sent.Clear();
        fixture.Db.TeamMembers.Remove(fixture.Db.TeamMembers.Single());
        fixture.Db.SaveChanges();
        await listener.DispatchAsync(fixture.Source.Id, TestContext.Current.CancellationToken);
        Assert.Equal("security", Assert.Single(hub.Sent));

        hub.Sent.Clear();
        security.Deactived();
        fixture.Db.SaveChanges();
        await listener.DispatchAsync(fixture.Source.Id, TestContext.Current.CancellationToken);
        Assert.Empty(hub.Sent);
        await listener.DispatchAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Empty(hub.Sent);
    }

    [Fact]
    public async Task LiveDeliveryDoesNotLeakAcrossProjectsOrSessions() {
        using var fixture = new Fixture();
        var member = fixture.AddUser("Developer");
        var otherMember = fixture.AddUser("Developer");
        fixture.Join(member);
        var otherTeam = Team.Create("Other", "other", fixture.Now);
        var otherProject = Project.Create(otherTeam.Id, "Other", "other", fixture.Now);
        var otherEnvironment = ProjectEnvironment.Create(otherProject.Id, "Other", EnvironmentTier.Development, 0, DeploymentTarget.Noop());
        var otherArtifact = Artifact.Create(otherProject.Id, "1.0.0", new string('b', 40), "main", "image:other",
            CheckStatus.Passed, CheckStatus.Passed, fixture.Now, createdById: otherMember.Id);
        var otherDeployment = Deployment.Request(otherProject.Id, otherEnvironment.Id, otherArtifact.Id,
            fixture.Now, "other", otherMember.Id, null, null, null);
        fixture.Db.AddRange(otherTeam, otherProject, otherEnvironment, otherArtifact, otherDeployment,
            TeamMember.Create(otherTeam.Id, otherMember.Id, TeamRole.Member, fixture.Now));
        fixture.Db.SaveChanges();

        var connections = new DeploymentConnections();
        connections.Add("member-tab-1", Fixture.UserPrincipal(member));
        connections.Add("member-tab-2", Fixture.UserPrincipal(member));
        connections.Add("other-tab", Fixture.UserPrincipal(otherMember));
        var hub = new RecordingHub();
        var listener = new DeploymentUpdateListener(null!, fixture.Services.GetRequiredService<IServiceScopeFactory>(), hub,
            connections, NullLogger<DeploymentUpdateListener>.Instance);

        await listener.DispatchAsync(fixture.Source.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, hub.Sent.Count);
        Assert.Contains("member-tab-1", hub.Sent);
        Assert.Contains("member-tab-2", hub.Sent);
        hub.Sent.Clear();
        await listener.DispatchAsync(otherDeployment.Id, TestContext.Current.CancellationToken);
        Assert.Equal("other-tab", Assert.Single(hub.Sent));
    }

    [Fact]
    public async Task ApprovalHintIsSentToEveryEligibleSession() {
        using var fixture = new Fixture();
        var reviewer = fixture.AddUser("Security");
        fixture.Join(reviewer);
        fixture.Source.BeginEvaluation();
        var evaluation = fixture.AddEvaluation("Security");
        fixture.Source.ApplyDecision(new PolicyOutcome(PolicyDecision.RequireApproval, evaluation.Id, 2, null), fixture.Now);
        fixture.Db.SaveChanges();
        var connections = new DeploymentConnections();
        connections.Add("reviewer-tab-1", Fixture.UserPrincipal(reviewer));
        connections.Add("reviewer-tab-2", Fixture.UserPrincipal(reviewer));
        var hub = new RecordingHub();
        var listener = new DeploymentUpdateListener(null!, fixture.Services.GetRequiredService<IServiceScopeFactory>(), hub,
            connections, NullLogger<DeploymentUpdateListener>.Instance);

        await listener.DispatchAsync(fixture.Source.Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, hub.ApprovalHints.Count);
        Assert.Contains("reviewer-tab-1", hub.ApprovalHints);
        Assert.Contains("reviewer-tab-2", hub.ApprovalHints);
    }

    [Fact]
    public async Task ApprovalHintsAndQueueRequireCurrentProjectRoleAndUnvotedNonrequester() {
        using var fixture = new Fixture();
        var reviewer = fixture.AddUser("Security");
        var developer = fixture.AddUser("Developer");
        var outsider = fixture.AddUser("Approver");
        fixture.Join(reviewer);
        fixture.Join(developer);
        fixture.Join(fixture.Creator);
        fixture.Source.BeginEvaluation();
        var evaluation = fixture.AddEvaluation("Security");
        fixture.Source.ApplyDecision(new PolicyOutcome(PolicyDecision.RequireApproval, evaluation.Id, 2, null), fixture.Now);
        fixture.Db.SaveChanges();
        var connections = new DeploymentConnections();
        connections.Add("reviewer", Fixture.UserPrincipal(reviewer));
        connections.Add("developer", Fixture.UserPrincipal(developer));
        connections.Add("outsider", Fixture.UserPrincipal(outsider));
        connections.Add("requester", Fixture.UserPrincipal(fixture.Creator));
        connections.Add("key", Fixture.KeyPrincipal(fixture.Project.Id, "deployments:approve"));
        var hub = new RecordingHub();
        var listener = new DeploymentUpdateListener(null!, fixture.Services.GetRequiredService<IServiceScopeFactory>(), hub,
            connections, NullLogger<DeploymentUpdateListener>.Instance);

        Assert.Equal(fixture.Source.Id, Assert.Single(await fixture.ApprovalQueue(Fixture.UserPrincipal(reviewer))));
        Assert.Empty(await fixture.ApprovalQueue(Fixture.UserPrincipal(developer)));
        Assert.Empty(await fixture.ApprovalQueue(Fixture.UserPrincipal(outsider)));
        Assert.Empty(await fixture.ApprovalQueue(Fixture.UserPrincipal(fixture.Creator)));
        Assert.Equal(403, ((IStatusCodeHttpResult)await fixture.ApprovalQueueResult(Fixture.KeyPrincipal(fixture.Project.Id, "deployments:approve"))).StatusCode);
        await listener.DispatchAsync(fixture.Source.Id, TestContext.Current.CancellationToken);
        Assert.Equal("reviewer", Assert.Single(hub.ApprovalHints));
        Assert.DoesNotContain("key", hub.Sent);
        hub.ApprovalHints.Clear();

        fixture.Db.TeamMembers.Remove(fixture.Db.TeamMembers.Single(item => item.UserId == reviewer.Id));
        fixture.Db.SaveChanges();
        Assert.Empty(await fixture.ApprovalQueue(Fixture.UserPrincipal(reviewer)));
        await listener.DispatchAsync(fixture.Source.Id, TestContext.Current.CancellationToken);
        Assert.Empty(hub.ApprovalHints);

        var otherTeam = Team.Create("Other", "other", fixture.Now);
        var otherProject = Project.Create(otherTeam.Id, "Other", "other", fixture.Now);
        var otherEnvironment = ProjectEnvironment.Create(otherProject.Id, "Other", EnvironmentTier.Development, 0, DeploymentTarget.Noop());
        var otherArtifact = Artifact.Create(otherProject.Id, "2.0.0", new string('c', 40), "main", "image:other",
            CheckStatus.Passed, CheckStatus.Passed, fixture.Now, createdById: fixture.Creator.Id);
        var otherDeployment = Deployment.Request(otherProject.Id, otherEnvironment.Id, otherArtifact.Id,
            fixture.Now, "other", fixture.Creator.Id, null, null, null);
        otherDeployment.BeginEvaluation();
        var otherEvaluation = PolicyEvaluation.Create(otherDeployment.Id, PolicyDecision.RequireApproval, 1, fixture.Now,
            """[{"Type":"RequireApprovals","RequiredRoles":["Security"]}]""");
        otherDeployment.ApplyDecision(new PolicyOutcome(PolicyDecision.RequireApproval, otherEvaluation.Id, 1, null), fixture.Now);
        fixture.Db.AddRange(otherTeam, otherProject, otherEnvironment, otherArtifact, otherDeployment, otherEvaluation);
        fixture.Db.SaveChanges();
        Assert.Empty(await fixture.ApprovalQueue(Fixture.UserPrincipal(reviewer)));
        await listener.DispatchAsync(otherDeployment.Id, TestContext.Current.CancellationToken);
        Assert.Empty(hub.ApprovalHints);

        fixture.Join(reviewer);
        Assert.Equal(204, await fixture.Decide(Fixture.UserPrincipal(reviewer), "Approved"));
        Assert.Empty(await fixture.ApprovalQueue(Fixture.UserPrincipal(reviewer)));
        await listener.DispatchAsync(fixture.Source.Id, TestContext.Current.CancellationToken);
        Assert.Empty(hub.ApprovalHints);
        reviewer.Deactived();
        fixture.Db.SaveChanges();
        Assert.Equal(401, ((IStatusCodeHttpResult)await fixture.ApprovalQueueResult(Fixture.UserPrincipal(reviewer))).StatusCode);
        await listener.DispatchAsync(fixture.Source.Id, TestContext.Current.CancellationToken);
        Assert.Empty(hub.ApprovalHints);
    }

    private sealed class RecordingHub : IHubContext<OpsHub>, IHubClients {
        public List<string> Sent { get; } = [];
        public List<string> ApprovalHints { get; } = [];
        public IHubClients Clients => this;
        public IGroupManager Groups => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => new RecordingClient(connectionId, Sent, ApprovalHints);
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy Group(string groupName) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class RecordingClient(string id, List<string> sent, List<string> approvalHints) : IClientProxy {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) {
            Assert.Single(args);
            if (method == "ApprovalQueueChanged") approvalHints.Add(id);
            else { Assert.Equal("DeploymentChanged", method); sent.Add(id); }
            return Task.CompletedTask;
        }
    }
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
        Assert.Single(fixture.Db.Jobs.Where(job => job.Type == JobType.EvaluateDeployment));
        Assert.Single(fixture.Db.Jobs.Where(job => job.Type == JobType.SendNotification));
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
        Assert.Single(fixture.Db.Jobs.Where(job => job.Type == JobType.EvaluateDeployment));
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
        Assert.Single(fixture.Db.Jobs.Where(job => job.Type == JobType.EvaluateDeployment));
        Assert.Equal(1, fixture.Replay.Claims);

        fixture.Environment.Retarget(DeploymentTarget.Noop());
        fixture.Db.SaveChanges();
        Assert.Equal(403, await fixture.Rollback(key));
        Assert.Equal(1, fixture.Replay.Lookups);
        Assert.Equal(1, fixture.Replay.Claims);
        Assert.Single(fixture.Db.Jobs.Where(job => job.Type == JobType.EvaluateDeployment));
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
        Assert.Single(fixture.Db.Jobs.Where(job => job.Type == JobType.EvaluateDeployment));
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
        Assert.Single(fixture.Db.Jobs.Where(job => job.Type == JobType.EvaluateDeployment));
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
        Assert.Single(fixture.Db.Jobs.Where(job => job.Type == JobType.EvaluateDeployment));
        Assert.Equal(1, fixture.Replay.Claims);
    }

    [Fact]
    public async Task ReadsAreScopedToMembershipSecurityAndKeyProject() {
        using var fixture = new Fixture();
        var outsider = fixture.AddUser("Developer");
        var member = fixture.AddUser("Developer");
        var security = fixture.AddUser("Security");
        fixture.Join(member);
        var foreign = Fixture.UserPrincipal(outsider);
        var own = Fixture.UserPrincipal(member);
        var reviewer = Fixture.UserPrincipal(security);

        Assert.Empty(await fixture.List(foreign));
        Assert.Empty(await fixture.List(foreign, fixture.Project.Id));
        foreach (var action in new[] { "get", "events", "evaluation" }) {
            Assert.Equal(404, await fixture.Read(action, foreign));
            Assert.Equal(200, await fixture.Read(action, own, evaluationExists: action == "evaluation"));
            Assert.Equal(200, await fixture.Read(action, reviewer, evaluationExists: action == "evaluation"));
        }
        Assert.Single(await fixture.List(own));
        Assert.Single(await fixture.List(reviewer));

        var key = Fixture.KeyPrincipal(fixture.Project.Id, "deployments:read");
        var otherKey = Fixture.KeyPrincipal(Guid.NewGuid(), "deployments:read");
        Assert.Single(await fixture.List(key));
        Assert.Empty(await fixture.List(otherKey));
        Assert.Empty(await fixture.List(otherKey, fixture.Project.Id));
        foreach (var action in new[] { "get", "events", "evaluation" }) {
            Assert.Equal(404, await fixture.Read(action, otherKey));
            Assert.Equal(200, await fixture.Read(action, key, evaluationExists: action == "evaluation"));
        }
        Assert.Empty(await fixture.List(Fixture.KeyPrincipal(fixture.Project.Id))); // real keys cannot carry read scope
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Rejected")]
    public async Task DecisionsRequireTeamMembershipNonrequesterAndEvaluatedRole(string decision) {
        using var fixture = new Fixture();
        var outsider = fixture.AddUser("Approver");
        var member = fixture.AddUser("Approver");
        var security = fixture.AddUser("Security");
        var admin = fixture.AddUser("Admin");
        fixture.Join(member);
        fixture.Join(security);
        fixture.Join(fixture.Creator);
        fixture.Source.BeginEvaluation();
        var evaluation = fixture.AddEvaluation("Security");
        fixture.Source.ApplyDecision(new PolicyOutcome(PolicyDecision.RequireApproval, evaluation.Id, 1, null), fixture.Now);
        fixture.Db.SaveChanges();
        Assert.Equal(404, await fixture.Decide(Fixture.UserPrincipal(outsider), decision));
        Assert.Equal(403, await fixture.Decide(Fixture.UserPrincipal(member), decision));
        Assert.Equal(403, await fixture.Decide(Fixture.UserPrincipal(fixture.Creator), decision));
        Assert.Equal(401, await fixture.Decide(Fixture.KeyPrincipal(fixture.Project.Id, "deployments:approve"), decision));
        Assert.Equal(0, fixture.Db.Approvals.Count());
        Assert.Equal(0, fixture.Db.Jobs.Count());
        Assert.Equal(DeploymentStatus.AwaitingApproval, fixture.Source.Status);
        Assert.Equal(204, await fixture.Decide(Fixture.UserPrincipal(security), decision));
        Assert.Single(fixture.Db.Approvals);
    }

    [Fact]
    public async Task CancellationAllowsRequesterOwnerOrAdminButNotMemberOrKey() {
        using var fixture = new Fixture();
        var member = fixture.AddUser("Developer");
        var owner = fixture.AddUser("Developer");
        var outsider = fixture.AddUser("Developer");
        var admin = fixture.AddUser("Admin");
        fixture.Join(member);
        fixture.Join(owner, TeamRole.Owner);
        Assert.Equal(404, await fixture.Cancel(Fixture.UserPrincipal(outsider)));
        Assert.Equal(403, await fixture.Cancel(Fixture.UserPrincipal(member)));
        Assert.Equal(404, await fixture.Cancel(Fixture.KeyPrincipal(fixture.Project.Id, "deployments:cancel")));
        Assert.Equal(DeploymentStatus.Requested, fixture.Source.Status);
        Assert.Empty(fixture.Db.DeploymentEvents);
        Assert.Equal(204, await fixture.Cancel(Fixture.UserPrincipal(owner)));
        Assert.Equal(409, await fixture.Cancel(Fixture.UserPrincipal(admin)));
        Assert.Equal(DeploymentStatus.Cancelled, fixture.Source.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequesterOrAdminMayCancel(bool admin) {
        using var fixture = new Fixture();
        fixture.Join(fixture.Creator);
        var actor = admin ? fixture.AddUser("Admin") : fixture.Creator;
        Assert.Equal(204, await fixture.Cancel(Fixture.UserPrincipal(actor)));
        Assert.Equal(DeploymentStatus.Cancelled, fixture.Source.Status);
    }

    private sealed class Fixture : IDisposable {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly ServiceProvider services;
        public ServiceProvider Services => services;
        public readonly AegisOpsDbContext Db;
        public readonly UserManager<User> Users;
        public readonly ReplayStore Replay = new();
        public readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        public readonly Team Team;
        public readonly Project Project;
        public readonly ProjectEnvironment Environment;
        public readonly Artifact Artifact;
        public readonly Deployment Source;
        public readonly User Creator;

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
            var creator = Creator = AddUser("Developer");
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

        public User AddUser(string role) {
            var user = AddUser();
            var storedRole = Db.Roles.SingleOrDefault(item => item.Name == role);
            if (storedRole is null) {
                storedRole = new IdentityRole<Guid>(role) { Id = Guid.NewGuid(), NormalizedName = role.ToUpperInvariant() };
                Db.Roles.Add(storedRole);
            }
            Db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = storedRole.Id });
            Db.SaveChanges();
            return user;
        }

        public void Join(User user, TeamRole role = TeamRole.Member) {
            Db.TeamMembers.Add(TeamMember.Create(Team.Id, user.Id, role, Now));
            Db.SaveChanges();
        }

        public PolicyEvaluation AddEvaluation(string role) {
            var evaluation = PolicyEvaluation.Create(Source.Id, PolicyDecision.RequireApproval, 1, Now,
                $$"""[{"Type":"RequireApprovals","RequiredRoles":["{{role}}"]}]""");
            Db.PolicyEvaluations.Add(evaluation);
            Db.SaveChanges();
            return evaluation;
        }

        public static ClaimsPrincipal UserPrincipal(User user) => new(new ClaimsIdentity([new Claim("sub", user.Id.ToString())], "test"));
        public static ClaimsPrincipal KeyPrincipal(Guid projectId, string? scope = null) => new(new ClaimsIdentity([
            new Claim("actor_type", "apiKey"),
            new Claim("sub", $"apikey:{Guid.NewGuid()}"),
            new Claim("project", projectId.ToString()),
            .. scope is null ? [] : new[] { new Claim("scope", scope) },
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

        public async Task<object[]> List(ClaimsPrincipal principal, Guid? projectId = null) {
            var result = await DeploymentEndpoints.List(Db, principal, Users, Context(), projectId);
            return Assert.IsAssignableFrom<IEnumerable<object>>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value).ToArray();
        }

        public Task<IResult> ApprovalQueueResult(ClaimsPrincipal principal) => DeploymentEndpoints.ApprovalQueue(principal, Users, Db, Context());
        public async Task<Guid[]> ApprovalQueue(ClaimsPrincipal principal) {
            var result = await ApprovalQueueResult(principal);
            var rows = Assert.IsAssignableFrom<IEnumerable<object>>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            return rows.Select(row => (Guid)row.GetType().GetProperty("Id")!.GetValue(row)!).ToArray();
        }

        public async Task<int> Read(string action, ClaimsPrincipal principal, bool evaluationExists = false) {
            if (evaluationExists && !Db.PolicyEvaluations.Any(item => item.DeploymentId == Source.Id)) AddEvaluation("Security");
            var result = action switch {
                "get" => await DeploymentEndpoints.Get(Source.Id, principal, Users, Db, Context()),
                "events" => await DeploymentEndpoints.Events(Source.Id, principal, Users, Db, Context()),
                _ => await DeploymentEndpoints.Evaluation(Source.Id, principal, Users, Db, Context()),
            };
            return ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        }

        public async Task<int> Decide(ClaimsPrincipal principal, string decision) =>
            ((IStatusCodeHttpResult)await DeploymentEndpoints.Decide(Source.Id, new DeploymentDecisionRequest(decision, null), principal, Users, Db, TimeProvider.System, Context())).StatusCode ?? 200;

        public async Task<int> Cancel(ClaimsPrincipal principal) =>
            ((IStatusCodeHttpResult)await DeploymentEndpoints.Cancel(Source.Id, principal, Users, Db, TimeProvider.System, Context())).StatusCode ?? 200;

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
            modelBuilder.Entity<Deployment>().Property(item => item.RequestedAt).HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<DeploymentEvent>().Property(item => item.Timestamp).HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<PolicyEvaluation>().Property(item => item.EvaluatedAt).HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
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
