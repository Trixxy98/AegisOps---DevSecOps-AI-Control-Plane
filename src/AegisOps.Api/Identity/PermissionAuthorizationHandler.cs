using AegisOps.Application.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace AegisOps.Api.Identity;

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement> {
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement
    ) {
        var roles = context.User.FindAll("role").Select(claim => claim.Value);
        if (RolePermissions.Grants(roles, requirement.Permission)) {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var scopes = context.User.FindAll("scope").Select(claim => claim.Value);
        if (scopes.Contains(requirement.Permission, StringComparer.Ordinal)) {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
