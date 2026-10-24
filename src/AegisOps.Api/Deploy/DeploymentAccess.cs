using System.Security.Claims;
using System.Text.Json;
using AegisOps.Application.Authorization;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Deploy;

// Scope in SQL before selecting rows, events, evaluations, or inspecting deployment state.
internal sealed class DeploymentAccess {
    private readonly Guid? userId;
    private readonly Guid? keyProjectId;
    private readonly string[] roles;
    private readonly string[] scopes;
    public IResult? Error { get; }

    private DeploymentAccess(Guid? userId, Guid? keyProjectId, string[] roles, string[] scopes, IResult? error = null) {
        this.userId = userId;
        this.keyProjectId = keyProjectId;
        this.roles = roles;
        this.scopes = scopes;
        Error = error;
    }

    private bool Admin => roles.Contains("Admin", StringComparer.Ordinal);
    private bool Security => roles.Contains("Security", StringComparer.Ordinal);
    private bool Permitted(string permission) => userId is not null
        ? RolePermissions.Grants(roles, permission)
        : scopes.Contains(permission, StringComparer.Ordinal);

    public static async Task<DeploymentAccess> ResolveAsync(ClaimsPrincipal principal, UserManager<User> users) {
        if (string.Equals(principal.FindFirst("actor_type")?.Value, "apiKey", StringComparison.OrdinalIgnoreCase)) {
            var project = principal.FindFirst("project")?.Value;
            var subject = principal.FindFirst("sub")?.Value;
            if (!Guid.TryParse(project, out var projectId) || subject is null ||
                !subject.StartsWith("apikey:", StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(subject["apikey:".Length..], out _))
                return new(null, null, [], [], Invalid());
            return new(null, projectId, [], principal.FindAll("scope").Select(item => item.Value).ToArray());
        }

        if (!Guid.TryParse(principal.FindFirst("sub")?.Value, out var id)) return new(null, null, [], [], Invalid());
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null || !user.IsActive) return new(null, null, [], [], Invalid());
        return new(id, null, (await users.GetRolesAsync(user)).ToArray(), []);
    }

    public IQueryable<Deployment> Scope(IQueryable<Deployment> query, AegisOpsDbContext db) {
        if (!Permitted(Permissions.DeploymentsRead)) return query.Where(item => false);
        if (keyProjectId is Guid project) return query.Where(item => item.ProjectId == project);
        if (Admin || Security) return query;
        var teams = db.TeamMembers.AsNoTracking().Where(item => item.UserId == userId).Select(item => item.TeamId);
        var projects = db.Projects.AsNoTracking().Where(item => teams.Contains(item.TeamId)).Select(item => item.Id);
        return query.Where(item => projects.Contains(item.ProjectId));
    }

    public async Task<bool> CanSeeAsync(Guid projectId, AegisOpsDbContext db, CancellationToken token) {
        if (!Permitted(Permissions.DeploymentsRead)) return false;
        if (keyProjectId is Guid keyProject) return keyProject == projectId;
        return Admin || Security || await db.Projects.AsNoTracking().AnyAsync(project => project.Id == projectId &&
            db.TeamMembers.Any(member => member.TeamId == project.TeamId && member.UserId == userId), token);
    }

    public async Task<bool> CanApproveAsync(Deployment deployment, AegisOpsDbContext db, CancellationToken token) {
        if (userId is null || !Permitted(Permissions.DeploymentsApprove)) return false;
        if (!Admin && !await MemberAsync(deployment.ProjectId, db, null, token)) return false;

        // Rule results snapshot the roles at evaluation time; editing a policy cannot relax an existing approval.
        if (deployment.PolicyEvaluationId is not Guid evaluationId) return false;
        var results = await db.PolicyEvaluations.AsNoTracking().Where(item => item.Id == evaluationId && item.DeploymentId == deployment.Id)
            .Select(item => item.RuleResults).SingleOrDefaultAsync(token);
        if (results is null) return false;
        using var document = JsonDocument.Parse(results);
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in document.RootElement.EnumerateArray()) {
            if (!rule.TryGetProperty("Type", out var type) || type.GetString() != "RequireApprovals" ||
                !rule.TryGetProperty("RequiredRoles", out var roleList) || roleList.ValueKind != JsonValueKind.Array) continue;
            foreach (var role in roleList.EnumerateArray()) {
                if (role.ValueKind == JsonValueKind.String && role.GetString() is string name) required.Add(name);
            }
        }
        return required.Count == 0 || roles.Any(required.Contains);
    }

    public async Task<bool> CanCancelAsync(Deployment deployment, AegisOpsDbContext db, CancellationToken token) {
        if (userId is null || !Permitted(Permissions.DeploymentsCancel)) return false;
        return Admin || deployment.RequestedById == userId || await MemberAsync(deployment.ProjectId, db, TeamRole.Owner, token);
    }

    private Task<bool> MemberAsync(Guid projectId, AegisOpsDbContext db, TeamRole? role, CancellationToken token) =>
        db.Projects.AsNoTracking().AnyAsync(project => project.Id == projectId &&
            db.TeamMembers.Any(member => member.TeamId == project.TeamId && member.UserId == userId && (role == null || member.Role == role)), token);

    private static IResult Invalid() => Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
    public static IResult NotFound() => Results.Problem(title: "Deployment was not found.", statusCode: StatusCodes.Status404NotFound);
    public static IResult Forbidden() => Results.Problem(title: "Deployment action is not permitted.", statusCode: StatusCodes.Status403Forbidden);
}
