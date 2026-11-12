using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using AegisOps.Api.Identity;
using AegisOps.Api.Organization;
using AegisOps.Application.Audit;
using AegisOps.Application.Authorization;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Tests;

/// <summary>
/// Audit check P1-07: team/member APIs and membership scoping. Drives the real team handlers over HTTP
/// with real JWT + permission authorization, and calls them directly to isolate the handler's own
/// Owner/Member/non-member rules (teams:write = Admin + Developer; the handler requires Admin or team Owner).
/// </summary>
public sealed class TeamMembershipEndpointTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FullLifecycleCreateGetUpdateAddChangeRemovePersistsStateAndAuditsEachMutation() {
        await using var host = await TeamHost.StartAsync();
        var (adminId, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (devId, _) = await host.CreateUserAsync("dev@example.test", "Developer");

        // Create: slug is trimmed/lower-cased, creator becomes Owner.
        using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/teams", admin,
            new CreateTeamRequest("  Platform Team ", "  Platform-Team ", " Core platform "));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/v1/teams/platform-team", created.Headers.Location?.OriginalString);
        var team = (await created.Content.ReadFromJsonAsync<TeamListItem>(Token))!;
        Assert.Equal(("Platform Team", "platform-team", "Core platform", "Owner"), (team.Name, team.Slug, team.Description, team.MyRole));

        // Get: detail lists the creator as Owner.
        var detail = await host.GetTeamAsync(admin, "PLATFORM-TEAM");
        Assert.Equal(team.Id, detail.Id);
        Assert.Equal("Owner", detail.MyRole);
        var owner = Assert.Single(detail.Members);
        Assert.Equal((adminId, "admin@example.test", "Owner"), (owner.UserId, owner.Email, owner.Role));

        // Update name + description.
        using var updated = await host.SendAsync(HttpMethod.Patch, "/api/v1/teams/platform-team", admin,
            new UpdateTeamRequest(" Platform Engineering ", "Builds the paved road"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedBody = (await updated.Content.ReadFromJsonAsync<TeamListItem>(Token))!;
        Assert.Equal(("Platform Engineering", "platform-team", "Builds the paved road", "Owner"),
            (updatedBody.Name, updatedBody.Slug, updatedBody.Description, updatedBody.MyRole));

        // Add member (role is case-insensitive).
        using var added = await host.SendAsync(HttpMethod.Put, $"/api/v1/teams/platform-team/members/{devId}", admin,
            new UpsertTeamMemberRequest("member"));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var addedBody = (await added.Content.ReadFromJsonAsync<TeamMemberItem>(Token))!;
        Assert.Equal((devId, "dev@example.test", "Member"), (addedBody.UserId, addedBody.Email, addedBody.Role));
        await host.WithDbAsync(async db => Assert.Equal(TeamRole.Member,
            (await db.TeamMembers.AsNoTracking().SingleAsync(m => m.TeamId == team.Id && m.UserId == devId, Token)).Role));

        // Change role in place (no duplicate row).
        using var promoted = await host.SendAsync(HttpMethod.Put, $"/api/v1/teams/platform-team/members/{devId}", admin,
            new UpsertTeamMemberRequest("Owner"));
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        Assert.Equal("Owner", (await promoted.Content.ReadFromJsonAsync<TeamMemberItem>(Token))!.Role);
        await host.WithDbAsync(async db => {
            var rows = await db.TeamMembers.AsNoTracking().Where(m => m.TeamId == team.Id && m.UserId == devId).ToListAsync(Token);
            Assert.Equal(TeamRole.Owner, Assert.Single(rows).Role);
        });
        detail = await host.GetTeamAsync(admin, "platform-team");
        Assert.Equal(2, detail.Members.Count(m => m.Role == "Owner"));

        // Remove member.
        using var removed = await host.SendAsync(HttpMethod.Delete, $"/api/v1/teams/platform-team/members/{devId}", admin);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        await host.WithDbAsync(async db => {
            var stored = await db.Teams.AsNoTracking().SingleAsync(Token);
            Assert.Equal(("Platform Engineering", "platform-team", "Builds the paved road"), (stored.Name, stored.Slug, stored.Description));
            var members = await db.TeamMembers.AsNoTracking().Where(m => m.TeamId == team.Id).ToListAsync(Token);
            Assert.Equal((adminId, TeamRole.Owner), (Assert.Single(members).UserId, members[0].Role));

            var audit = (await db.AuditEvents.AsNoTracking().ToListAsync(Token)).OrderBy(e => e.Timestamp).ToList();
            Assert.Equal(["team.created", "team.updated", "team.member_upserted", "team.member_upserted", "team.member_removed"],
                audit.Select(e => e.Action));
            Assert.All(audit, e => {
                Assert.Equal(("User", adminId, "admin@example.test"), (e.ActorType, e.ActorId, e.ActorDisplay));
                Assert.Equal(("Team", team.Id), (e.ResourceType, e.ResourceId));
            });
        });

        detail = await host.GetTeamAsync(admin, "platform-team");
        Assert.Equal([adminId], detail.Members.Select(m => m.UserId));
    }

    [Fact]
    public async Task CreateTeamRejectsInvalidDuplicateAndUnprivilegedRequestsWithoutSideEffects() {
        await using var host = await TeamHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (_, developer) = await host.CreateUserAsync("dev@example.test", "Developer");
        var (_, security) = await host.CreateUserAsync("sec@example.test", "Security");

        async Task<HttpStatusCode> Create(string? bearer, CreateTeamRequest body) {
            using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/teams", bearer, body);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Created, await Create(admin, new("Platform", "platform", null)));
        Assert.Equal(HttpStatusCode.Conflict, await Create(admin, new("Platform 2", " PLATFORM ", null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(admin, new("  ", "blank-name", null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(admin, new("Blank slug", "   ", null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(admin, new("Spaces", "has space", null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(admin, new("Symbols", "bad/slug!", null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(admin, new("Unicode", "tëam", null)));
        Assert.Equal(HttpStatusCode.Forbidden, await Create(developer, new("Sneaky", "sneaky", null)));
        Assert.Equal(HttpStatusCode.Forbidden, await Create(security, new("Sneaky", "sneaky-sec", null)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Create(null, new("Anon", "anon", null)));

        await host.WithDbAsync(async db => {
            Assert.Equal(["platform"], await db.Teams.Select(t => t.Slug).ToListAsync(Token));
            Assert.Equal(1, await db.TeamMembers.CountAsync(Token));
            Assert.Equal(["team.created"], await db.AuditEvents.Where(e => e.Outcome == "Succeeded").Select(e => e.Action).ToListAsync(Token));
            // docs/09 section 5: the two policy-level 403s are audited as Denied.
            Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == "authorization.denied" && e.Outcome == "Denied", Token));
        });
    }

    [Fact]
    public async Task UpdateTeamValidatesInputAndCanClearDescription() {
        await using var host = await TeamHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        await host.CreateTeamAsync(admin, "platform", "Original description");

        async Task<HttpStatusCode> Patch(string slug, UpdateTeamRequest body) {
            using var response = await host.SendAsync(HttpMethod.Patch, $"/api/v1/teams/{slug}", admin, body);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await Patch("platform", new(null, null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Patch("platform", new("   ", "ignored")));
        Assert.Equal(HttpStatusCode.NotFound, await Patch("ghost", new("Ghost", null)));
        await host.WithDbAsync(async db => {
            var stored = await db.Teams.AsNoTracking().SingleAsync(Token);
            Assert.Equal(("platform", "Original description"), (stored.Name, stored.Description));
            Assert.Equal(0, await db.AuditEvents.CountAsync(e => e.Action == "team.updated", Token));
        });

        // Description-only update keeps the name; empty string clears the description.
        Assert.Equal(HttpStatusCode.OK, await Patch("platform", new(null, "  ")));
        await host.WithDbAsync(async db => {
            var stored = await db.Teams.AsNoTracking().SingleAsync(Token);
            Assert.Equal(("platform", (string?)null), (stored.Name, stored.Description));
            Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == "team.updated", Token));
        });
    }

    [Fact]
    public async Task UpsertAndRemoveMemberRejectInvalidRoleUnknownOrInactiveUserAndUnknownTargets() {
        await using var host = await TeamHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (devId, _) = await host.CreateUserAsync("dev@example.test", "Developer");
        var (inactiveId, _) = await host.CreateUserAsync("gone@example.test", "Developer", active: false);
        var team = await host.CreateTeamAsync(admin, "platform");

        async Task<HttpStatusCode> Put(string slug, Guid userId, string role) {
            using var response = await host.SendAsync(HttpMethod.Put, $"/api/v1/teams/{slug}/members/{userId}", admin,
                new UpsertTeamMemberRequest(role));
            return response.StatusCode;
        }

        async Task<HttpStatusCode> Delete(string slug, Guid userId) {
            using var response = await host.SendAsync(HttpMethod.Delete, $"/api/v1/teams/{slug}/members/{userId}", admin);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await Put("platform", devId, "Admin"));
        Assert.Equal(HttpStatusCode.BadRequest, await Put("platform", devId, ""));
        Assert.Equal(HttpStatusCode.BadRequest, await Put("platform", devId, "0"));
        Assert.Equal(HttpStatusCode.BadRequest, await Put("platform", devId, "Owner, Member"));
        Assert.Equal(HttpStatusCode.NotFound, await Put("platform", Guid.NewGuid(), "Member"));
        Assert.Equal(HttpStatusCode.NotFound, await Put("platform", inactiveId, "Member"));
        Assert.Equal(HttpStatusCode.NotFound, await Put("ghost", devId, "Member"));
        Assert.Equal(HttpStatusCode.NotFound, await Delete("platform", devId));          // not a member
        Assert.Equal(HttpStatusCode.NotFound, await Delete("platform", Guid.NewGuid()));  // unknown user
        Assert.Equal(HttpStatusCode.NotFound, await Delete("ghost", devId));

        // Duplicate add is idempotent: one row, same role.
        Assert.Equal(HttpStatusCode.OK, await Put("platform", devId, "Member"));
        Assert.Equal(HttpStatusCode.OK, await Put("platform", devId, "Member"));

        await host.WithDbAsync(async db => {
            var members = await db.TeamMembers.AsNoTracking().Where(m => m.TeamId == team).ToListAsync(Token);
            Assert.Equal(2, members.Count);
            Assert.Equal(TeamRole.Member, members.Single(m => m.UserId == devId).Role);
            Assert.DoesNotContain(members, m => m.UserId == inactiveId);
            Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == "team.member_upserted", Token));
            Assert.Equal(0, await db.AuditEvents.CountAsync(e => e.Action == "team.member_removed", Token));
        });
    }

    [Theory]
    [InlineData("Developer", true)]
    [InlineData("Security", false)]
    [InlineData("Approver", false)]
    public async Task NonAdminTeamOwnersCanManageTheirTeamThroughTheRoutesButOutsidersCannot(string role, bool canWrite) {
        await using var host = await TeamHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (userId, bearer) = await host.CreateUserAsync("user@example.test", role);
        var (otherId, _) = await host.CreateUserAsync("other@example.test", "Developer");
        await host.CreateTeamAsync(admin, "platform");
        using (var promote = await host.SendAsync(HttpMethod.Put, $"/api/v1/teams/platform/members/{userId}", admin,
            new UpsertTeamMemberRequest("Owner")))
            Assert.Equal(HttpStatusCode.OK, promote.StatusCode);

        // teams:read is granted to every role.
        using (var list = await host.SendAsync(HttpMethod.Get, "/api/v1/teams", bearer))
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("Owner", (await host.GetTeamAsync(bearer, "platform")).MyRole);

        // docs/09: teams:write is Admin, or a Developer who is Owner of the team (route permission + handler Owner check).
        // Security/Approver do not hold teams:write, so even as team Owner they are rejected by the route policy (403).
        var (_, outsider) = await host.CreateUserAsync("outsider@example.test", "Developer");
        using (var hijack = await host.SendAsync(HttpMethod.Patch, "/api/v1/teams/platform", outsider, new UpdateTeamRequest("Hijack", null)))
            Assert.Equal(HttpStatusCode.NotFound, hijack.StatusCode);
        using (var sneak = await host.SendAsync(HttpMethod.Put, $"/api/v1/teams/platform/members/{otherId}", outsider, new UpsertTeamMemberRequest("Owner")))
            Assert.Equal(HttpStatusCode.NotFound, sneak.StatusCode);
        using (var patch = await host.SendAsync(HttpMethod.Patch, "/api/v1/teams/platform", bearer, new UpdateTeamRequest("Renamed by owner", null)))
            Assert.Equal(canWrite ? HttpStatusCode.OK : HttpStatusCode.Forbidden, patch.StatusCode);
        using (var put = await host.SendAsync(HttpMethod.Put, $"/api/v1/teams/platform/members/{otherId}", bearer, new UpsertTeamMemberRequest("Member")))
            Assert.Equal(canWrite ? HttpStatusCode.OK : HttpStatusCode.Forbidden, put.StatusCode);
        using (var delete = await host.SendAsync(HttpMethod.Delete, $"/api/v1/teams/platform/members/{otherId}", bearer)) {
            if (canWrite) Assert.True(delete.IsSuccessStatusCode);
            else Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        }
        using (var anonymous = await host.SendAsync(HttpMethod.Get, "/api/v1/teams", null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await host.WithDbAsync(async db => {
            Assert.Equal(canWrite ? "Renamed by owner" : "platform", (await db.Teams.AsNoTracking().SingleAsync(Token)).Name);
            Assert.Equal(2, await db.TeamMembers.CountAsync(Token)); // creator Admin + promoted Owner; the added member was removed again
        });
    }

    [Fact]
    public async Task HandlersLetTeamOwnersManageTheirTeamButNotMembersOrNonMembers() {
        await using var host = await TeamHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (ownerId, _) = await host.CreateUserAsync("owner@example.test", "Developer");
        var (memberId, _) = await host.CreateUserAsync("member@example.test", "Developer");
        var (outsiderId, _) = await host.CreateUserAsync("outsider@example.test", "Developer");
        var (newcomerId, _) = await host.CreateUserAsync("newcomer@example.test", "Developer");
        var team = await host.CreateTeamAsync(admin, "platform");
        await host.AddMemberAsync(admin, "platform", ownerId, "Owner");
        await host.AddMemberAsync(admin, "platform", memberId, "Member");
        var auditBefore = await host.WithDbAsync(db => db.AuditEvents.CountAsync(Token));

        // Member and non-member are refused (404 hides the team) on every mutation, nothing changes.
        foreach (var actor in new[] { memberId, outsiderId }) {
            Assert.Equal(404, await host.UpdateAsync(actor, "platform", new UpdateTeamRequest("Hijack", null)));
            Assert.Equal(404, await host.UpsertAsync(actor, "platform", newcomerId, "Member"));
            Assert.Equal(404, await host.UpsertAsync(actor, "platform", actor, "Owner")); // no self-promotion
            Assert.Equal(404, await host.RemoveAsync(actor, "platform", ownerId));
        }

        await host.WithDbAsync(async db => {
            Assert.Equal("platform", (await db.Teams.AsNoTracking().SingleAsync(Token)).Name);
            Assert.Equal(TeamRole.Member, (await db.TeamMembers.AsNoTracking().SingleAsync(m => m.UserId == memberId, Token)).Role);
            Assert.False(await db.TeamMembers.AnyAsync(m => m.UserId == newcomerId || m.UserId == outsiderId, Token));
            Assert.Equal(auditBefore, await db.AuditEvents.CountAsync(Token));
        });

        // A non-admin Owner can do everything at handler level.
        Assert.Equal(200, await host.UpdateAsync(ownerId, "platform", new UpdateTeamRequest("Platform Eng", null)));
        Assert.Equal(200, await host.UpsertAsync(ownerId, "platform", newcomerId, "Member"));
        Assert.Equal(200, await host.UpsertAsync(ownerId, "platform", memberId, "Owner"));
        Assert.Equal(204, await host.RemoveAsync(ownerId, "platform", newcomerId));

        await host.WithDbAsync(async db => {
            Assert.Equal("Platform Eng", (await db.Teams.AsNoTracking().SingleAsync(Token)).Name);
            var members = await db.TeamMembers.AsNoTracking().Where(m => m.TeamId == team).ToListAsync(Token);
            Assert.Equal(TeamRole.Owner, members.Single(m => m.UserId == memberId).Role);
            Assert.DoesNotContain(members, m => m.UserId == newcomerId);
            var ownerAudit = await db.AuditEvents.AsNoTracking().Where(e => e.ActorId == ownerId).ToListAsync(Token);
            Assert.Equal(["team.member_removed", "team.member_upserted", "team.member_upserted", "team.updated"],
                ownerAudit.Select(e => e.Action).Order(StringComparer.Ordinal));
            Assert.All(ownerAudit, e => Assert.Equal(team, e.ResourceId));
        });
    }

    [Fact]
    public async Task ListAndGetOnlyRevealTeamsTheCallerBelongsToUnlessAdmin() {
        await using var host = await TeamHost.StartAsync();
        var (adminId, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (_, otherAdmin) = await host.CreateUserAsync("admin2@example.test", "Admin");
        var (devId, developer) = await host.CreateUserAsync("dev@example.test", "Developer");
        var (_, outsider) = await host.CreateUserAsync("outsider@example.test", "Developer");
        var alpha = await host.CreateTeamAsync(admin, "alpha");
        var bravo = await host.CreateTeamAsync(admin, "bravo");
        var charlie = await host.CreateTeamAsync(otherAdmin, "charlie");
        await host.AddMemberAsync(admin, "alpha", devId, "Member");
        await host.AddMemberAsync(otherAdmin, "charlie", devId, "Owner");

        var devTeams = await host.ListTeamsAsync(developer);
        Assert.Equal([("alpha", "Member"), ("charlie", "Owner")], devTeams.Select(t => (t.Slug, t.MyRole!)));
        Assert.Empty(await host.ListTeamsAsync(outsider));

        // Admins see every team, including ones they are not a member of.
        var adminTeams = await host.ListTeamsAsync(admin);
        Assert.Equal([alpha, bravo, charlie], adminTeams.Select(t => t.Id));
        Assert.All(adminTeams, t => Assert.Null(t.MyRole));

        // GetTeam: member sees full roster; non-member gets the same 404 as an unknown slug.
        var alphaDetail = await host.GetTeamAsync(developer, "alpha");
        Assert.Equal("Member", alphaDetail.MyRole);
        // Role is stored as text, so ORDER BY role is alphabetical (Member before Owner); compare as a set.
        Assert.Equal([(adminId, "Owner"), (devId, "Member")], alphaDetail.Members.Select(m => (m.UserId, m.Role)).OrderBy(m => m.Role == "Owner" ? 0 : 1));
        foreach (var slug in new[] { "bravo", "does-not-exist" }) {
            using var hidden = await host.SendAsync(HttpMethod.Get, $"/api/v1/teams/{slug}", developer);
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            Assert.Equal("Team was not found.", (await hidden.Content.ReadFromJsonAsync<ProblemPayload>(Token))!.Title);
        }

        using (var outsiderGet = await host.SendAsync(HttpMethod.Get, "/api/v1/teams/alpha", outsider))
            Assert.Equal(HttpStatusCode.NotFound, outsiderGet.StatusCode);

        var adminView = await host.GetTeamAsync(admin, "charlie");
        Assert.Null(adminView.MyRole);
        Assert.Equal(2, adminView.Members.Count);

        // Owner of charlie still cannot modify alpha (Member) or bravo (non-member) at handler level.
        Assert.Equal(404, await host.UpdateAsync(devId, "alpha", new UpdateTeamRequest("x", null)));
        Assert.Equal(404, await host.UpdateAsync(devId, "bravo", new UpdateTeamRequest("x", null)));
        Assert.Equal(404, await host.RemoveAsync(devId, "alpha", adminId));
        Assert.Equal(200, await host.UpdateAsync(devId, "charlie", new UpdateTeamRequest("Charlie Team", null)));

        // Removing the developer from alpha immediately drops it from their list.
        await host.RemoveMemberAsync(admin, "alpha", devId);
        Assert.Equal(["charlie"], (await host.ListTeamsAsync(developer)).Select(t => t.Slug));
        using (var afterRemoval = await host.SendAsync(HttpMethod.Get, "/api/v1/teams/alpha", developer))
            Assert.Equal(HttpStatusCode.NotFound, afterRemoval.StatusCode);
    }

    [Fact]
    public async Task LastOwnerCannotBeDemotedOrRemovedButCanOnceAnotherOwnerExists() {
        await using var host = await TeamHost.StartAsync();
        var (adminId, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (devId, _) = await host.CreateUserAsync("dev@example.test", "Developer");
        var team = await host.CreateTeamAsync(admin, "platform");
        await host.AddMemberAsync(admin, "platform", devId, "Member");
        var auditBefore = await host.WithDbAsync(db => db.AuditEvents.CountAsync(Token));

        // Even an Admin cannot leave the team ownerless.
        using (var demote = await host.SendAsync(HttpMethod.Put, $"/api/v1/teams/platform/members/{adminId}", admin,
            new UpsertTeamMemberRequest("Member"))) {
            Assert.Equal(HttpStatusCode.BadRequest, demote.StatusCode);
            Assert.Equal("A team must keep at least one Owner.", (await demote.Content.ReadFromJsonAsync<ProblemPayload>(Token))!.Title);
        }
        using (var remove = await host.SendAsync(HttpMethod.Delete, $"/api/v1/teams/platform/members/{adminId}", admin))
            Assert.Equal(HttpStatusCode.BadRequest, remove.StatusCode);

        // A non-admin sole Owner is likewise blocked at handler level.
        Assert.Equal(200, await host.UpsertAsync(adminId, "platform", devId, "Owner"));
        Assert.Equal(204, await host.RemoveAsync(devId, "platform", adminId));
        Assert.Equal(400, await host.UpsertAsync(devId, "platform", devId, "Member"));
        Assert.Equal(400, await host.RemoveAsync(devId, "platform", devId));

        await host.WithDbAsync(async db => {
            var member = Assert.Single(await db.TeamMembers.AsNoTracking().Where(m => m.TeamId == team).ToListAsync(Token));
            Assert.Equal((devId, TeamRole.Owner), (member.UserId, member.Role));
            Assert.Equal(auditBefore + 2, await db.AuditEvents.CountAsync(Token)); // only the promote + remove that succeeded
        });

        // With a second Owner, demotion of the first then succeeds.
        Assert.Equal(200, await host.UpsertAsync(adminId, "platform", adminId, "Owner"));
        Assert.Equal(200, await host.UpsertAsync(devId, "platform", devId, "Member"));
        await host.WithDbAsync(async db => {
            var roles = await db.TeamMembers.AsNoTracking().Where(m => m.TeamId == team)
                .ToDictionaryAsync(m => m.UserId, m => m.Role, Token);
            Assert.Equal(TeamRole.Owner, roles[adminId]);
            Assert.Equal(TeamRole.Member, roles[devId]);
        });
    }

    [Fact]
    public async Task TeamMembershipGovernsProjectVisibilityForNonAdmins() {
        await using var host = await TeamHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (devId, developer) = await host.CreateUserAsync("dev@example.test", "Developer");
        var platform = await host.CreateTeamAsync(admin, "platform");
        var other = await host.CreateTeamAsync(admin, "other");
        await host.WithDbAsync(async db => {
            db.Projects.AddRange(
                Project.Create(platform, "Circuit", "circuit", DateTimeOffset.UtcNow),
                Project.Create(other, "Secret", "secret", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Token);
        });

        async Task<HttpStatusCode> GetProject(string slug) {
            using var response = await host.SendAsync(HttpMethod.Get, $"/api/v1/projects/{slug}", developer);
            return response.StatusCode;
        }

        async Task<string[]> ListProjects() {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/projects", developer);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<List<ProjectListItem>>(Token))!.Select(p => p.Slug).ToArray();
        }

        Assert.Equal(HttpStatusCode.NotFound, await GetProject("circuit"));
        Assert.Empty(await ListProjects());

        await host.AddMemberAsync(admin, "platform", devId, "Member");
        Assert.Equal(HttpStatusCode.OK, await GetProject("circuit"));
        Assert.Equal(HttpStatusCode.NotFound, await GetProject("secret"));
        Assert.Equal(["circuit"], await ListProjects());

        await host.RemoveMemberAsync(admin, "platform", devId);
        Assert.Equal(HttpStatusCode.NotFound, await GetProject("circuit"));
        Assert.Empty(await ListProjects());

        using var adminList = await host.SendAsync(HttpMethod.Get, "/api/v1/projects", admin);
        Assert.Equal(2, (await adminList.Content.ReadFromJsonAsync<List<ProjectListItem>>(Token))!.Count);
    }

    private sealed record ProblemPayload(string? Title, int? Status);

    private sealed class TeamHost : IAsyncDisposable {
        private static readonly JwtOptions Jwt = new() {
            Issuer = "aegisops-tests",
            Audience = "aegisops-tests",
            SigningKey = "team-membership-tests-signing-key-0123456789",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7,
        };

        private readonly WebApplication _app;
        private readonly SqliteConnection _connection;
        private readonly HttpClient _client;

        private TeamHost(WebApplication app, SqliteConnection connection) {
            _app = app;
            _connection = connection;
            _client = app.GetTestClient();
        }

        public static async Task<TeamHost> StartAsync() {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(Token);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Jwt:Issuer"] = Jwt.Issuer,
                ["Jwt:Audience"] = Jwt.Audience,
                ["Jwt:SigningKey"] = Jwt.SigningKey,
                ["Jwt:AccessTokenMinutes"] = Jwt.AccessTokenMinutes.ToString(CultureInfo.InvariantCulture),
            });
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection)
                .UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteModelCustomizer>());
            builder.Services.AddIdentityCore<User>(IdentityStoreExtensions.ConfigureIdentity).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AegisOpsDbContext>();
            builder.Services.AddScoped<IAuditWriter, EfAuditWriter>();
            builder.Services.AddJwtAuthentication(builder.Configuration);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            // Same routes and permissions as Program.cs; typed lambdas avoid the route-handler analyzer crash on method groups.
            app.MapGet("/api/v1/teams", (ClaimsPrincipal user, UserManager<User> users, AegisOpsDbContext db, HttpContext http) =>
                ListTeams.Handle(user, users, db, http))
                .RequireAuthorization(Permissions.TeamsRead);
            app.MapPost("/api/v1/teams", (CreateTeamRequest body, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                CreateTeam.Handle(body, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.UsersManage);
            app.MapGet("/api/v1/teams/{slug}", (string slug, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, HttpContext http) => GetTeam.Handle(slug, user, users, db, http))
                .RequireAuthorization(Permissions.TeamsRead);
            app.MapPatch("/api/v1/teams/{slug}", (string slug, UpdateTeamRequest body, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, IAuditWriter audit, HttpContext http) => UpdateTeam.Handle(slug, body, user, users, db, audit, http))
                .RequireAuthorization(Permissions.TeamsWrite);
            app.MapPut("/api/v1/teams/{slug}/members/{userId:guid}", (string slug, Guid userId, UpsertTeamMemberRequest body,
                ClaimsPrincipal user, UserManager<User> users, AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                UpsertTeamMember.Handle(slug, userId, body, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.TeamsWrite);
            app.MapDelete("/api/v1/teams/{slug}/members/{userId:guid}", (string slug, Guid userId, ClaimsPrincipal user,
                UserManager<User> users, AegisOpsDbContext db, IAuditWriter audit, HttpContext http) =>
                RemoveTeamMember.Handle(slug, userId, user, users, db, audit, http))
                .RequireAuthorization(Permissions.TeamsWrite);
            app.MapGet("/api/v1/projects", (ClaimsPrincipal user, UserManager<User> users, AegisOpsDbContext db, HttpContext http) =>
                ListProjects.Handle(user, users, db, http))
                .RequireAuthorization(Permissions.ProjectsRead);
            app.MapGet("/api/v1/projects/{slug}", (string slug, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, HttpContext http) => GetProject.Handle(slug, user, users, db, http))
                .RequireAuthorization(Permissions.ProjectsRead);
            await app.StartAsync(Token);

            var host = new TeamHost(app, connection);
            await host.WithDbAsync(db => db.Database.EnsureCreatedAsync(Token));
            return host;
        }

        public async Task<(Guid Id, string Bearer)> CreateUserAsync(string email, string role, bool active = true) {
            await using var scope = _app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync(role))
                Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() })).Succeeded);
            var user = User.Create(email, email.Split('@')[0], DateTimeOffset.UtcNow);
            if (!active) user.Deactived();
            Assert.True((await users.CreateAsync(user)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            return (user.Id, active ? JwtAccessTokenFactory.Create(user, [role], Jwt, DateTimeOffset.UtcNow) : string.Empty);
        }

        public async Task<Guid> CreateTeamAsync(string bearer, string slug, string? description = null) {
            using var response = await SendAsync(HttpMethod.Post, "/api/v1/teams", bearer, new CreateTeamRequest(slug, slug, description));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<TeamListItem>(Token))!.Id;
        }

        public async Task AddMemberAsync(string bearer, string slug, Guid userId, string role) {
            using var response = await SendAsync(HttpMethod.Put, $"/api/v1/teams/{slug}/members/{userId}", bearer, new UpsertTeamMemberRequest(role));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task RemoveMemberAsync(string bearer, string slug, Guid userId) {
            using var response = await SendAsync(HttpMethod.Delete, $"/api/v1/teams/{slug}/members/{userId}", bearer);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        public async Task<List<TeamListItem>> ListTeamsAsync(string bearer) {
            using var response = await SendAsync(HttpMethod.Get, "/api/v1/teams", bearer);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<List<TeamListItem>>(Token))!;
        }

        public async Task<TeamDetailResponse> GetTeamAsync(string bearer, string slug) {
            using var response = await SendAsync(HttpMethod.Get, $"/api/v1/teams/{slug}", bearer);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<TeamDetailResponse>(Token))!;
        }

        // Direct handler calls: exercise the handler's own Owner/Member/non-member rules beneath the Admin-only route policy.
        public Task<int> UpdateAsync(Guid actorId, string slug, UpdateTeamRequest body) =>
            InvokeAsync(services => UpdateTeam.Handle(slug, body, Principal(actorId), services.GetRequiredService<UserManager<User>>(),
                services.GetRequiredService<AegisOpsDbContext>(), services.GetRequiredService<IAuditWriter>(), Context()));

        public Task<int> UpsertAsync(Guid actorId, string slug, Guid userId, string role) =>
            InvokeAsync(services => UpsertTeamMember.Handle(slug, userId, new UpsertTeamMemberRequest(role), Principal(actorId),
                services.GetRequiredService<UserManager<User>>(), services.GetRequiredService<AegisOpsDbContext>(),
                services.GetRequiredService<IAuditWriter>(), TimeProvider.System, Context()));

        public Task<int> RemoveAsync(Guid actorId, string slug, Guid userId) =>
            InvokeAsync(services => RemoveTeamMember.Handle(slug, userId, Principal(actorId), services.GetRequiredService<UserManager<User>>(),
                services.GetRequiredService<AegisOpsDbContext>(), services.GetRequiredService<IAuditWriter>(), Context()));

        private async Task<int> InvokeAsync(Func<IServiceProvider, Task<IResult>> call) {
            await using var scope = _app.Services.CreateAsyncScope();
            var result = await call(scope.ServiceProvider);
            return Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? StatusCodes.Status200OK;
        }

        private static ClaimsPrincipal Principal(Guid userId) => new(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "test"));

        private static DefaultHttpContext Context() => new() { RequestAborted = Token };

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer, object? body = null) {
            var request = new HttpRequestMessage(method, path);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
            return _client.SendAsync(request, Token);
        }

        public async Task WithDbAsync(Func<AegisOpsDbContext, Task> action) {
            await using var scope = _app.Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>());
        }

        public async Task<T> WithDbAsync<T>(Func<AegisOpsDbContext, Task<T>> action) {
            await using var scope = _app.Services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>());
        }

        public async ValueTask DisposeAsync() {
            _client.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
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
            modelBuilder.Entity<Artifact>().Property(item => item.CreatedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
        }
    }
}
