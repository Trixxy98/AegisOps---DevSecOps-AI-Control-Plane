using System.Security.Cryptography;
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
        await EnsureDemoTeamAsync(db, users, now);
        await EnsureProductionApprovalsAsync(db, users, now);
    }

    private sealed record DemoAccount(string Email, string DisplayName, string[] Roles);
    private static readonly DemoAccount[] Accounts = [
        new(Email, "AegisOps Admin", [AdminRole]),
        new("developer@aegisops.local", "AegisOps Developer", [DeveloperRole]),
        new("rith@aegisops.local", "Rith", [DeveloperRole]),
        new("security@aegisops.local", "AegisOps Security", [SecurityRole, ApproverRole]),
        new("approver@aegisops.local", "AegisOps Approver", [ApproverRole]),
    ];

    private static async Task EnsureDemoTeamAsync(
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
