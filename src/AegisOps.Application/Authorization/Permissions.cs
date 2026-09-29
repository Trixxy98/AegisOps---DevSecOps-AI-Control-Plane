namespace AegisOps.Application.Authorization;

public static class Permissions {
    public const string TeamsRead = "teams:read";
    public const string TeamsWrite = "teams:write";
    public const string ProjectsRead = "projects:read";
    public const string ProjectsWrite = "projects:write";
    public const string EnvironmentsWrite = "environments:write";
    public const string ApiKeysManage = "apikeys:manage";
    public const string ArtifactsRead = "artifacts:read";
    public const string ArtifactsWrite = "artifacts:write";
    public const string ScansRead = "scans:read";
    public const string ScansWrite = "scans:write";
    public const string FindingsRead = "findings:read";
    public const string FindingsSuppress = "findings:suppress";
    public const string DeploymentsRead = "deployments:read";
    public const string DeploymentsRequest = "deployments:request";
    public const string DeploymentsCancel = "deployments:cancel";
    public const string DeploymentsApprove = "deployments:approve";
    public const string PoliciesRead = "policies:read";
    public const string PoliciesWrite = "policies:write";
    public const string AiChat = "ai:chat";
    public const string AiRequestsRead = "ai:requests:read";
    public const string AiPoliciesWrite = "ai:policies:write";
    public const string AuditRead = "audit:read";
    public const string UsersManage = "users:manage";
    public const string RolesManage = "roles:manage";
    public const string ModelsManage = "models:manage";
    public static readonly IReadOnlyList<string> All = [
        TeamsRead,
        TeamsWrite,
        ProjectsRead,
        ProjectsWrite,
        EnvironmentsWrite,
        ApiKeysManage,
        ArtifactsRead,
        ArtifactsWrite,
        ScansRead,
        ScansWrite,
        FindingsRead,
        FindingsSuppress,
        DeploymentsRead,
        DeploymentsRequest,
        DeploymentsCancel,
        DeploymentsApprove,
        PoliciesRead,
        PoliciesWrite,
        AiChat,
        AiRequestsRead,
        AiPoliciesWrite,
        AuditRead,
        UsersManage,
        RolesManage,
        ModelsManage,
    ];
}