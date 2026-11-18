namespace MessageBus.Contracts.Security;

/// <summary>
/// Workspace permission names checked by endpoints (<c>.RequireWorkspacePermission(...)</c>) and
/// returned to the client in <c>/api/me</c>. Which workspace role grants which permission is decided
/// by the hosted service catalog; this class only fixes the
/// names so the service, the client and API-key scopes agree. The first four existed in the template.
/// </summary>
public static class PermissionNames
{
    /// <summary>See the workspace and its data (every member).</summary>
    public const string WorkspaceRead = "workspace.read";
    /// <summary>Invite, remove and change members; map Entra groups to roles (the original issue).</summary>
    public const string MembersManage = "workspace.members.manage";
    /// <summary>Change workspace settings.</summary>
    public const string SettingsManage = "workspace.settings.manage";
    /// <summary>Delete the workspace (owners).</summary>
    public const string WorkspaceDelete = "workspace.delete";

    /// <summary>Send messages and use the dashboard's 'Send test message' (every member).</summary>
    public const string MessagesSend = "workspace.messages.send";
    /// <summary>Cancel anyone's threads and projects, not just your own (the original issue, the original issue).</summary>
    public const string MessagesManage = "workspace.messages.manage";
    /// <summary>Replay, redirect and discard dead letters (the original issue, the original issue).</summary>
    public const string DeadLettersManage = "workspace.deadletters.manage";
    /// <summary>Create, edit and delete roles; publish role instructions (the original issue, the original issue).</summary>
    public const string RolesManage = "workspace.roles.manage";
    /// <summary>Approve or reject requests to join roles that require approval, for any role (role owners can for their own) (the original issue).</summary>
    public const string RoleJoinsApprove = "workspace.roles.joins.approve";
    /// <summary>Remove or edit any machine, and approve held runners (the original issue, the original issue).</summary>
    public const string MachinesManage = "workspace.machines.manage";
    /// <summary>Issue one-time enrollment tokens for shared runners (the original issue).</summary>
    public const string RunnersEnroll = "workspace.runners.enroll";
    /// <summary>Create, list and revoke workspace API keys (the original issue).</summary>
    public const string ApiKeysManage = "workspace.apikeys.manage";
    /// <summary>Rotate workspace and role keys and revoke key grants (the original issue).</summary>
    public const string KeysManage = "workspace.keys.manage";
    /// <summary>Change any project's budget, visibility and status (the original issue, the original issue).</summary>
    public const string ProjectsManage = "workspace.projects.manage";
    /// <summary>Create, edit and delete routes (the original issue).</summary>
    public const string RoutesManage = "workspace.routes.manage";
    /// <summary>Connect and configure integrations (the original issue).</summary>
    public const string IntegrationsManage = "workspace.integrations.manage";
    /// <summary>Create and edit alert rules and alert destinations (the original issue).</summary>
    public const string AlertsManage = "workspace.alerts.manage";
    /// <summary>Change security settings and agent policies: hop limit, allowed IP ranges, unmanaged runner approval (the original issue).</summary>
    public const string PoliciesManage = "workspace.policies.manage";
    /// <summary>Read and export the audit log (the original issue).</summary>
    public const string AuditRead = "workspace.audit.read";

    /// <summary>Every name above.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        WorkspaceRead, MembersManage, SettingsManage, WorkspaceDelete,
        MessagesSend, MessagesManage, DeadLettersManage, RolesManage, RoleJoinsApprove,
        MachinesManage, RunnersEnroll, ApiKeysManage, KeysManage, ProjectsManage,
        RoutesManage, IntegrationsManage, AlertsManage, PoliciesManage, AuditRead,
    ];
}
