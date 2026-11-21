namespace MessageBus.Contracts.Http;

/// <summary>
/// Header names used between <c>mbus</c> (CLI, local MCP server, runner) and the service. The
/// runner's WebSocket connect request and its HTTP/2 traffic use the same names.
/// </summary>
public static class MbusHeaders
{
    /// <summary>The workspace a machine or API-key request is for, when the path doesn't say.</summary>
    public const string Workspace = "X-Mbus-Workspace";
    /// <summary>Unix seconds when a machine signed the request (<see cref="Security.MachineRequestSigning"/>).</summary>
    public const string Timestamp = "X-Mbus-Timestamp";
    /// <summary>A random single-use value in a signed request.</summary>
    public const string Nonce = "X-Mbus-Nonce";
    /// <summary>The device-key signature of a machine request, base64url.</summary>
    public const string Signature = "X-Mbus-Signature";
    /// <summary>Machine signature domain version; isolated requests require exactly one value, <c>2</c>.</summary>
    public const string SignatureVersion = "X-Mbus-Signature-Version";
    /// <summary>The <c>mbus</c> version making the request (CLI, MCP server or runner).</summary>
    public const string ClientVersion = "X-Mbus-Client-Version";
    /// <summary>Which part of <c>mbus</c> is calling: <c>cli</c>, <c>mcp</c> or <c>runner</c>.</summary>
    public const string ClientComponent = "X-Mbus-Client";
    /// <summary>The runner process's session id on the connect request; a newer session replaces an older one.</summary>
    public const string Session = "X-Mbus-Session";
    /// <summary>
    /// The agent behind an MCP request: one of the signing machine's own agents, by id (<c>agt_…</c>) or kind
    /// (<c>claude-code</c>). The service uses it for <c>inbox</c>, <c>progress</c> and <c>settle</c>.
    /// </summary>
    public const string Agent = "X-Mbus-Agent";
    /// <summary>Free slots, sent by the runner on connect.</summary>
    public const string Slots = "X-Mbus-Slots";
    /// <summary>Response header: the oldest <c>mbus</c> version the service accepts.</summary>
    public const string MinClientVersion = "X-Mbus-Min-Client-Version";
    /// <summary>Correlates a request with logs on both ends.</summary>
    public const string RequestId = "X-Mbus-Request-Id";
    /// <summary>Response header with a short machine-readable error code (see <see cref="Mcp.McpErrorCodes"/>).</summary>
    public const string Error = "X-Mbus-Error";
    /// <summary>W3C trace context header.</summary>
    public const string TraceParent = "traceparent";
    /// <summary>W3C trace state header.</summary>
    public const string TraceState = "tracestate";
}

/// <summary>
/// REST paths that one component serves and another calls. Feature-internal endpoints (dashboard
/// pages) aren't listed; they live with their feature under <c>/api/w/{workspaceId}</c>.
/// </summary>
public static class ApiRoutes
{
    /// <summary>Remote MCP endpoint (streamable HTTP) for agents that can't run <c>mbus mcp</c> (the original issue).</summary>
    public const string Mcp = "/mcp";
    /// <summary>POST <see cref="Security.MachineEnrollRequest"/>: enroll a machine (the original issue, called by <c>mbus login</c>).</summary>
    public const string MachineEnroll = "/api/machines/enroll";
    /// <summary>GET with the person's CLI session (<see cref="Security.AuthSchemes.Cli"/>): the workspaces they can enroll a machine in (<see cref="Security.EnrollableWorkspace"/> list) (the original issue, called by <c>mbus login</c>).</summary>
    public const string MachineWorkspaces = "/api/machines/workspaces";
    /// <summary>GET, signed by an enrolled machine: its own <see cref="Machines.MachineInfo"/>; answers even while it awaits approval (the original issue).</summary>
    public const string MachineSelf = "/api/machines/me";
    /// <summary>GET, signed by an enrolled machine: its own agents (<see cref="Machines.AgentInfo"/> list) with their roles, and no other machine's (the original issue, for <c>mbus doctor</c>).</summary>
    public const string MachineSelfAgents = "/api/machines/me/agents";
    /// <summary>GET with any non-browser credential (machine, API key, Entra token): who the service thinks the caller is, its workspace and permissions (the original issue).</summary>
    public const string Caller = "/api/caller";
    /// <summary>GET the workspace's machines (<see cref="Machines.MachineInfo"/> list); <c>/{machineId}</c> GET, PATCH (name, location), DELETE; <c>/{machineId}/approve</c> POST (the original issue).</summary>
    public const string Machines = "/api/w/{workspaceId}/machines";
    /// <summary>GET <c>?repositoryId=github:&lt;numeric-id&gt;</c>: discover approved verification environments for a repository.</summary>
    public const string VerificationEnvironments = "/api/w/{workspaceId}/environments";
    /// <summary>GET the workspace's API keys (list of key metadata), POST a new key request; <c>/{keyId}</c> DELETE revokes (the original issue).</summary>
    public const string ApiKeys = "/api/w/{workspaceId}/api-keys";
    /// <summary>PUT <see cref="Machines.MachineInventory"/>: report a fresh agent scan (the original issue, called by <c>mbus login</c> and <c>mbus connect --rescan</c>).</summary>
    public const string MachineInventory = "/api/machines/inventory";
    /// <summary>POST a token request under a workspace: issue a one-time runner token (the original issue).</summary>
    public const string EnrollmentTokens = "/api/w/{workspaceId}/enrollment-tokens";
    /// <summary>GET the workspace's and roles' public keys (<see cref="Security.PublicKeyInfo"/>) (the original issue).</summary>
    public const string PublicKeys = "/api/w/{workspaceId}/keys";
    /// <summary>GET this machine's key grants (<see cref="Security.KeyGrant"/>) (the original issue).</summary>
    public const string KeyGrants = "/api/w/{workspaceId}/keys/grants";
    /// <summary>GET <c>?scope=role&amp;owner=developer</c>: whether keys must be replaced (<see cref="Security.KeyRotationState"/> list) (the original issue).</summary>
    public const string KeyRotation = "/api/w/{workspaceId}/keys/rotation";
    /// <summary>POST <see cref="Attachments.UploadLinkRequest"/>: get a short-lived upload link (the original issue).</summary>
    public const string AttachmentUploadLink = "/api/w/{workspaceId}/attachments/upload-link";
    /// <summary>POST <c>?threadId=thr_…</c>: download links (<see cref="Runner.DownloadLink"/> list) for the attachments of a delivery the calling machine holds (the original issue).</summary>
    public const string AttachmentDownloadLinks = "/api/w/{workspaceId}/deliveries/{deliveryId}/attachment-links";
    /// <summary>POST: a download link (<see cref="Runner.DownloadLink"/>) for one attachment, for its uploader or the person whose thread it is on (a reply's files) (the original issue).</summary>
    public const string AttachmentDownloadLink = "/api/w/{workspaceId}/attachments/{attachmentId}/download-link";
    /// <summary>Runner WebSocket connect path (<see cref="Runner.RunnerProtocol.ConnectPath"/>).</summary>
    public const string RunnerConnect = Runner.RunnerProtocol.ConnectPath;
}
