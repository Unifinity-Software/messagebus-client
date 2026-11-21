using System.Text.Json.Serialization;

namespace MessageBus.Contracts.Roles;

/// <summary>The role change requested for human review; deletion is not supported.</summary>
public enum RoleProposalOperation
{
    /// <summary>Create a role whose name is absent.</summary>
    [JsonStringEnumMemberName("create")] Create,
    /// <summary>Replace the captured generation and revision of an existing role.</summary>
    [JsonStringEnumMemberName("update")] Update,
}

/// <summary>Authoritative proposal state. A wait deadline does not change it.</summary>
public enum RoleProposalStatus
{
    /// <summary>Awaiting a workspace owner's decision.</summary>
    [JsonStringEnumMemberName("pending")] Pending,
    /// <summary>A decision was claimed; application or recovery is in progress.</summary>
    [JsonStringEnumMemberName("applying")] Applying,
    /// <summary>The role and immutable receipt were committed together.</summary>
    [JsonStringEnumMemberName("approved")] Approved,
    /// <summary>An owner rejected the request.</summary>
    [JsonStringEnumMemberName("rejected")] Rejected,
    /// <summary>The pending request passed its server deadline.</summary>
    [JsonStringEnumMemberName("expired")] Expired,
    /// <summary>The captured role changed; submit a new proposal.</summary>
    [JsonStringEnumMemberName("conflict")] Conflict,
    /// <summary>Definitive invalid prepared data prevented application.</summary>
    [JsonStringEnumMemberName("failed")] Failed,
}

/// <summary>A complete role replacement, without identity, signatures or other server-owned metadata.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoleDefinitionV1
{
    /// <summary>Immutable role name, fixed at submission.</summary>
    public required string Name { get; init; }
    /// <summary>Display label, at most 80 characters.</summary>
    public required string DisplayName { get; init; }
    /// <summary>Description, at most 500 characters.</summary>
    public string? Description { get; init; }
    /// <summary>Delivery settings.</summary>
    public DeliverySettings Delivery { get; set; } = new();
    /// <summary>Required machine capabilities.</summary>
    public IReadOnlyList<string> RequiredCapabilities { get; set; } = [];
    /// <summary>Limits.</summary>
    public RoleLimits Limits { get; set; } = new();
    /// <summary>Alert rules.</summary>
    public IReadOnlyList<AlertRule> AlertRules { get; set; } = [];
    /// <summary>Owners, senders and approval requirements.</summary>
    public RoleAccess Access { get; set; } = new();
    /// <summary>Run settings; omitted create settings use the server's safe proposal baseline.</summary>
    public RoleProposalRunEnvironment? RunEnvironment { get; init; }
    /// <summary>Instructions, 1–30,000 UTF-16 code units after server normalization.</summary>
    public required string Instructions { get; init; }
}

/// <summary>Requested run settings. Omitted fields are normalized by the server against the safe create baseline or captured update base.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoleProposalRunEnvironment
{
    /// <summary>Requested Codex Windows sandbox mode.</summary>
    public CodexWindowsSandbox? CodexOnWindows { get; init; }
    /// <summary>Agent tools to retain; an explicit empty list clears them.</summary>
    public IReadOnlyList<string>? KeepAgentTools { get; init; }
    /// <summary>Requested checkout mode.</summary>
    public CheckoutMode? Checkout { get; init; }
    /// <summary>Requested setup command.</summary>
    public string? SetupCommand { get; init; }
    /// <summary>Requested run deadline in seconds.</summary>
    public int? TimeoutSeconds { get; init; }
    /// <summary>Requested output capture.</summary>
    public LogCapture? Logs { get; init; }
    /// <summary>Requested permissions; each omitted field inherits independently.</summary>
    public RoleProposalPermissionProfile? PermissionProfile { get; init; }
    /// <summary>Requested model settings; each omitted field inherits independently.</summary>
    public RoleProposalModels? Models { get; init; }
}

/// <summary>Requested permissions with no client-side grants or runner defaults.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoleProposalPermissionProfile
{
    /// <summary>Requested profile name.</summary>
    public string? Name { get; init; }
    /// <summary>Requested allowed tools; empty explicitly requests agent defaults.</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }
    /// <summary>Requested network access.</summary>
    public NetworkAccess? Network { get; init; }
    /// <summary>Requested network allowlist.</summary>
    public IReadOnlyList<string>? AllowedHosts { get; init; }
    /// <summary>Requested file editing permission.</summary>
    public bool? CanEditFiles { get; init; }
    /// <summary>Requested command execution permission.</summary>
    public bool? CanRunCommands { get; init; }
    /// <summary>Requested push permission.</summary>
    public bool? CanPush { get; init; }
    /// <summary>Requested test execution permission.</summary>
    public bool? CanRunTests { get; init; }
}

/// <summary>Requested model settings without expanding omitted lists to unrestricted defaults.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoleProposalModels
{
    /// <summary>Requested default models by agent kind.</summary>
    public IReadOnlyList<AgentModelDefault>? Defaults { get; init; }
    /// <summary>Requested allowed models; empty explicitly allows any reported model.</summary>
    public IReadOnlyList<string>? Allowed { get; init; }
}

/// <summary>A versioned server capability; absent or unknown versions are unsupported.</summary>
public sealed record RoleProposalsCapability
{
    /// <summary>The supported protocol version; this client understands 1.</summary>
    public required int Version { get; init; }
    /// <summary>The server's permission comparison baseline.</summary>
    public required string BaselineVersion { get; init; }
    /// <summary>The server's canonical definition normalizer.</summary>
    public required string NormalizerVersion { get; init; }
}

/// <summary>A create/update submission with local coordinator provenance, not a cryptographic session credential.</summary>
public sealed record SubmitRoleProposalRequest
{
    /// <summary>Create or update.</summary>
    public required RoleProposalOperation Operation { get; init; }
    /// <summary>The requested complete definition.</summary>
    public required RoleDefinitionV1 Definition { get; init; }
    /// <summary>Why the change is needed, at most 2,000 characters.</summary>
    public required string Rationale { get; init; }
    /// <summary>Caller-chosen retry key; reuse with exactly the same request only.</summary>
    public required string IdempotencyKey { get; init; }
    /// <summary>The enrolled machine's proposing agent.</summary>
    public required string AgentId { get; init; }
    /// <summary>The immutable local coordinator record id.</summary>
    public required string CoordinatorRecordId { get; init; }
    /// <summary>The original session inbox; notices never move to another inbox.</summary>
    public required string InboxId { get; init; }
}

/// <summary>The submission acknowledgment; approval requires a separate dashboard decision.</summary>
public sealed record RoleProposalSubmitted
{
    /// <summary>The proposal id.</summary>
    public required string ProposalId { get; init; }
    /// <summary>The authoritative state, including an existing proposal on idempotent retry.</summary>
    public required RoleProposalStatus Status { get; init; }
    /// <summary>Server deadline for a pending decision.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>Owner dashboard review link.</summary>
    public required string ReviewUrl { get; init; }
}

/// <summary>The captured identity and storage revision of one role incarnation.</summary>
public sealed record RoleProposalBaseRole
{
    /// <summary>Immutable generation id.</summary>
    public required string Generation { get; init; }
    /// <summary>Exact storage ETag.</summary>
    public required string Etag { get; init; }
}

/// <summary>A server-computed warning about the final normalized role.</summary>
public sealed record RoleProposalWarning
{
    /// <summary>Stable warning code.</summary>
    public required string Code { get; init; }
    /// <summary>JSON field path.</summary>
    public required string Field { get; init; }
    /// <summary>Severity as supplied by the server.</summary>
    public required string Severity { get; init; }
    /// <summary>Explanation; runtime-dependent warnings are not isolation guarantees.</summary>
    public required string Message { get; init; }
}

/// <summary>An immutable application result, retained even when the role is later changed or recreated.</summary>
public sealed record RoleProposalReceipt
{
    /// <summary>The proposal applied.</summary>
    public required string ProposalId { get; init; }
    /// <summary>The role name.</summary>
    public required string Role { get; init; }
    /// <summary>The applied generation.</summary>
    public required string Generation { get; init; }
    /// <summary>The published instruction version.</summary>
    public required int Version { get; init; }
    /// <summary>Hash of the full normalized final definition.</summary>
    public required string FinalDigest { get; init; }
    /// <summary>Hash of the normalized instruction UTF-8 alone.</summary>
    public required string ContentSha256 { get; init; }
    /// <summary>Authenticated owner attribution, not a cryptographic signature.</summary>
    public required string ApprovedBy { get; init; }
    /// <summary>Server application time.</summary>
    public required DateTimeOffset AppliedAt { get; init; }
}

/// <summary>The authoritative proposer detail/result returned by GET and bounded wait.</summary>
public sealed record RoleProposalDetail
{
    /// <summary>The proposal id.</summary>
    public required string ProposalId { get; init; }
    /// <summary>Authoritative state; pending/applying at a wait deadline is not failure.</summary>
    public required RoleProposalStatus Status { get; init; }
    /// <summary>Create or update.</summary>
    public required RoleProposalOperation Operation { get; init; }
    /// <summary>The original session binding.</summary>
    public required string CoordinatorRecordId { get; init; }
    /// <summary>The original inbox, independent of its current liveness.</summary>
    public required string InboxId { get; init; }
    /// <summary>The proposing agent.</summary>
    public required string AgentId { get; init; }
    /// <summary>The proposing machine.</summary>
    public required string MachineId { get; init; }
    /// <summary>Server deadline.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>Owner review link.</summary>
    public required string ReviewUrl { get; init; }
    /// <summary>Original rationale.</summary>
    public string? Rationale { get; init; }
    /// <summary>Persisted safe template or original update snapshot.</summary>
    public RoleDefinitionV1? BaseDefinition { get; init; }
    /// <summary>Immutable original requested definition.</summary>
    public RoleDefinitionV1? RequestedDefinition { get; init; }
    /// <summary>The current role, null when absent.</summary>
    public RoleDefinitionV1? CurrentDefinition { get; init; }
    /// <summary>The applied edited definition, when available.</summary>
    public RoleDefinitionV1? FinalDefinition { get; init; }
    /// <summary>Server warnings retained with the final review, when supplied in the detail.</summary>
    public IReadOnlyList<RoleProposalWarning> Warnings { get; set; } = [];
    /// <summary>Captured update identity; null for create.</summary>
    public RoleProposalBaseRole? BaseRole { get; init; }
    /// <summary>Application receipt; only approved is successful application.</summary>
    public RoleProposalReceipt? Receipt { get; init; }
    /// <summary>Decision attribution.</summary>
    public string? DecidedBy { get; init; }
    /// <summary>Server decision time.</summary>
    public DateTimeOffset? DecidedAt { get; init; }
    /// <summary>Rejection explanation, if any.</summary>
    public string? Comment { get; init; }
}
