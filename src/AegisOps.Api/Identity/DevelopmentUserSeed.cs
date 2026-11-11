using System.Security.Cryptography;
using AegisOps.Domain.Ai;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using Microsoft.AspNetCore.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Identity;

public static partial class DevelopmentUserSeed {
    public const string Email = "admin@aegisops.local";
    public const string AdminRole = "Admin";
    public const string DeveloperRole = "Developer";
    public const string SecurityRole = "Security";
    public const string ApproverRole = "Approver";

    public static async Task SeedAsync(IServiceProvider services) {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var logger = scope.ServiceProvider 
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(DevelopmentUserSeed));
        
        foreach (var roleName in new[] {AdminRole, DeveloperRole, SecurityRole, ApproverRole}) {
            await EnsureRoleAsync(roles, roleName);
        }

        var now = time.GetUtcNow();
        foreach (var account in Accounts) {
            var user = await users.FindByEmailAsync(account.Email);
            if (user is null) {
                    var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
                    user = User.Create(account.Email, account.DisplayName, now);
                    ThrowIfFailed(await users.CreateAsync(user, password));
                    LogCreated(logger, account.Email, password);
            }

            foreach (var roleName in account.Roles) {
                if (!await users.IsInRoleAsync(user, roleName)) {
                    ThrowIfFailed(await users.AddToRoleAsync(user, roleName));
                }
            }
        }

        var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
        var teamId = await EnsureDemoTeamAsync(db, users, now);
        await EnsureDemoProjectsAsync(db, teamId, now);
        await EnsureProductionApprovalsAsync(db, users, now);
        await EnsureScanPoliciesAsync(db, users, now);
        await EnsureCircuitStagingTargetAsync(db);
        await EnsureSiteStagingTargetAsync(db);
        await EnsureAiDefaultsAsync(db, users, now);
    }

    private static async Task EnsureAiDefaultsAsync(AegisOpsDbContext db, UserManager<User> users, DateTimeOffset now) {
        var admin = await users.FindByEmailAsync(Email);
        if (admin is null) return;
        var model = await db.AiModels.SingleOrDefaultAsync(item => item.Name == "llama3.2:3b");
        if (model is null) {
            model = AiModel.Create("llama3.2:3b");
            model.SetEnabled(true);
            db.AiModels.Add(model);
        }
        var policy = await db.AiPolicies.SingleOrDefaultAsync(item => item.Scope == PolicyScope.Global && item.IsEnabled);
        if (policy is null) {
            policy = AiPolicy.Create("Global baseline", PolicyScope.Global, null, admin.Id, now);
            policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Redact, [], true, false, true, admin.Id, now);
            db.AiPolicies.Add(policy);
        }
        await db.SaveChangesAsync();
    }

    private sealed record DemoAccount(string Email, string DisplayName, string[] Roles);
    private static readonly DemoAccount[] Accounts = [
        new(Email, "AegisOps Admin", [AdminRole]),
        new("developer@aegisops.local", "AegisOps Developer", [DeveloperRole]),
        new("rith@aegisops.local", "Rith", [DeveloperRole]),
        new("security@aegisops.local", "AegisOps Security", [SecurityRole, ApproverRole]),
        new("approver@aegisops.local", "AegisOps Approver", [ApproverRole]),
    ];

    private static async Task<Guid> EnsureDemoTeamAsync(
        AegisOpsDbContext db,
        UserManager<User> users,
        DateTimeOffset now
    ) {
        const string slug = "network-platform";
        var team = await db.Teams.SingleOrDefaultAsync(item => item.Slug == slug);
        if (team is null) {
            team = Team.Create(
                "Network Platform",
                slug,
                now,
                "Demo team for network services"
            );
            db.Teams.Add(team);
            await db.SaveChangesAsync();
        }

        await EnsureMemberAsync(db, users, team.Id, "developer@aegisops.local", TeamRole.Owner, now);
        await EnsureMemberAsync(db, users, team.Id, "rith@aegisops.local", TeamRole.Owner, now);
        // Approvers must belong to the project's team (docs/09), so Journey A works from a fresh clone.
        await EnsureMemberAsync(db, users, team.Id, "security@aegisops.local", TeamRole.Member, now);
        await EnsureMemberAsync(db, users, team.Id, "approver@aegisops.local", TeamRole.Member, now);
        return team.Id;
    }

    /// <summary>
    /// The three target services of docs/13 with the usual Development/Staging/Production environments, so the
    /// demo journeys (docs/demo-script.md) run from a fresh clone. A slug that already exists anywhere is left
    /// alone, whichever team owns it.
    /// </summary>
    private static async Task EnsureDemoProjectsAsync(AegisOpsDbContext db, Guid teamId, DateTimeOffset now) {
        (string Slug, string Name, string Description)[] projects = [
            ("circuit-service", "Circuit Service", "Demo target: circuit inventory API"),
            ("site-service", "Site Service", "Demo target: site and device inventory API"),
            ("notification-service", "Notification Service", "Demo target: outbound notifications API"),
        ];
        foreach (var (slug, name, description) in projects) {
            if (await db.Projects.AnyAsync(item => item.Slug == slug)) {
                continue;
            }

            var project = Project.Create(teamId, name, slug, now, description);
            db.Projects.Add(project);
            db.Environments.AddRange(
                AegisOps.Domain.Organization.Environment.Create(project.Id, "Development", EnvironmentTier.Development, 0),
                AegisOps.Domain.Organization.Environment.Create(project.Id, "Staging", EnvironmentTier.Staging, 1),
                AegisOps.Domain.Organization.Environment.Create(project.Id, "Production", EnvironmentTier.Production, 2));
            await db.SaveChangesAsync();
        }
    }

    private static async Task EnsureMemberAsync(
        AegisOpsDbContext db,
        UserManager<User> users,
        Guid teamId,
        string email,
        TeamRole role,
        DateTimeOffset joinedAt
    ) {
        var user = await users.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"Seed user {email} is missing.");

        var exists = await db.TeamMembers.AnyAsync(member => member.TeamId == teamId && member.UserId == user.Id);
        if (exists) {
            return;
        }

        db.TeamMembers.Add(TeamMember.Create(teamId, user.Id, role, joinedAt));
        await db.SaveChangesAsync();
    }

    private static async Task EnsureProductionApprovalsAsync(
        AegisOpsDbContext db,
        UserManager<User> users,
        DateTimeOffset now
    ) {
        var admin = await users.FindByEmailAsync(Email);
        if (admin is null) {
            return;
        }

        var policy = await db.Policies.SingleOrDefaultAsync(item =>
            item.Name == "Production baseline" && item.ArchivedAt == null);
        if (policy is null) {
            policy = Policy.Create(
                "Production baseline",
                PolicyScope.Global,
                [EnvironmentTier.Production],
                admin.Id,
                now,
                description: "Tests must pass, and production needs two approvals.");
            db.Policies.Add(policy);
            db.PolicyRules.Add(PolicyRule.Create(
                policy.Id,
                PolicyRuleType.RequireTestsPassed,
                RuleEffect.Deny,
                0,
                "{}"));
            db.PolicyRules.Add(PolicyRule.Create(
                policy.Id,
                PolicyRuleType.RequireApprovals,
                RuleEffect.RequireApproval,
                1,
                """{"count":2}"""));
            await db.SaveChangesAsync();
            return;
        }

        var hasApprovals = await db.PolicyRules.AnyAsync(rule =>
            rule.PolicyId == policy.Id && rule.Type == PolicyRuleType.RequireApprovals);
        if (hasApprovals) {
            return;
        }

        db.PolicyRules.Add(PolicyRule.Create(
            policy.Id,
            PolicyRuleType.RequireApprovals,
            RuleEffect.RequireApproval,
            1,
            """{"count":2}"""));
        policy.SetTiers(policy.AppliesToTiers, admin.Id, now);
        await db.SaveChangesAsync();
    }

    private static async Task EnsureScanPoliciesAsync(
        AegisOpsDbContext db,
        UserManager<User> users,
        DateTimeOffset now
    ) {
        var admin = await users.FindByEmailAsync(Email);
        if (admin is null) {
            return;
        }

        if (!await db.Policies.AnyAsync(item => item.Name == "Global baseline" && item.ArchivedAt == null)) {
            var baseline = Policy.Create(
                "Global baseline",
                PolicyScope.Global,
                [EnvironmentTier.Development, EnvironmentTier.Staging, EnvironmentTier.Production],
                admin.Id,
                now,
                description: "Tests must pass, and any critical finding is denied.");
            db.Policies.Add(baseline);
            db.PolicyRules.Add(PolicyRule.Create(baseline.Id, PolicyRuleType.RequireTestsPassed, RuleEffect.Deny, 0, "{}"));
            db.PolicyRules.Add(PolicyRule.Create(baseline.Id, PolicyRuleType.MaxFindings, RuleEffect.Deny, 1, """{"severity":"Critical","max":0}"""));
        }

        if (!await db.Policies.AnyAsync(item => item.Name == "Staging gate" && item.ArchivedAt == null)) {
            var staging = Policy.Create(
                "Staging gate",
                PolicyScope.Global,
                [EnvironmentTier.Staging],
                admin.Id,
                now,
                description: "Staging requires fresh scans. High findings warn above 10.");
            db.Policies.Add(staging);
            db.PolicyRules.Add(PolicyRule.Create(
                staging.Id,
                PolicyRuleType.RequireScan,
                RuleEffect.Deny,
                0,
                """{"scanners":["Gitleaks","Trivy","Semgrep"],"maxAgeHours":168}"""));
            db.PolicyRules.Add(PolicyRule.Create(
                staging.Id,
                PolicyRuleType.MaxFindings,
                RuleEffect.Warn,
                1,
                """{"severity":"High","max":10}"""));
        }

        await db.SaveChangesAsync();
    }

    private static async Task EnsureCircuitStagingTargetAsync(AegisOpsDbContext db) {
        var environment = await db.Environments
            .Where(item => item.Name == "Staging" && item.Tier == EnvironmentTier.Staging)
            .Join(db.Projects.Where(item => item.Slug == "circuit-service"), item => item.ProjectId, project => project.Id, (item, _) => item)
            .SingleOrDefaultAsync();
        if (environment is null || !string.Equals(environment.Target.Type, "noop", StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        environment.Retarget(DeploymentTarget.Docker(
            "circuit-service-staging",
            "aegisops-staging",
            8102,
            8080,
            "/health",
            60));
        await db.SaveChangesAsync();
    }

    private static async Task EnsureSiteStagingTargetAsync(AegisOpsDbContext db) {
        var environment = await db.Environments
            .Where(item => item.Name == "Staging" && item.Tier == EnvironmentTier.Staging)
            .Join(db.Projects.Where(item => item.Slug == "site-service"), item => item.ProjectId, project => project.Id, (item, _) => item)
            .SingleOrDefaultAsync();
        // Never override a target explicitly configured by an operator (including Production).
        if (environment is null || !string.Equals(environment.Target.Type, "noop", StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        environment.Retarget(DeploymentTarget.Docker(
            "site-service-staging",
            "aegisops-staging",
            8101,
            8080,
            "/health",
            60));
        await db.SaveChangesAsync();
    }

    private static async Task EnsureRoleAsync(RoleManager<IdentityRole<Guid>> roles, string name) {
        if (await roles.RoleExistsAsync(name)) {
            return;
        }

        ThrowIfFailed(await roles.CreateAsync(new IdentityRole<Guid> {
            Id = Guid.CreateVersion7(),
            Name = name,
        }));
    }

    private static void ThrowIfFailed(IdentityResult result) {
        if (result.Succeeded) {
            return;
        }

        var errors = string.Join("; ", result.Errors.Select(error => error.Description));
        throw new InvalidOperationException(errors);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Development user {Email} created. Password: {Password}")]
    private static partial void LogCreated(ILogger logger, string email, string password);
}
