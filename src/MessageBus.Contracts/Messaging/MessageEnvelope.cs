using System.Text.Json;

namespace MessageBus.Contracts.Messaging;

/// <summary>
/// One message, CloudEvents 1.0 style. Everything here is readable by the service (it routes on it
/// and the dashboard shows it); the body and attachments are end-to-end encrypted when
/// <see cref="Encryption"/> is set and are only referenced here. <c>docs/security-model.md</c> (the original issue)
/// lists exactly which fields the service can see.
/// </summary>
/// <remarks>
/// The service sets <see cref="Id"/>, <see cref="Source"/>, <see cref="Time"/>,
/// <see cref="WorkspaceId"/>, <see cref="Sender"/> and <see cref="HopCount"/> itself; values a client
/// sends for them are ignored.
/// </remarks>
public sealed record MessageEnvelope
{
    /// <summary>CloudEvents version, always <c>1.0</c>.</summary>
    public string SpecVersion { get; set; } = "1.0";

    /// <summary>Message id (<c>msg_…</c>, see <see cref="ContractIds"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Any type string the sender chooses, for example <c>task.implement</c> or <c>docs.requested</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Who sent it, as a URI: <c>agent://{workspaceId}/{agentId}</c>, <c>person://{workspaceId}/{subjectId}</c>, <c>apikey://…</c> or <c>system://…</c>.</summary>
    public required string Source { get; init; }

    /// <summary>When the service accepted it.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>The workspace (tenant) it belongs to.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Who it is for.</summary>
    public required MessageTarget Target { get; init; }

    /// <summary>Where answers go (for <see cref="Expect.Reply"/>); defaults to the sender.</summary>
    public MessageTarget? ReplyTo { get; init; }

    /// <summary>The thread (task) this message belongs to (<c>thr_…</c>).</summary>
    public required string ThreadId { get; init; }

    /// <summary>The message this one answers or was caused by, if any.</summary>
    public string? ParentId { get; init; }

    /// <summary>The project it is part of (<c>prj_…</c>), if any. No routing depends on it.</summary>
    public string? ProjectId { get; init; }

    /// <summary>A short plain-text title for the dashboard (at most <see cref="MessageLimits.MaxTitleLength"/> characters). Not encrypted.</summary>
    public string? Title { get; init; }

    /// <summary>Free-form labels for filtering, for example <c>{"area":"api"}</c>.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();

    /// <summary>Typed references (PRs, work items, commits). Not encrypted; receivers fetch them with their own credentials.</summary>
    public IReadOnlyList<Ref> Refs { get; set; } = [];

    /// <summary>What the sender expects back; null for nothing.</summary>
    public Expect? Expect { get; init; }

    /// <summary>Delivery priority.</summary>
    public Priority Priority { get; set; } = Priority.Normal;

    /// <summary>A second message with the same key, target and workspace inside the role's dedupe window is dropped.</summary>
    public string? DedupeKey { get; init; }

    /// <summary>After this time an undelivered message goes to dead letters as <see cref="DeadLetterReason.Expired"/>.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>How many agent-to-agent messages led to this one (0 for a message a person or a new orchestrator sent).</summary>
    public int HopCount { get; init; }

    /// <summary>W3C trace context, so one trace follows the work across machines.</summary>
    public TraceContext? Trace { get; init; }

    /// <summary>Who sent it, as verified by the service.</summary>
    public required SenderIdentity Sender { get; init; }

    /// <summary>Where the (usually encrypted) body is.</summary>
    public required BodyRef Body { get; init; }

    /// <summary>Attached files, bundles and patches (each encrypted on its own).</summary>
    public IReadOnlyList<AttachmentRef> Attachments { get; set; } = [];

    /// <summary>How the body is encrypted; null when it isn't end-to-end encrypted (a client that can't run <c>mbus</c>).</summary>
    public EncryptionInfo? Encryption { get; init; }

    /// <summary>Agents that may not take this message (for example the authors of the code a review is about, the original issue).</summary>
    public IReadOnlyList<string> ExcludeAgents { get; set; } = [];

    /// <summary>The model the sender asked the task to run on (the original issue); null for the role's or agent's default.</summary>
    public string? Model { get; init; }

    /// <summary>The verification request persisted by the service for this message, if any.</summary>
    public VerificationRequest? Verification { get; init; }
}

/// <summary>
/// The body as the sending agent wrote it, before encryption (and after decryption on the receiving
/// machine). The service never sees this when the message is end-to-end encrypted.
/// </summary>
public sealed record MessageBody
{
    /// <summary>Worker friction notes, encrypted with the answer by the local settle tool.</summary>
    public IReadOnlyList<FrictionNote> Friction { get; set; } = [];

    /// <summary>What to do, written for an agent to act on.</summary>
    public required string Summary { get; init; }

    /// <summary>Optional longer free text.</summary>
    public string? Details { get; init; }

    /// <summary>Optional structured data whose shape the role's instructions define.</summary>
    public JsonElement? Data { get; init; }
}

/// <summary>Where a message body is stored. Bodies up to <see cref="MessageLimits.InlineBodyBytes"/> are inline; larger ones are in the bodies container.</summary>
public sealed record BodyRef
{
    /// <summary>Size in bytes of what is stored (ciphertext when encrypted).</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of what is stored.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Media type of the plaintext; <see cref="MessageLimits.BodyContentType"/> for a <see cref="MessageBody"/>.</summary>
    public string ContentType { get; set; } = MessageLimits.BodyContentType;

    /// <summary>The stored bytes, base64, when inline.</summary>
    public string? Data { get; init; }

    /// <summary>The blob name in the body container when not inline.</summary>
    public string? BlobName { get; init; }
}

/// <summary>
/// A typed reference to something outside the bus. <see cref="Kind"/> is one of <see cref="RefKinds"/>
/// (other values are allowed and passed through); the other fields are filled as the kind needs.
/// </summary>
public sealed record Ref
{
    /// <summary>Display metadata when <see cref="Kind"/> is <see cref="RefKinds.Exhibit"/>.</summary>
    public ExhibitRef? Exhibit { get; init; }

    /// <summary>The kind, for example <c>github.pr</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>A link to it, when it has one.</summary>
    public string? Url { get; init; }

    /// <summary>Repository, <c>owner/name</c> on GitHub or <c>org/project/repo</c> on Azure DevOps.</summary>
    public string? Repo { get; init; }

    /// <summary>PR, issue, work item or build number.</summary>
    public long? Number { get; init; }

    /// <summary>A commit SHA.</summary>
    public string? Sha { get; init; }

    /// <summary>A branch or file path.</summary>
    public string? Path { get; init; }

    /// <summary>An id that isn't a number, such as an attachment id.</summary>
    public string? Id { get; init; }

    /// <summary>A short label for display.</summary>
    public string? Title { get; init; }
}

/// <summary>Well-known <see cref="Ref.Kind"/> values.</summary>
public static class RefKinds
{
    /// <summary>An exhibit from a workflow step.</summary>
    public const string Exhibit = "exhibit";

    /// <summary>An attachment a runner task tried to return but could not send.</summary>
    public const string AttachmentRefused = "attachment.refused";

    /// <summary>A GitHub pull request.</summary>
    public const string GitHubPullRequest = "github.pr";
    /// <summary>A GitHub issue.</summary>
    public const string GitHubIssue = "github.issue";
    /// <summary>A commit on GitHub.</summary>
    public const string GitHubCommit = "github.commit";
    /// <summary>An Azure DevOps work item.</summary>
    public const string AdoWorkItem = "ado.workitem";
    /// <summary>An Azure DevOps pull request.</summary>
    public const string AdoPullRequest = "ado.pr";
    /// <summary>An Azure DevOps build.</summary>
    public const string AdoBuild = "ado.build";
    /// <summary>A commit in a git repository with no hosted link (for example inside a bundle).</summary>
    public const string GitCommit = "git.commit";
    /// <summary>A git branch.</summary>
    public const string GitBranch = "git.branch";
    /// <summary>A file path in the task's working tree.</summary>
    public const string File = "file";
    /// <summary>Any web link.</summary>
    public const string Url = "url";
    /// <summary>An attachment on this message (<see cref="Ref.Id"/> is the attachment id).</summary>
    public const string Attachment = "attachment";
    /// <summary>An MCP resource URI.</summary>
    public const string McpResource = "mcp.resource";
}

/// <summary>An attached file, git bundle or patch. The message carries this, never a link; links are minted when a receiver takes the message.</summary>
public sealed record AttachmentRef
{
    /// <summary>Attachment id (<c>att_…</c>).</summary>
    public required string Id { get; init; }

    /// <summary>File name for display and for saving on the receiving machine.</summary>
    public required string Name { get; init; }

    /// <summary>How it was packaged.</summary>
    public required AttachmentFormat Format { get; init; }

    /// <summary>Size in bytes of the stored blob (ciphertext when encrypted).</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of the stored blob.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Media type of the plaintext.</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>For a git bundle: the branch it carries.</summary>
    public string? Branch { get; init; }

    /// <summary>For a git bundle or patch: the commit it is based on.</summary>
    public string? BaseCommit { get; init; }

    /// <summary>How it is encrypted; null when not end-to-end encrypted.</summary>
    public EncryptionInfo? Encryption { get; init; }
}

/// <summary>
/// How a body or attachment was encrypted on the sending machine: AES-256-GCM with a fresh key, and
/// that key wrapped to the target role's public key (the original issue owns the scheme). The GCM tag is appended to
/// the ciphertext.
/// </summary>
public sealed record EncryptionInfo
{
    /// <summary>Content algorithm, <see cref="Security.EncryptionAlgorithms.Aes256Gcm"/>.</summary>
    public required string Algorithm { get; init; }

    /// <summary>Key-wrap algorithm, one of <see cref="Security.EncryptionAlgorithms"/>.</summary>
    public required string KeyWrapAlgorithm { get; init; }

    /// <summary>The id and version of the public key the content key was wrapped to (<c>ek_…</c>).</summary>
    public required string KeyId { get; init; }

    /// <summary>The wrapped content key, base64.</summary>
    public required string WrappedKey { get; init; }

    /// <summary>The GCM nonce, base64 (12 bytes).</summary>
    public required string Nonce { get; init; }

    /// <summary>Sender's ephemeral public key for ECDH-based wrapping, base64 (null for RSA wrapping).</summary>
    public string? EphemeralPublicKey { get; init; }
}

/// <summary>Who sent a message, as the service verified it. Shown to the receiving agent as provenance.</summary>
public sealed record SenderIdentity
{
    /// <summary>The kind of sender.</summary>
    public required SenderKind Kind { get; init; }

    /// <summary>The person behind it (the agent's or API key's owner), as a subject id such as <c>microsoft:&lt;oid&gt;</c>.</summary>
    public string? SubjectId { get; init; }

    /// <summary>Name for display.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The sending agent (<c>agt_…</c>), for agent senders.</summary>
    public string? AgentId { get; init; }

    /// <summary>The sending machine (<c>mch_…</c>), for agent senders.</summary>
    public string? MachineId { get; init; }

    /// <summary>The API key used (<c>key_…</c>), for API-key senders.</summary>
    public string? ApiKeyId { get; init; }

    /// <summary>The role the sender was working in when it sent this, if any.</summary>
    public string? Role { get; init; }
}

/// <summary>W3C trace context (https://www.w3.org/TR/trace-context/).</summary>
public sealed record TraceContext
{
    /// <summary>The <c>traceparent</c> value, for example <c>00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01</c>.</summary>
    public required string TraceParent { get; init; }

    /// <summary>The <c>tracestate</c> value, if any.</summary>
    public string? TraceState { get; init; }
}

/// <summary>Size and time limits that more than one component enforces or shows.</summary>
public static class MessageLimits
{
    /// <summary>Media type of an encoded <see cref="MessageBody"/>.</summary>
    public const string BodyContentType = "application/vnd.mbus.body+json";
    /// <summary>Largest body (after encryption) the service accepts: 256 KB. Larger content goes in attachments.</summary>
    public const int MaxBodyBytes = 256 * 1024;
    /// <summary>Bodies up to 64 KB are stored inline in the message row; larger ones in the bodies container.</summary>
    public const int InlineBodyBytes = 64 * 1024;
    /// <summary>Longest <see cref="MessageEnvelope.Title"/>.</summary>
    public const int MaxTitleLength = 200;
    /// <summary>Default hop limit (workspace policy can lower it).</summary>
    public const int DefaultMaxHops = 8;
    /// <summary>Default largest attachment a role accepts: 100 MB.</summary>
    public const long DefaultMaxAttachmentBytes = 100L * 1024 * 1024;
    /// <summary>How long an attachment is kept when its thread never completes: 72 hours.</summary>
    public const int AttachmentLifetimeSeconds = 72 * 3600;
    /// <summary>How long dead letters are kept: 14 days.</summary>
    public const int DeadLetterRetentionDays = 14;
    /// <summary>How long finished messages and threads are kept: 30 days.</summary>
    public const int MessageRetentionDays = 30;
    /// <summary>How long audit events are kept: 365 days.</summary>
    public const int AuditRetentionDays = 365;
    /// <summary>Hard cap on one lease, however many heartbeats: 8 hours.</summary>
    public const int MaxLeaseSeconds = 8 * 3600;
}
