using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using AegisOps.Api.Ai;
using AegisOps.Api.Identity;
using AegisOps.Api.Organization;
using AegisOps.Application.Audit;
using AegisOps.Application.Authorization;
using AegisOps.Domain.Ai;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AegisOps.Api.Tests;

/// <summary>
/// Audit checks P1-05 and P8-02: the complete permission x role matrix from docs/09 section 4.
/// <list type="number">
/// <item>Pure table: RolePermissions grants exactly what docs/09 says, for every permission x role.</item>
/// <item>Real endpoint table: the actual Program.cs host is started (Kestrel on 127.0.0.1:0, unreachable Postgres,
/// no Redis) and its EndpointDataSource must equal <see cref="Routes"/>; a source guard parses Program.cs too.</item>
/// <item>HTTP matrix: every authorized /api/v1 route is called with a real JWT as every role and anonymously
/// against the real host. 403 and 401 are decided by the authorization middleware before any handler or
/// database access; allowed roles reach the handler (which then fails on the unreachable database, i.e. "not 401/403").</item>
/// <item>SQLite host: representative 2xx calls per permission plus the handler-level rules that docs/09 adds.
/// Deployment handler rules (team membership, approver != requester, evaluated role, cancel by requester/Owner/Admin,
/// API-key Production opt-in) are covered by DeploymentAuthorizationTests and are not duplicated here.</item>
/// </list>
/// docs/09 is the spec; every mismatch found by the first run of this suite was fixed in src/ (see the regression tests).
/// </summary>
public sealed class AuthorizationMatrixTests(AuthorizationMatrixTests.RealApiFixture api)
    : IClassFixture<AuthorizationMatrixTests.RealApiFixture> {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string Admin = "Admin";
    private const string Developer = "Developer";
    private const string Security = "Security";
    private const string Approver = "Approver";
    private const string Anonymous = "Anonymous";
    private const string AllRoles = "Admin,Developer,Security,Approver";
    private const string AdminOnly = "Admin";
    private const string AdminDev = "Admin,Developer";
    private const string AdminSec = "Admin,Security";
    private const string Approvers = "Admin,Security,Approver";

    private static readonly string[] Roles = [Admin, Developer, Security, Approver];
    private static readonly string[] SecurityAndApprover = [Security, Approver];
    private static readonly string[] UnknownRole = ["Auditor"];
    private static readonly string[] DocApiKeyScopes = ["artifacts:write", "scans:write", "deployments:request"];
    private static readonly string[] ProductionTier = ["Production"];
    private static readonly string[] DeployScope = ["deployments:request"];
    private static readonly string[] ExpectedAnonymousRoutes = ["POST /api/v1/auth/login => <anonymous>", "POST /api/v1/auth/refresh => <anonymous>"];
    private static readonly string[] ExpectedNonApiPatterns = ["/health/live", "/health/ready"];
    private static readonly string[] ExpectedApiKeyRoutes = [
        "PATCH /api/v1/artifacts/{id:guid}",
        "POST /api/v1/artifacts/{id:guid}/scans",
        "POST /api/v1/deployments",
        "POST /api/v1/deployments/{id:guid}/rollback",
        "POST /api/v1/projects/{slug}/artifacts",
    ];

    // ---------------------------------------------------------------------------------------------
    // docs/09 section 4 table. Columns: Admin, Developer, Security, Approver.
    // Conditional = a narrowed grant ("Owner of team", "team member", "requester / team Owner", "own only").
    // ---------------------------------------------------------------------------------------------
    private enum Cell { No, Yes, Conditional }

    private const Cell Y = Cell.Yes;
    private const Cell N = Cell.No;
    private const Cell C = Cell.Conditional;

    // docs/09 section 4 policy aliases: route policy name -> permission that satisfies it.
    private static readonly Dictionary<string, string> DocPolicyAliases = new(StringComparer.Ordinal) {
        ["ai:policies:read"] = "ai:policies:write",
    };

    private static readonly Dictionary<string, Cell[]> DocMatrix = new(StringComparer.Ordinal) {
        ["teams:read"] = [Y, Y, Y, Y],
        ["projects:read"] = [Y, Y, Y, Y],
        ["teams:write"] = [Y, C, N, N],
        ["projects:write"] = [Y, C, N, N],
        ["environments:write"] = [Y, C, N, N],
        ["apikeys:manage"] = [Y, C, N, N],
        ["artifacts:read"] = [Y, Y, Y, Y],
        ["scans:read"] = [Y, Y, Y, Y],
        ["findings:read"] = [Y, Y, Y, Y],
        ["artifacts:write"] = [Y, N, Y, N],
        ["scans:write"] = [Y, N, Y, N],
        ["findings:suppress"] = [Y, N, Y, N],
        ["deployments:read"] = [Y, Y, Y, Y],
        ["deployments:request"] = [Y, C, N, N],
        ["deployments:cancel"] = [Y, C, N, N],
        ["deployments:approve"] = [Y, N, Y, Y],
        ["policies:read"] = [Y, Y, Y, Y],
        ["policies:write"] = [Y, N, Y, N],
        ["ai:chat"] = [Y, Y, Y, Y],
        ["ai:requests:read"] = [Y, C, Y, C],
        ["ai:policies:write"] = [Y, N, Y, N],
        ["audit:read"] = [Y, N, Y, N],
        ["users:manage"] = [Y, N, N, N],
        ["roles:manage"] = [Y, N, N, N],
        ["models:manage"] = [Y, N, N, N],
    };

    // ---------------------------------------------------------------------------------------------
    // Every Map* in src/AegisOps.Api/Program.cs under /api/v1.
    // Policies: null = anonymous, "" = RequireAuthorization() (any authenticated principal), "a+b" = both policies.
    // DocRoles: roles docs/09 says the route policy admits (Conditional cells included; the handler narrows them,
    // e.g. "Owner of team" -> Admin or team Owner, "team member", "own only").
    // ---------------------------------------------------------------------------------------------
    private sealed record RouteRule(string Method, string Pattern, string? Policies, string DocPermission, string DocRoles);

    private static readonly RouteRule[] Routes = [
        new("POST", "/api/v1/auth/login", null, "anonymous", ""),
        new("POST", "/api/v1/auth/refresh", null, "anonymous", ""),
        new("POST", "/api/v1/auth/logout", "", "authenticated", AllRoles),
        new("GET", "/api/v1/auth/me", "", "authenticated", AllRoles),
        new("GET", "/api/v1/system/dependencies", "", "authenticated", AllRoles),
        new("POST", "/api/v1/auth/change-password", "", "authenticated", AllRoles),
        new("POST", "/api/v1/ai/chat", "ai:chat", "ai:chat", AllRoles),
        new("GET", "/api/v1/ai/models", "ai:chat", "ai:chat", AllRoles),
        new("GET", "/api/v1/ai/requests", "ai:requests:read", "ai:requests:read", AllRoles),
        new("GET", "/api/v1/ai/policies", "ai:policies:read", "ai:policies:write", AdminSec),
        new("PUT", "/api/v1/ai/policies", "ai:policies:write", "ai:policies:write", AdminSec),
        new("POST", "/api/v1/admin/ai/models/sync", "models:manage", "models:manage", AdminOnly),
        new("GET", "/api/v1/admin/ping", "users:manage", "users:manage", AdminOnly),
        new("GET", "/api/v1/admin/users", "users:manage", "users:manage", AdminOnly),
        new("POST", "/api/v1/admin/users/{id:guid}/reset-password", "users:manage", "users:manage", AdminOnly),
        new("POST", "/api/v1/admin/users", "users:manage", "users:manage", AdminOnly),
        new("PATCH", "/api/v1/admin/users/{id:guid}", "users:manage", "users:manage", AdminOnly),
        new("PUT", "/api/v1/admin/users/{id:guid}/roles", "roles:manage", "roles:manage", AdminOnly),
        new("GET", "/api/v1/teams", "teams:read", "teams:read", AllRoles),
        // Creating a team: teams:write per docs/09, but the "Owner of team" condition cannot apply to a new team.
        new("POST", "/api/v1/teams", "users:manage", "teams:write", AdminOnly),
        new("GET", "/api/v1/teams/{slug}", "teams:read", "teams:read", AllRoles),
        new("PATCH", "/api/v1/teams/{slug}", "teams:write", "teams:write", AdminDev),
        new("GET", "/api/v1/teams/{slug}/webhook", "teams:write", "teams:write", AdminDev),
        new("GET", "/api/v1/teams/{slug}/webhook/deliveries", "teams:write", "teams:write", AdminDev),
        new("GET", "/api/v1/teams/{slug}/webhook/summary", "teams:write", "teams:write", AdminDev),
        new("POST", "/api/v1/teams/{slug}/webhook/deliveries/{eventId:guid}/replay", "teams:write", "teams:write", AdminDev),
        new("PUT", "/api/v1/teams/{slug}/webhook", "teams:write", "teams:write", AdminDev),
        new("POST", "/api/v1/admin/webhooks/rewrap", "users:manage", "users:manage", AdminOnly),
        new("PUT", "/api/v1/teams/{slug}/members/{userId:guid}", "teams:write", "teams:write", AdminDev),
        new("DELETE", "/api/v1/teams/{slug}/members/{userId:guid}", "teams:write", "teams:write", AdminDev),
        new("POST", "/api/v1/projects", "projects:write", "projects:write", AdminDev),
        new("GET", "/api/v1/projects", "projects:read", "projects:read", AllRoles),
        new("GET", "/api/v1/projects/{slug}", "projects:read", "projects:read", AllRoles),
        new("PATCH", "/api/v1/projects/{slug}", "projects:write", "projects:write", AdminDev),
        new("PUT", "/api/v1/projects/{slug}/repository", "projects:write", "projects:write", AdminDev),
        new("PATCH", "/api/v1/projects/{slug}/environments/{envId:guid}", "environments:write", "environments:write", AdminDev),
        new("GET", "/api/v1/projects/{slug}/environments/{envId:guid}/runtime", "projects:read", "projects:read", AllRoles),
        new("GET", "/api/v1/projects/{slug}/environments/{envId:guid}/variables", "projects:read", "projects:read", AllRoles),
        new("PUT", "/api/v1/projects/{slug}/environments/{envId:guid}/variables/{name}", "environments:write", "environments:write", AdminDev),
        new("DELETE", "/api/v1/projects/{slug}/environments/{envId:guid}/variables/{name}", "environments:write", "environments:write", AdminDev),
        new("POST", "/api/v1/projects/{slug}/api-keys", "apikeys:manage", "apikeys:manage", AdminDev),
        new("GET", "/api/v1/projects/{slug}/api-keys", "apikeys:manage", "apikeys:manage", AdminDev),
        new("DELETE", "/api/v1/projects/{slug}/api-keys/{keyId:guid}", "apikeys:manage", "apikeys:manage", AdminDev),
        new("POST", "/api/v1/projects/{slug}/artifacts", "artifacts:write", "artifacts:write", AdminSec),
        new("GET", "/api/v1/projects/{slug}/artifacts", "artifacts:read", "artifacts:read", AllRoles),
        new("GET", "/api/v1/artifacts/{id:guid}", "artifacts:read", "artifacts:read", AllRoles),
        new("PATCH", "/api/v1/artifacts/{id:guid}", "artifacts:write", "artifacts:write", AdminSec),
        new("POST", "/api/v1/artifacts/{id:guid}/scans", "scans:write", "scans:write", AdminSec),
        new("GET", "/api/v1/artifacts/{id:guid}/findings", "findings:read", "findings:read", AllRoles),
        new("GET", "/api/v1/projects/{slug}/findings", "findings:read", "findings:read", AllRoles),
        new("POST", "/api/v1/findings/{id:guid}/ai-explain", "findings:read+ai:chat", "findings:read+ai:chat", AllRoles),
        new("POST", "/api/v1/projects/{slug}/finding-suppressions", "findings:suppress", "findings:suppress", AdminSec),
        new("POST", "/api/v1/policies", "policies:write", "policies:write", AdminSec),
        new("GET", "/api/v1/policies/rule-types", "policies:read", "policies:read", AllRoles),
        new("POST", "/api/v1/policies/simulate", "policies:read", "policies:read", AllRoles),
        new("GET", "/api/v1/policies", "policies:read", "policies:read", AllRoles),
        new("GET", "/api/v1/policies/{id:guid}", "policies:read", "policies:read", AllRoles),
        new("PATCH", "/api/v1/policies/{id:guid}", "policies:write", "policies:write", AdminSec),
        new("DELETE", "/api/v1/policies/{id:guid}", "policies:write", "policies:write", AdminSec),
        new("PUT", "/api/v1/policies/{id:guid}", "policies:write", "policies:write", AdminSec),
        new("POST", "/api/v1/deployments", "deployments:request", "deployments:request", AdminDev),
        new("GET", "/api/v1/deployments", "deployments:read", "deployments:read", AllRoles),
        new("GET", "/api/v1/deployments/approval-queue", "deployments:approve", "deployments:approve", Approvers),
        new("GET", "/api/v1/deployments/{id:guid}", "deployments:read", "deployments:read", AllRoles),
        new("GET", "/api/v1/deployments/{id:guid}/events", "deployments:read", "deployments:read", AllRoles),
        new("GET", "/api/v1/deployments/{id:guid}/evaluation", "deployments:read", "deployments:read", AllRoles),
        new("POST", "/api/v1/deployments/{id:guid}/ai-summary", "deployments:read+ai:chat", "deployments:read+ai:chat", AllRoles),
        new("POST", "/api/v1/deployments/{id:guid}/approvals", "deployments:approve", "deployments:approve", Approvers),
        new("POST", "/api/v1/deployments/{id:guid}/cancel", "deployments:cancel", "deployments:cancel", AdminDev),
        new("POST", "/api/v1/deployments/{id:guid}/rollback", "deployments:request", "deployments:request", AdminDev),
        new("GET", "/api/v1/admin/jobs", "users:manage", "users:manage", AdminOnly),
        new("GET", "/api/v1/admin/jobs/summary", "users:manage", "users:manage", AdminOnly),
        new("POST", "/api/v1/admin/jobs/{id:guid}/retry", "users:manage", "users:manage", AdminOnly),
        new("GET", "/api/v1/audit", "audit:read", "audit:read", AdminSec),
    ];

    // =============================================================================================
    // 1. Pure table: RolePermissions vs docs/09 for every permission x role.
    // =============================================================================================

    [Fact]
    public void DocMatrixListsExactlyThePermissionsDefinedInCode() {
        Assert.Equal(25, Permissions.All.Count);
        Assert.Equal(Permissions.All.Count, Permissions.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Permissions.All.Order(StringComparer.Ordinal), DocMatrix.Keys.Order(StringComparer.Ordinal));
    }

    public static IEnumerable<TheoryDataRow<string, string>> PermissionRoleRows() {
        foreach (var permission in DocMatrix.Keys) {
            foreach (var role in Roles) {
                yield return new TheoryDataRow<string, string>(permission, role) { TestDisplayName = $"RolePermissions: {role} x {permission}" };
            }
        }
    }

    [Theory]
    [MemberData(nameof(PermissionRoleRows))]
    public void RolePermissionsMatchDocsForEveryPermissionAndRole(string permission, string role) {
        var cell = DocMatrix[permission][Array.IndexOf(Roles, role)];
        var granted = RolePermissions.Grants([role], permission);
        switch (cell) {
            case Cell.Yes:
                Assert.True(granted, $"docs/09 grants {permission} to {role}, RolePermissions does not.");
                break;
            case Cell.No:
                Assert.False(granted, $"docs/09 denies {permission} to {role}, RolePermissions grants it.");
                break;
            default:
                // A narrowed grant ("Owner of team", "own only"...) is granted at role level; the handler narrows it.
                Assert.True(granted, $"docs/09 conditionally grants {permission} to {role}; the role must hold it so the handler can narrow it.");
                Assert.Contains(Routes, route => route.DocPermission == permission && Split(route.DocRoles).Contains(role));
                break;
        }
    }

    [Fact]
    public void RolePermissionsOnlyGrantKnownPermissionsAndCombineRolesAsAUnion() {
        foreach (var role in Roles)
            Assert.All(RolePermissions.For([role]), permission => Assert.Contains(permission, Permissions.All));
        Assert.Empty(RolePermissions.For(UnknownRole));
        Assert.Empty(RolePermissions.For([]));

        // farid@ (Security + Approver) in docs/09 section 9: union of both rows, nothing more.
        var union = RolePermissions.For([Security]).Union(RolePermissions.For([Approver]), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        Assert.Equal(union, RolePermissions.For(SecurityAndApprover));
    }

    [Fact]
    public void ApiKeyScopesAreExactlyTheDocumentedSubset() {
        Assert.Equal(DocApiKeyScopes.Order(StringComparer.Ordinal), ApiKey.AllowedScopes.Order(StringComparer.Ordinal));
        Assert.All(ApiKey.AllowedScopes, scope => Assert.Contains(scope, Permissions.All));
    }

    // =============================================================================================
    // 2. Route policies from the real Program.cs endpoint table.
    // =============================================================================================

    [Fact]
    public void RealEndpointTableMatchesTheMatrixRouteTable() {
        var actual = api.ApiEndpoints.Select(Describe).Order(StringComparer.Ordinal).ToArray();
        var expected = Routes.Select(route => $"{route.Method} {route.Pattern} => {Normalize(route.Policies)}")
            .Order(StringComparer.Ordinal).ToArray();

        var missing = expected.Except(actual, StringComparer.Ordinal).ToArray();
        var unexpected = actual.Except(expected, StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0 && unexpected.Length == 0,
            "Program.cs endpoints differ from the authorization matrix. Add new routes to Routes with their docs/09 permission.\n" +
            $"Not in Program.cs: {string.Join("; ", missing)}\nNot in matrix: {string.Join("; ", unexpected)}");
        Assert.Equal(74, actual.Length);
    }

    [Fact]
    public void EveryApiRouteRequiresAuthorizationExceptLoginAndRefresh_AndHubIsAuthorized() {
        var anonymous = api.ApiEndpoints.Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0)
            .Select(Describe).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(ExpectedAnonymousRoutes, anonymous);
        Assert.All(api.ApiEndpoints, endpoint => Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>()));

        var hub = api.AllEndpoints.Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/hubs/ops", StringComparison.Ordinal) == true).ToArray();
        Assert.NotEmpty(hub);
        Assert.All(hub, endpoint => Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));

        // Everything outside /api/v1 and /hubs is a health probe (anonymous by design).
        var other = api.AllEndpoints.Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .Where(pattern => !pattern.StartsWith("/api/v1/", StringComparison.Ordinal) && !pattern.StartsWith("/hubs/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(ExpectedNonApiPatterns, other);
    }

    [Fact]
    public void ProgramSourceGuard_EveryMapCallIsInTheMatrixAndAuthorized() {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "AegisOps.Api", "Program.cs"));

        // Only these Map* shapes are understood; anything else (MapGroup, MapMethods, MapFallback...) must extend this guard.
        var unknown = Regex.Matches(source, @"\.Map(\w+)\s*[<(]").Select(match => match.Groups[1].Value)
            .Where(name => name is not ("Get" or "Post" or "Put" or "Patch" or "Delete" or "Hub" or "HealthChecks"))
            .ToArray();
        Assert.Empty(unknown);

        var parsed = new List<string>();
        foreach (Match match in Regex.Matches(source, @"\.Map(Get|Post|Put|Patch|Delete)\(\s*""([^""]+)""")) {
            var method = match.Groups[1].Value.ToUpperInvariant();
            var pattern = match.Groups[2].Value;
            var statement = source[match.Index..source.IndexOf(';', match.Index)];
            var anonymous = Routes.Single(route => route.Method == method && route.Pattern == pattern).Policies is null;
            Assert.True(anonymous || statement.Contains(".RequireAuthorization(", StringComparison.Ordinal),
                $"{method} {pattern} has no RequireAuthorization in Program.cs.");
            parsed.Add($"{method} {pattern}");
        }

        Assert.Equal(Routes.Select(route => $"{route.Method} {route.Pattern}").Order(StringComparer.Ordinal),
            parsed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RealPolicyRegistrationMatchesRolePermissionsForEveryPermissionAndRole() {
        var failures = new List<string>();
        foreach (var permission in Permissions.All) {
            foreach (var role in Roles) {
                var allowed = await api.AllowsAsync(RealApiFixture.UserPrincipal([role]), permission);
                if (allowed != RolePermissions.Grants([role], permission)) failures.Add($"{role} x {permission}: policy={allowed}");
            }

            Assert.False(await api.AllowsAsync(new ClaimsPrincipal(new ClaimsIdentity()), permission), $"anonymous x {permission}");
            Assert.False(await api.AllowsAsync(RealApiFixture.UserPrincipal(UnknownRole), permission), $"unknown role x {permission}");
        }

        Assert.Empty(failures);
    }

    public static IEnumerable<TheoryDataRow<string, string>> AuthorizedRouteRows() =>
        Routes.Where(route => route.Policies is not null).Select(route =>
            new TheoryDataRow<string, string>(route.Method, route.Pattern) {
                TestDisplayName = $"Route reach: {route.Method} {route.Pattern}",
            });

    [Theory]
    [MemberData(nameof(AuthorizedRouteRows))]
    public async Task RoutePolicyAdmitsExactlyTheRolesDocsImply(string method, string pattern) {
        var route = Find(method, pattern);
        var reach = new List<string>();
        foreach (var role in Roles)
            if (await api.AllowsAsync(RealApiFixture.UserPrincipal([role]), route.Policies)) reach.Add(role);

        var doc = Split(route.DocRoles);
        Assert.True(doc.SequenceEqual(reach), $"{method} {pattern}: docs/09 ({route.DocPermission}) needs [{route.DocRoles}], policy admits [{string.Join(",", reach)}].");
    }

    [Fact]
    public async Task EveryRoutePolicyIsADocumentedPermissionOrPolicyAlias() {
        var names = Routes.Where(route => !string.IsNullOrEmpty(route.Policies))
            .SelectMany(route => route.Policies!.Split('+')).Distinct(StringComparer.Ordinal).ToArray();
        Assert.All(names, name => Assert.True(DocMatrix.ContainsKey(name) || DocPolicyAliases.ContainsKey(name), name));

        // docs/09 section 4: ai:policies:read is a policy (not a role permission) satisfied by ai:policies:write.
        foreach (var (alias, permission) in DocPolicyAliases)
            foreach (var role in Roles)
                Assert.Equal(RolePermissions.Grants([role], permission), await api.AllowsAsync(RealApiFixture.UserPrincipal([role]), alias));
    }

    [Fact]
    public async Task ApiKeyScopesReachOnlyTheirOwnRoutes() {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in ApiKey.AllowedScopes) {
            var key = RealApiFixture.ApiKeyPrincipal(scope);
            foreach (var route in Routes.Where(route => !string.IsNullOrEmpty(route.Policies))) {
                var allowed = await api.AllowsAsync(key, route.Policies);
                Assert.Equal(route.Policies!.Split('+').All(policy => policy == scope), allowed);
                if (allowed) reachable.Add($"{route.Method} {route.Pattern}");
            }
        }

        Assert.Equal(ExpectedApiKeyRoutes, reachable.Order(StringComparer.Ordinal));
    }

    // =============================================================================================
    // 3. HTTP matrix against the real Program.cs host with real JWTs.
    // =============================================================================================

    public static IEnumerable<TheoryDataRow<string, string, string>> HttpMatrixRows() {
        foreach (var route in Routes.Where(route => route.Policies is not null)) {
            foreach (var role in Roles.Append(Anonymous)) {
                yield return new TheoryDataRow<string, string, string>(route.Method, route.Pattern, role) {
                    TestDisplayName = $"HTTP {route.Method} {route.Pattern} as {role}",
                };
            }
        }
    }

    [Theory]
    [MemberData(nameof(HttpMatrixRows))]
    public async Task HttpMatrixEveryRouteAsEveryRole(string method, string pattern, string role) {
        var route = Find(method, pattern);
        using var response = await api.SendAsync(method, Concrete(pattern), role == Anonymous ? null : api.Bearer(role));
        var status = response.StatusCode;

        if (role == Anonymous) {
            Assert.Equal(HttpStatusCode.Unauthorized, status);
        } else if (Split(route.DocRoles).Contains(role)) {
            Assert.True(status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                $"{role} should pass authorization for {method} {pattern} ({route.DocPermission}) but got {(int)status}.");
        } else {
            // Policy-level denial: 403 Problem Details, even though the Denied audit cannot be saved (database unreachable).
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task SecurityPlusApproverTokenGetsTheUnionOverHttp() {
        var bearer = api.Bearer(SecurityAndApprover);
        using var approve = await api.SendAsync("GET", "/api/v1/deployments/approval-queue", bearer);
        using var audit = await api.SendAsync("GET", "/api/v1/audit", bearer);
        using var request = await api.SendAsync("POST", "/api/v1/deployments", bearer);
        using var users = await api.SendAsync("GET", "/api/v1/admin/users", bearer);
        Assert.NotEqual(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, audit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, request.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
    }

    [Fact]
    public async Task ForgedOrExpiredTokensAreRejected() {
        var user = User.Create("forged@example.test", "forged", DateTimeOffset.UtcNow);
        var forged = JwtAccessTokenFactory.Create(user, [Admin], new JwtOptions {
            Issuer = RealApiFixture.Jwt.Issuer, Audience = RealApiFixture.Jwt.Audience,
            SigningKey = "a-different-signing-key-that-is-long-enough-0123456789",
        }, DateTimeOffset.UtcNow);
        var expired = JwtAccessTokenFactory.Create(user, [Admin], RealApiFixture.Jwt, DateTimeOffset.UtcNow.AddHours(-2));

        using var forgedResponse = await api.SendAsync("GET", "/api/v1/admin/ping", forged);
        using var expiredResponse = await api.SendAsync("GET", "/api/v1/admin/ping", expired);
        using var adminResponse = await api.SendAsync("GET", "/api/v1/admin/ping", api.Bearer(Admin));
        Assert.Equal(HttpStatusCode.Unauthorized, forgedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, adminResponse.StatusCode);
    }

    [Fact]
    public async Task PolicyDenialReturnsProblemDetailsWithoutInternals_EvenWhenTheAuditCannotBeSaved() {
        // The real host's database is unreachable, so the Denied audit write fails; the response must stay a clean 403.
        using var response = await api.SendAsync("GET", "/api/v1/audit?secret=query-value", api.Bearer(Developer));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(403, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Forbidden.", json.RootElement.GetProperty("title").GetString());
        var body = json.RootElement.GetRawText();
        Assert.DoesNotContain("audit:read", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("query-value", body, StringComparison.Ordinal);

        using var anonymous = await api.SendAsync("GET", "/api/v1/audit", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); // challenges are unchanged
    }

    // =============================================================================================
    // 4. SQLite host: representative 2xx per permission and handler-level docs/09 rules.
    // =============================================================================================

    public static IEnumerable<TheoryDataRow<string>> RoleRows() => Roles.Select(role => new TheoryDataRow<string>(role));

    [Theory]
    [MemberData(nameof(RoleRows))]
    public async Task RepresentativeRoutesSucceedOrForbidPerDocMatrix(string role) {
        await using var host = await MatrixSqlHost.StartAsync();
        var (_, bearer) = await host.CreateUserAsync($"{role}@example.test", role);
        var (created, _) = await host.CreateUserAsync($"target-{role}@example.test", Developer);
        var team = await host.SeedTeamAsync("matrix-team");
        var expected = (string permission, HttpStatusCode success) =>
            DocMatrix[permission][Array.IndexOf(Roles, role)] == Cell.No ? HttpStatusCode.Forbidden : success;

        await AssertStatus(host, "GET", "/api/v1/teams", bearer, null, expected("teams:read", HttpStatusCode.OK));
        await AssertStatus(host, "GET", "/api/v1/projects", bearer, null, expected("projects:read", HttpStatusCode.OK));
        await AssertStatus(host, "GET", "/api/v1/policies", bearer, null, expected("policies:read", HttpStatusCode.OK));
        await AssertStatus(host, "GET", "/api/v1/audit", bearer, null, expected("audit:read", HttpStatusCode.OK));
        await AssertStatus(host, "GET", "/api/v1/admin/users", bearer, null, expected("users:manage", HttpStatusCode.OK));
        await AssertStatus(host, "POST", "/api/v1/admin/users", bearer,
            new CreateUserRequest($"new-{role}@example.test", "New user", [role], null), expected("users:manage", HttpStatusCode.Created));
        await AssertStatus(host, "PUT", $"/api/v1/admin/users/{created}/roles", bearer,
            new SetUserRolesRequest([role]), expected("roles:manage", HttpStatusCode.OK));
        await AssertStatus(host, "POST", "/api/v1/policies", bearer,
            new CreatePolicyRequest($"{role} team policy", null, "Team", team, ProductionTier, null),
            expected("policies:write", HttpStatusCode.Created));
        // Admin writes the Global AI policy, Security the Team one (docs/09: "Admin (Global + Team)", "Security (Team)").
        var aiScope = role == Admin ? PolicyScope.Global : PolicyScope.Team;
        await AssertStatus(host, "PUT", "/api/v1/ai/policies", bearer, AiPolicy(aiScope, aiScope == PolicyScope.Team ? team : null),
            expected("ai:policies:write", HttpStatusCode.OK));
    }

    [Fact]
    public async Task AnonymousIsRejectedOnEveryRepresentativeRoute() {
        await using var host = await MatrixSqlHost.StartAsync();
        foreach (var (method, path) in MatrixSqlHost.MappedRoutes)
            await AssertStatus(host, method, path, null, null, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OwnerOfTeamRules_ProjectsEnvironmentsAndApiKeys() {
        // docs/09 section 4: projects:write, environments:write and apikeys:manage = Admin, or a Developer who is Owner of the team.
        await using var host = await MatrixSqlHost.StartAsync();
        var (ownerId, owner) = await host.CreateUserAsync("owner@example.test", Developer);
        var (memberId, member) = await host.CreateUserAsync("member@example.test", Developer);
        var (_, outsider) = await host.CreateUserAsync("outsider@example.test", Developer);
        var (securityId, security) = await host.CreateUserAsync("security@example.test", Security);
        var (approverId, approver) = await host.CreateUserAsync("approver@example.test", Approver);
        var (_, admin) = await host.CreateUserAsync("admin@example.test", Admin);
        var team = await host.SeedTeamAsync("owner-team");
        await host.AddMemberAsync(team, ownerId, TeamRole.Owner);
        await host.AddMemberAsync(team, memberId, TeamRole.Member);
        await host.AddMemberAsync(team, securityId, TeamRole.Owner);
        await host.AddMemberAsync(team, approverId, TeamRole.Owner);

        // Create project: Owner of the target team or Admin; a Member/outsider gets 404, Security/Approver Owners 403.
        await AssertStatus(host, "POST", "/api/v1/projects", member, new CreateProjectRequest(team, "M", "by-member", null), HttpStatusCode.NotFound);
        await AssertStatus(host, "POST", "/api/v1/projects", outsider, new CreateProjectRequest(team, "O", "by-outsider", null), HttpStatusCode.NotFound);
        await AssertStatus(host, "POST", "/api/v1/projects", security, new CreateProjectRequest(team, "S", "by-security", null), HttpStatusCode.Forbidden);
        await AssertStatus(host, "POST", "/api/v1/projects", approver, new CreateProjectRequest(team, "A", "by-approver", null), HttpStatusCode.Forbidden);
        await AssertStatus(host, "POST", "/api/v1/projects", admin, new CreateProjectRequest(team, "Admin", "by-admin", null), HttpStatusCode.Created);
        using var created = await host.SendAsync("POST", "/api/v1/projects", owner, new CreateProjectRequest(team, "Owned", "owned", null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var project = (await created.Content.ReadFromJsonAsync<ProjectResponse>(Token))!;
        var environmentId = project.Environments[0].Id;
        Assert.Equal(2, await host.WithDbAsync(db => db.Projects.CountAsync(Token)));

        // Update project.
        foreach (var (bearer, status) in new[] {
                     (member, HttpStatusCode.NotFound), (outsider, HttpStatusCode.NotFound),
                     (security, HttpStatusCode.Forbidden), (approver, HttpStatusCode.Forbidden),
                     (owner, HttpStatusCode.OK), (admin, HttpStatusCode.OK),
                 }) {
            await AssertStatus(host, "PATCH", "/api/v1/projects/owned", bearer, new UpdateProjectRequest("Renamed", null, null), status);
            await AssertStatus(host, "PATCH", $"/api/v1/projects/owned/environments/{environmentId}", bearer,
                new UpdateEnvironmentRequest("Dev", null, null), status);
        }

        // API keys.
        var keyRequest = new CreateApiKeyRequest("ci", DeployScope, DateTimeOffset.UtcNow.AddDays(30));
        await AssertStatus(host, "POST", "/api/v1/projects/owned/api-keys", member, keyRequest, HttpStatusCode.NotFound);
        await AssertStatus(host, "POST", "/api/v1/projects/owned/api-keys", security, keyRequest, HttpStatusCode.Forbidden);
        await AssertStatus(host, "GET", "/api/v1/projects/owned/api-keys", member, null, HttpStatusCode.NotFound);
        await AssertStatus(host, "GET", "/api/v1/projects/owned/api-keys", approver, null, HttpStatusCode.Forbidden);
        await AssertStatus(host, "POST", "/api/v1/projects/owned/api-keys", owner, keyRequest, HttpStatusCode.Created);
        var keyId = await host.WithDbAsync(db => db.ApiKeys.Select(key => key.Id).SingleAsync(Token));
        await AssertStatus(host, "DELETE", $"/api/v1/projects/owned/api-keys/{keyId}", member, null, HttpStatusCode.NotFound);
        await AssertStatus(host, "DELETE", $"/api/v1/projects/owned/api-keys/{keyId}", outsider, null, HttpStatusCode.NotFound);
        await AssertStatus(host, "DELETE", $"/api/v1/projects/owned/api-keys/{keyId}", owner, null, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task SecuritySeesAllProjectsReadOnly() {
        await using var host = await MatrixSqlHost.StartAsync();
        var (_, bearer) = await host.CreateUserAsync("security@example.test", Security);
        var team = await host.SeedTeamAsync("other-team");
        await host.SeedProjectAsync(team, "other-project");
        using var list = await host.SendAsync("GET", "/api/v1/projects", bearer);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains("other-project", await list.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        await AssertStatus(host, "GET", "/api/v1/projects/other-project", bearer, null, HttpStatusCode.OK);
        // Read-only: no write access to projects outside (or inside) a team.
        await AssertStatus(host, "PATCH", "/api/v1/projects/other-project", bearer, new UpdateProjectRequest("x", null, null), HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(Developer)]
    [InlineData(Approver)]
    public async Task NonMemberDeveloperOrApproverCannotSeeOtherTeamsProjects(string role) {
        await using var host = await MatrixSqlHost.StartAsync();
        var (_, bearer) = await host.CreateUserAsync("user@example.test", role);
        var (_, admin) = await host.CreateUserAsync("admin@example.test", Admin);
        var team = await host.SeedTeamAsync("other-team");
        await host.SeedProjectAsync(team, "other-project");
        using var list = await host.SendAsync("GET", "/api/v1/projects", bearer);
        Assert.DoesNotContain("other-project", await list.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        await AssertStatus(host, "GET", "/api/v1/projects/other-project", bearer, null, HttpStatusCode.NotFound);
        await AssertStatus(host, "GET", "/api/v1/projects/other-project", admin, null, HttpStatusCode.OK);
    }

    [Fact]
    public async Task AiPolicyScopes_AdminGlobalAndTeam_SecurityTeamOnly() {
        await using var host = await MatrixSqlHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", Admin);
        var (_, security) = await host.CreateUserAsync("security@example.test", Security);
        var (_, developer) = await host.CreateUserAsync("dev@example.test", Developer);
        var adminTeam = await host.SeedTeamAsync("ai-admin-team");
        var securityTeam = await host.SeedTeamAsync("ai-security-team");
        await AssertStatus(host, "PUT", "/api/v1/ai/policies", admin, AiPolicy(PolicyScope.Team, adminTeam), HttpStatusCode.OK);
        await AssertStatus(host, "PUT", "/api/v1/ai/policies", admin, AiPolicy(PolicyScope.Global, null), HttpStatusCode.OK);
        await AssertStatus(host, "PUT", "/api/v1/ai/policies", security, AiPolicy(PolicyScope.Team, securityTeam), HttpStatusCode.OK);
        await AssertStatus(host, "PUT", "/api/v1/ai/policies", security, AiPolicy(PolicyScope.Global, null), HttpStatusCode.Forbidden);
        await AssertStatus(host, "PUT", "/api/v1/ai/policies", developer, AiPolicy(PolicyScope.Team, securityTeam), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PolicyEditRules_GlobalAdminOnly_TeamSecurity_ProjectNeedsTeamAccess() {
        // docs/09 section 5 EditPolicyHandler, enforced by CreatePolicy, SetPolicyEnabled, ArchivePolicy and Replace.
        await using var host = await MatrixSqlHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", Admin);
        var (securityId, security) = await host.CreateUserAsync("security@example.test", Security);
        var memberTeam = await host.SeedTeamAsync("security-team");
        var otherTeam = await host.SeedTeamAsync("other-team");
        var memberProject = await host.SeedProjectAsync(memberTeam, "member-project");
        var otherProject = await host.SeedProjectAsync(otherTeam, "other-project");
        await host.AddMemberAsync(memberTeam, securityId, TeamRole.Member);

        await AssertStatus(host, "POST", "/api/v1/policies", security, Policy("Global", null), HttpStatusCode.Forbidden);
        await AssertStatus(host, "POST", "/api/v1/policies", security, Policy("Project", otherProject), HttpStatusCode.Forbidden);
        await AssertStatus(host, "POST", "/api/v1/policies", security, Policy("Project", memberProject), HttpStatusCode.Created);
        await AssertStatus(host, "POST", "/api/v1/policies", security, Policy("Team", otherTeam), HttpStatusCode.Created);
        await AssertStatus(host, "POST", "/api/v1/policies", admin, Policy("Project", otherProject), HttpStatusCode.Created);
        var global = await host.CreatePolicyAsync(admin, Policy("Global", null));
        var foreignProject = await host.WithDbAsync(db => db.Policies.Where(item => item.ScopeId == otherProject).Select(item => item.Id).SingleAsync(Token));
        var teamPolicy = await host.WithDbAsync(db => db.Policies.Where(item => item.ScopeId == otherTeam).Select(item => item.Id).SingleAsync(Token));
        Assert.Equal(4, await host.WithDbAsync(db => db.Policies.CountAsync(Token)));

        // Security may not mutate the Global policy or a Project policy outside its teams, by any route.
        foreach (var id in new[] { global, foreignProject }) {
            await AssertStatus(host, "PATCH", $"/api/v1/policies/{id}", security, new SetPolicyEnabledRequest(false), HttpStatusCode.Forbidden);
            await AssertStatus(host, "PUT", $"/api/v1/policies/{id}", security, Policy("Global", null), HttpStatusCode.Forbidden);
            await AssertStatus(host, "DELETE", $"/api/v1/policies/{id}", security, null, HttpStatusCode.Forbidden);
        }

        Assert.True(await host.WithDbAsync(db => db.Policies.AllAsync(item => item.IsEnabled && item.ArchivedAt == null, Token)));
        await AssertStatus(host, "PATCH", $"/api/v1/policies/{teamPolicy}", security, new SetPolicyEnabledRequest(false), HttpStatusCode.OK);
        await AssertStatus(host, "PATCH", $"/api/v1/policies/{global}", admin, new SetPolicyEnabledRequest(false), HttpStatusCode.OK);
        await AssertStatus(host, "DELETE", $"/api/v1/policies/{foreignProject}", admin, null, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeniedAuthorizationWritesOneDeniedAuditEventWithoutRequestData() {
        await using var host = await MatrixSqlHost.StartAsync();
        var (developerId, bearer) = await host.CreateUserAsync("dev@example.test", Developer);
        using (var anonymous = await host.SendAsync("GET", "/api/v1/audit", null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, await host.WithDbAsync(db => db.AuditEvents.CountAsync(Token))); // challenges are not audited

        using var response = await host.SendAsync("GET", "/api/v1/audit?token=query-secret", bearer);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var audit = Assert.Single(await host.WithDbAsync(db => db.AuditEvents.AsNoTracking().ToListAsync(Token)));
        Assert.Equal(AuditingAuthorizationResultHandler.DeniedAction, audit.Action);
        Assert.Equal("Denied", audit.Outcome);
        Assert.Equal("Endpoint", audit.ResourceType);
        Assert.Equal("User", audit.ActorType);
        Assert.Equal(developerId, audit.ActorId);
        Assert.DoesNotContain("query-secret", $"{audit.ActorDisplay}|{audit.ResourceType}|{audit.CorrelationId}", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static RouteRule Find(string method, string pattern) =>
        Routes.Single(route => route.Method == method && route.Pattern == pattern);

    private static string[] Split(string roles) => roles.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static string Normalize(string? policies) => policies switch {
        null => "<anonymous>",
        "" => "<authenticated>",
        _ => string.Join("+", policies.Split('+').Order(StringComparer.Ordinal)),
    };

    private static string Describe(RouteEndpoint endpoint) {
        var method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Single() ?? "*";
        var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        var policies = authorize.Count == 0 ? null
            : string.Join("+", authorize.Select(item => item.Policy).Where(policy => policy is not null));
        return $"{method} {endpoint.RoutePattern.RawText} => {Normalize(policies)}";
    }

    private static string Concrete(string pattern) {
        var withGuids = Regex.Replace(pattern, @"\{[^}]+:guid\}", _ => Guid.NewGuid().ToString());
        return Regex.Replace(withGuids, @"\{[^}]+\}", "matrix-slug");
    }

    private static CreatePolicyRequest Policy(string scope, Guid? scopeId) =>
        new($"{scope} policy {Guid.NewGuid():N}", null, scope, scopeId, ProductionTier, null);

    private static AiPolicyRequest AiPolicy(PolicyScope scope, Guid? scopeId) =>
        new(null, $"{scope} policy", scope, scopeId, [], 20, 100, 4000, SensitiveDataAction.Redact, [], true, false, true);

    private static async Task AssertStatus(MatrixSqlHost host, string method, string path, string? bearer, object? body, HttpStatusCode expected) {
        using var response = await host.SendAsync(method, path, bearer, body);
        Assert.True(expected == response.StatusCode,
            $"{method} {path}: expected {(int)expected}, got {(int)response.StatusCode}. {await response.Content.ReadAsStringAsync(Token)}");
    }

    private static string RepoRoot() {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AegisOps.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Repository root (AegisOps.slnx) not found.");
    }

    /// <summary>
    /// Runs the real src/AegisOps.Api/Program.cs entry point (Production environment, Kestrel on a random loopback port,
    /// Postgres pointed at a closed port, no Redis) and captures its host through the hosting DiagnosticListener,
    /// the same technique WebApplicationFactory uses.
    /// </summary>
    public sealed class RealApiFixture : IAsyncLifetime {
        internal static readonly JwtOptions Jwt = new() {
            Issuer = "aegisops-matrix-tests",
            Audience = "aegisops-matrix-tests",
            SigningKey = "authorization-matrix-tests-signing-key-0123456789",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7,
        };

        private static readonly AsyncLocal<bool> Capturing = new();
        private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
        private Task? _run;
        private IHost? _host;
        private HttpClient? _client;

        internal IReadOnlyList<RouteEndpoint> AllEndpoints { get; private set; } = [];
        internal IReadOnlyList<RouteEndpoint> ApiEndpoints { get; private set; } = [];

        public async ValueTask InitializeAsync() {
            string[] args = [
                "--environment=Production",
                "--urls=http://127.0.0.1:0",
                $"--Jwt:Issuer={Jwt.Issuer}",
                $"--Jwt:Audience={Jwt.Audience}",
                $"--Jwt:SigningKey={Jwt.SigningKey}",
                "--ConnectionStrings:Postgres=Host=127.0.0.1;Port=1;Database=aegisops_matrix;Username=matrix;Timeout=2",
                "--ConnectionStrings:Redis=",
                "--OTEL_EXPORTER_OTLP_ENDPOINT=",
                "--Ai:Ollama:BaseUrl=http://127.0.0.1:1/",
                "--Ai:Ollama:TimeoutSeconds=2",
                "--Worker:HealthBaseUrl=http://127.0.0.1:1/",
                "--Database:MigrateOnStartup=false",
                "--Logging:LogLevel:Default=None",
                "--Serilog:MinimumLevel:Default=Fatal",
            ];
            var built = new TaskCompletionSource<IHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (DiagnosticListener.AllListeners.Subscribe(new HostingObserver(built))) {
                var entry = typeof(Login).Assembly.EntryPoint ?? throw new InvalidOperationException("AegisOps.Api has no entry point.");
                _run = Task.Run(() => {
                    Capturing.Value = true;
                    try {
                        var result = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : [args]);
                        if (result is Task task) task.GetAwaiter().GetResult();
                    } catch (TargetInvocationException exception) when (exception.InnerException is not null) {
                        built.TrySetException(exception.InnerException);
                        ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    }
                });
                if (await Task.WhenAny(built.Task, _run).WaitAsync(TimeSpan.FromSeconds(60), Token) == _run) await _run;
                _host = await built.Task;
            }

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = _host.Services.GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStarted.Register(() => started.TrySetResult());
            if (await Task.WhenAny(started.Task, _run).WaitAsync(TimeSpan.FromSeconds(60), Token) == _run) {
                await _run;
                throw new InvalidOperationException("AegisOps.Api exited before it started.");
            }

            var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            _client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
            AllEndpoints = _host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
            ApiEndpoints = AllEndpoints.Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true).ToArray();
        }

        internal static ClaimsPrincipal UserPrincipal(IEnumerable<string> roles) {
            var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()) };
            claims.AddRange(roles.Select(role => new Claim("role", role)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", JwtRegisteredClaimNames.Name, "role"));
        }

        internal static ClaimsPrincipal ApiKeyPrincipal(string scope) => new(new ClaimsIdentity([
            new Claim(JwtRegisteredClaimNames.Sub, $"apikey:{Guid.NewGuid()}"),
            new Claim("project", Guid.NewGuid().ToString()),
            new Claim("actor_type", "ApiKey"),
            new Claim("scope", scope),
        ], "ApiKey"));

        /// <summary>Evaluates the route's policies with the real host's IAuthorizationService ("" = default policy).</summary>
        internal async Task<bool> AllowsAsync(ClaimsPrincipal principal, string? policies) {
            if (policies is null) return true;
            var authorization = _host!.Services.GetRequiredService<IAuthorizationService>();
            if (policies.Length == 0) {
                var provider = _host.Services.GetRequiredService<IAuthorizationPolicyProvider>();
                return (await authorization.AuthorizeAsync(principal, null, await provider.GetDefaultPolicyAsync())).Succeeded;
            }

            foreach (var policy in policies.Split('+'))
                if (!(await authorization.AuthorizeAsync(principal, null, policy)).Succeeded) return false;
            return true;
        }

        internal string Bearer(string role) => Bearer([role]);

        internal string Bearer(IReadOnlyList<string> roles) {
            var key = string.Join(",", roles);
            lock (_tokens) {
                if (!_tokens.TryGetValue(key, out var token)) {
                    var user = User.Create($"{key.Replace(',', '-')}@example.test", key, DateTimeOffset.UtcNow);
                    token = JwtAccessTokenFactory.Create(user, roles, Jwt, DateTimeOffset.UtcNow);
                    _tokens[key] = token;
                }

                return token;
            }
        }

        internal Task<HttpResponseMessage> SendAsync(string method, string path, string? bearer) {
            var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (method is "POST" or "PUT" or "PATCH") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            return _client!.SendAsync(request, Token);
        }

        public async ValueTask DisposeAsync() {
            _client?.Dispose();
            if (_host is not null) _host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            if (_run is not null) await _run.WaitAsync(TimeSpan.FromSeconds(30));
        }

        private sealed class HostingObserver(TaskCompletionSource<IHost> built) : IObserver<DiagnosticListener> {
            private readonly List<IDisposable> _subscriptions = [];

            public void OnNext(DiagnosticListener value) {
                if (value.Name != "Microsoft.Extensions.Hosting") return;
                lock (_subscriptions) _subscriptions.Add(value.Subscribe(new EventObserver(built)));
            }

            public void OnCompleted() { }
            public void OnError(Exception error) { }
        }

        private sealed class EventObserver(TaskCompletionSource<IHost> built) : IObserver<KeyValuePair<string, object?>> {
            public void OnNext(KeyValuePair<string, object?> value) {
                if (Capturing.Value && value.Key == "HostBuilt" && value.Value is IHost host) built.TrySetResult(host);
            }

            public void OnCompleted() { }
            public void OnError(Exception error) { }
        }
    }

    /// <summary>SQLite + Identity + real JWT/permission policies, with the Program.cs routes needed for representative calls.</summary>
    private sealed class MatrixSqlHost : IAsyncDisposable {
        private static readonly JwtOptions Jwt = new() {
            Issuer = "aegisops-tests",
            Audience = "aegisops-tests",
            SigningKey = "authorization-matrix-sqlite-signing-key-0123456789",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7,
        };

        internal static readonly (string Method, string Path)[] MappedRoutes = [
            ("GET", "/api/v1/teams"), ("GET", "/api/v1/projects"), ("GET", "/api/v1/projects/any"),
            ("PATCH", "/api/v1/projects/any"), ("POST", "/api/v1/projects"), ("GET", "/api/v1/policies"), ("POST", "/api/v1/policies"),
            ("PUT", "/api/v1/ai/policies"), ("GET", "/api/v1/audit"), ("GET", "/api/v1/admin/users"), ("POST", "/api/v1/admin/users"),
            ("PUT", $"/api/v1/admin/users/{Guid.Empty}/roles"),
        ];

        private readonly WebApplication _app;
        private readonly SqliteConnection _connection;
        private readonly HttpClient _client;

        private MatrixSqlHost(WebApplication app, SqliteConnection connection) {
            _app = app;
            _connection = connection;
            _client = app.GetTestClient();
        }

        public static async Task<MatrixSqlHost> StartAsync() {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(Token);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
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
            app.MapGet("/api/v1/projects", (ClaimsPrincipal user, UserManager<User> users, AegisOpsDbContext db, HttpContext http) =>
                ListProjects.Handle(user, users, db, http))
                .RequireAuthorization(Permissions.ProjectsRead);
            app.MapGet("/api/v1/projects/{slug}", (string slug, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, HttpContext http) => GetProject.Handle(slug, user, users, db, http))
                .RequireAuthorization(Permissions.ProjectsRead);
            app.MapPatch("/api/v1/projects/{slug}", (string slug, UpdateProjectRequest body, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, IAuditWriter audit, HttpContext http) => UpdateProject.Handle(slug, body, user, users, db, audit, http))
                .RequireAuthorization(Permissions.ProjectsWrite);
            app.MapPost("/api/v1/projects", (CreateProjectRequest body, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                CreateProject.Handle(body, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.ProjectsWrite);
            app.MapPatch("/api/v1/projects/{slug}/environments/{envId:guid}", (string slug, Guid envId, UpdateEnvironmentRequest body,
                ClaimsPrincipal user, UserManager<User> users, AegisOpsDbContext db, IAuditWriter audit, HttpContext http) =>
                UpdateEnvironment.Handle(slug, envId, body, user, users, db, audit, http))
                .RequireAuthorization(Permissions.EnvironmentsWrite);
            app.MapPost("/api/v1/projects/{slug}/api-keys", (string slug, CreateApiKeyRequest body, ClaimsPrincipal user,
                UserManager<User> users, AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                CreateApiKey.Handle(slug, body, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.ApiKeysManage);
            app.MapGet("/api/v1/projects/{slug}/api-keys", (string slug, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, HttpContext http) => ListApiKeys.Handle(slug, user, users, db, http))
                .RequireAuthorization(Permissions.ApiKeysManage);
            app.MapDelete("/api/v1/projects/{slug}/api-keys/{keyId:guid}", (string slug, Guid keyId, ClaimsPrincipal user,
                UserManager<User> users, AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                RevokeApiKey.Handle(slug, keyId, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.ApiKeysManage);
            app.MapPatch("/api/v1/policies/{id:guid}", (Guid id, SetPolicyEnabledRequest body, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                SetPolicyEnabled.Handle(id, body, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.PoliciesWrite);
            app.MapDelete("/api/v1/policies/{id:guid}", (Guid id, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                ArchivePolicy.Handle(id, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.PoliciesWrite);
            app.MapPut("/api/v1/policies/{id:guid}", (Guid id, CreatePolicyRequest body, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                PolicyPhase1Endpoints.Replace(id, body, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.PoliciesWrite);
            app.MapGet("/api/v1/policies", (AegisOpsDbContext db, HttpContext http) => ListPolicies.Handle(db, http))
                .RequireAuthorization(Permissions.PoliciesRead);
            app.MapPost("/api/v1/policies", (CreatePolicyRequest body, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                CreatePolicy.Handle(body, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.PoliciesWrite);
            app.MapPut("/api/v1/ai/policies", (AiPolicyRequest body, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, IAuditWriter audit, HttpContext http) => AiEndpoints.SavePolicy(body, user, users, db, audit, http))
                .RequireAuthorization(Permissions.AiPoliciesWrite);
            app.MapGet("/api/v1/audit", (AegisOpsDbContext db, HttpContext http) => PolicyPhase1Endpoints.Audit(db, http))
                .RequireAuthorization(Permissions.AuditRead);
            app.MapGet("/api/v1/admin/users", (UserManager<User> users) => ListUsers.Handle(users))
                .RequireAuthorization(Permissions.UsersManage);
            app.MapPost("/api/v1/admin/users", (CreateUserRequest body, ClaimsPrincipal user, UserManager<User> users,
                AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                CreateUser.Handle(body, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.UsersManage);
            app.MapPut("/api/v1/admin/users/{id:guid}/roles", (Guid id, SetUserRolesRequest body, ClaimsPrincipal user,
                UserManager<User> users, AegisOpsDbContext db, IAuditWriter audit, TimeProvider time, HttpContext http) =>
                SetUserRoles.Handle(id, body, user, users, db, audit, time, http))
                .RequireAuthorization(Permissions.RolesManage);
            await app.StartAsync(Token);

            var host = new MatrixSqlHost(app, connection);
            await host.WithDbAsync(db => db.Database.EnsureCreatedAsync(Token));
            return host;
        }

        public async Task<(Guid Id, string Bearer)> CreateUserAsync(string email, string role) {
            await using var scope = _app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync(role))
                Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() })).Succeeded);
            var user = User.Create(email, email.Split('@')[0], DateTimeOffset.UtcNow);
            Assert.True((await users.CreateAsync(user)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            return (user.Id, JwtAccessTokenFactory.Create(user, [role], Jwt, DateTimeOffset.UtcNow));
        }

        public Task<Guid> SeedTeamAsync(string slug) => WithDbAsync(async db => {
            var team = Team.Create(slug, slug, DateTimeOffset.UtcNow);
            db.Teams.Add(team);
            await db.SaveChangesAsync(Token);
            return team.Id;
        });

        public Task<Guid> SeedProjectAsync(Guid teamId, string slug) => WithDbAsync(async db => {
            var project = Project.Create(teamId, slug, slug, DateTimeOffset.UtcNow);
            db.Projects.Add(project);
            await db.SaveChangesAsync(Token);
            return project.Id;
        });

        public async Task<Guid> CreatePolicyAsync(string bearer, CreatePolicyRequest request) {
            using var response = await SendAsync("POST", "/api/v1/policies", bearer, request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<PolicyResponse>(Token))!.Id;
        }

        public Task AddMemberAsync(Guid teamId, Guid userId, TeamRole role) => WithDbAsync(async db => {
            db.TeamMembers.Add(TeamMember.Create(teamId, userId, role, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Token);
        });

        public Task<HttpResponseMessage> SendAsync(string method, string path, string? bearer, object? body = null) {
            var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
            else if (method is "POST" or "PUT" or "PATCH") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
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
            modelBuilder.Entity<AiRequest>().Property(item => item.CreatedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<AuditEvent>().Property(item => item.Timestamp).HasConversion(new DateTimeOffsetToBinaryConverter());
        }
    }
}
