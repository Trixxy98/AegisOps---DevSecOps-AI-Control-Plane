namespace AegisOps.Application.Authorization;

public static class RolePermissions {
    private static readonly string[] Shared = [
        Permissions.TeamsRead,
        Permissions.ProjectsRead,
        Permissions.ArtifactsRead,
        Permissions.ScansRead,
        Permissions.FindingsRead,
        Permissions.DeploymentsRead,
        Permissions.PoliciesRead,
        Permissions.AiChat,
        Permissions.AiRequestsRead,
    ];

    private static readonly Dictionary<string, string[]>Map = new(StringComparer.Ordinal) {
        ["Admin"] = [
            ..Shared,
            Permissions.TeamsWrite,
            Permissions.ProjectsWrite,
            Permissions.EnvironmentsWrite,
            Permissions.ApiKeysManage,
            Permissions.ArtifactsWrite,
            Permissions.ScansWrite,
            Permissions.FindingsSuppress,
            Permissions.DeploymentsRequest,
            Permissions.DeploymentsCancel,
            Permissions.DeploymentsApprove,
            Permissions.PoliciesWrite,
            Permissions.AiPoliciesWrite,
            Permissions.AuditRead,
            Permissions.UsersManage,
            Permissions.RolesManage,
            Permissions.ModelsManage,
        ],
        ["Developer"] = [
            ..Shared,
            Permissions.DeploymentsRequest,
            Permissions.DeploymentsCancel,
        ],
        ["Security"] = [
            ..Shared,
            Permissions.ArtifactsWrite,
            Permissions.ScansWrite,
            Permissions.FindingsSuppress,
            Permissions.DeploymentsApprove,
            Permissions.PoliciesWrite,
            Permissions.AiPoliciesWrite,
            Permissions.AuditRead,
        ],
        ["Approver"] = [
            ..Shared,
            Permissions.DeploymentsApprove,
        ],
    };

    public static IReadOnlyList<string> For(IEnumerable<string> roles) {
        var granted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles) {
            if (!Map.TryGetValue(role, out var permissions)) {
                continue;
            }

            foreach (var permission in permissions) {
                granted.Add(permission);
            }
        }

        return granted.OrderBy(permission => permission, StringComparer.Ordinal).ToArray();
    }

    public static bool Grants(IEnumerable<string> roles, string permission) {
            return For(roles).Contains(permission, StringComparer.Ordinal);
        }
}