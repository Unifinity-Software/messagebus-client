using System.Text.Json.Serialization;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Projects;
using MessageBus.Contracts.Roles;

namespace MessageBus.Contracts.Mcp;

/// <summary>
/// MCP tool names (the original issue). The remote <c>/mcp</c> endpoint and the local <c>mbus mcp</c> server expose
/// the same tools with the same input and output types (the <c>…Input</c> and <c>…Result</c> records
/// in this namespace, serialized with <see cref="Json.MessageBusJson"/>).
/// </summary>
/// <remarks>
/// Bodies: agents always write and read plaintext <see cref="MessageBody"/> values. The local server
/// encrypts them into <c>sealedBody</c> before calling the service and decrypts on the way back, so
/// the service only sees ciphertext; it hides the <c>sealed…</c> fields from the schema it shows
/// agents. A client using the remote endpoint directly sends plaintext, which is protected in transit
/// and at rest but not end-to-end.
/// </remarks>
public static class McpToolNames
{
    /// <summary>Submit a role definition for editable owner review; local coordinators only.</summary>
    public const string ProposeRole = "propose_role";
    /// <summary>Read a role proposal belonging to the original local coordinator.</summary>
    public const string GetRoleProposal = "get_role_proposal";
    /// <summary>Wait for authoritative role proposal state, up to 30 seconds.</summary>
    public const string WaitRoleProposal = "wait_role_proposal";

    /// <summary>Whether this tool requires original coordinator proposal authority.</summary>
    public static bool IsRoleProposal(string name) => name is ProposeRole or GetRoleProposal or WaitRoleProposal;
    /// <summary>Send one message (a task, a question, an event).</summary>
    public const string Send = "send";
    /// <summary>Send several messages at once, optionally with a join.</summary>
    public const string SendMany = "send_many";
    /// <summary>Service-only send that atomically enforces a required verification request.</summary>
    public const string SendVerified = "send_verified";
    /// <summary>Service-only batch send that atomically enforces required verification requests.</summary>
    public const string SendManyVerified = "send_many_verified";
    /// <summary>Wait for any or all of a set of threads (or a project's) to finish, up to a timeout.</summary>
    public const string Wait = "wait";
    /// <summary>Read new events on a thread or project since a cursor.</summary>
    public const string Watch = "watch";
    /// <summary>Take messages addressed to the caller (its roles, its agent, its session inbox).</summary>
    public const string Inbox = "inbox";
    /// <summary>Report progress on a task being worked on.</summary>
    public const string Progress = "progress";
    /// <summary>Report how a task ended (and reply, for questions).</summary>
    public const string Settle = "settle";
    /// <summary>Look up threads or a project.</summary>
    public const string Status = "status";
    /// <summary>Move a task to another project, including its sub-tasks by default.</summary>
    public const string Move = "move";
    /// <summary>Cancel a thread (and its children) or a whole project.</summary>
    public const string Cancel = "cancel";
    /// <summary>Find or create the project for a work item.</summary>
    public const string EnsureProject = "ensure_project";
    /// <summary>Read-only work-item discovery.</summary>
    public const string FindProject = "find_project";
    /// <summary>Mark a project finished, with a summary.</summary>
    public const string CompleteProject = "complete_project";
    /// <summary>List roles, who holds them and free slots.</summary>
    public const string Directory = "directory";
    /// <summary>Read a role's description and live instructions (what it accepts and returns).</summary>
    public const string DescribeRole = "describe_role";
    /// <summary>Get events about references (PRs, builds, work items) delivered to an inbox.</summary>
    public const string Subscribe = "subscribe";
    /// <summary>Fetch an attachment (a git bundle into a repository, or a file) onto this machine. Local server only: it needs this machine's keys and git.</summary>
    public const string FetchAttachment = "fetch_attachment";
    /// <summary>Send a finished thread's task again as the next round (the original issue). A retried review keeps its original agent exclusions. Local server only: it opens and re-seals the body.</summary>
    public const string Retry = "retry";
    /// <summary>Ask a person to approve one exact step (the original issue). Only a person answers, in the dashboard; agents can never approve.</summary>
    public const string RequestApproval = "request_approval";
    /// <summary>Wait for the decision on an approval request, up to a timeout (the original issue).</summary>
    public const string WaitApproval = "wait_approval";
    /// <summary>
    /// Open this session's inbox (the original issue): replies sent there wake this same session again through the runner on this machine
    /// after it has closed. Through the local server, which records the session on this machine.
    /// </summary>
    public const string OpenInbox = "open_inbox";

    /// <summary>Every tool name.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Send, SendMany, Wait, Watch, Inbox, Progress, Settle, Status, Cancel, Move,
        EnsureProject, FindProject, CompleteProject, Directory, DescribeRole, Subscribe, FetchAttachment, Retry,
        RequestApproval, WaitApproval, OpenInbox, SendVerified, SendManyVerified, ProposeRole, GetRoleProposal, WaitRoleProposal,
    ];

    /// <summary>Tools only the local <c>mbus mcp</c> server has (the remote endpoint can't decrypt or touch this machine's files).</summary>
    public static IReadOnlyList<string> LocalOnly { get; } = [FetchAttachment, Retry, ProposeRole, GetRoleProposal, WaitRoleProposal];

    /// <summary>Tools that require a hosted service implementation and are never exposed by the local server.</summary>
    public static IReadOnlyList<string> ServiceOnly { get; } = [SendVerified, SendManyVerified];
}

/// <summary>Error codes a tool (or a REST call, in <see cref="Http.MbusHeaders.Error"/>) returns.</summary>
public static class McpErrorCodes
{
    /// <summary>The server does not advertise a supported feature protocol.</summary>
    public const string Unsupported = "unsupported";
    /// <summary>An argument is missing or malformed. Fix the call; don't retry as is.</summary>
    public const string InvalidInput = "invalid-input";
    /// <summary>The thread, project, role or agent doesn't exist (or isn't visible to the caller).</summary>
    public const string NotFound = "not-found";
    /// <summary>The caller may not do this.</summary>
    public const string Forbidden = "forbidden";
    /// <summary>The thing changed or already ended (for example settling a cancelled task).</summary>
    public const string Conflict = "conflict";
    /// <summary>Over a size limit (body or attachment).</summary>
    public const string TooLarge = "too-large";
    /// <summary>Over a budget, fan-out or rate limit. Retry after <see cref="McpError.RetryAfterSeconds"/> if set.</summary>
    public const string LimitReached = "limit-reached";
    /// <summary>The service is unavailable. Retry with backoff.</summary>
    public const string Unavailable = "unavailable";
    /// <summary>Something unexpected went wrong.</summary>
    public const string Internal = "internal";
}

/// <summary>A structured tool error: what went wrong and whether retrying helps.</summary>
public sealed record McpError
{
    /// <summary>One of <see cref="McpErrorCodes"/>.</summary>
    public required string Code { get; init; }

    /// <summary>What went wrong and what to do, written for an agent.</summary>
    public required string Message { get; init; }

    /// <summary>Whether the same call may succeed later.</summary>
    public bool Retryable { get; init; }

    /// <summary>Wait at least this long before retrying.</summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>Related role proposal, when supplied by a proposal endpoint.</summary>
    public string? ProposalId { get; init; }

    /// <summary>Authoritative proposal state on a conflict.</summary>
    public RoleProposalStatus? Status { get; init; }

    /// <summary>Current server review revision on stale-review.</summary>
    public string? CurrentRevision { get; init; }

    /// <summary>The argument at fault, for <see cref="McpErrorCodes.InvalidInput"/>.</summary>
    public string? Field { get; init; }
}

/// <summary>A body encrypted by the local MCP server: where it is and how it is encrypted.</summary>
public sealed record SealedBody
{
    /// <summary>The ciphertext, inline or as an uploaded blob.</summary>
    public required BodyRef Body { get; init; }

    /// <summary>How it is encrypted.</summary>
    public required EncryptionInfo Encryption { get; init; }
}

/// <summary>Input of <c>send</c> (and each item of <c>send_many</c>).</summary>
public sealed record SendInput
{
    /// <summary>The verification required for this role-targeted task, if any.</summary>
    public VerificationRequest? Verification { get; init; }

    /// <summary>Required provenance for a supported retry. The server reads its project and execution owner; labels cannot override them.</summary>
    public string? SourceThreadId { get; init; }

    /// <summary>Who it is for.</summary>
    public required MessageTarget To { get; init; }

    /// <summary>Message type, for example <c>task.implement</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Short plain-text title shown on the dashboard (not encrypted).</summary>
    public string? Title { get; init; }

    /// <summary>The body (plaintext; the local server encrypts it).</summary>
    public MessageBody? Body { get; init; }

    /// <summary>Set by the local server in place of <see cref="Body"/>; agents never set it.</summary>
    public SealedBody? SealedBody { get; init; }

    /// <summary>The project; defaults to the project this session last ensured or its assigned runner task's project.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Post into this existing thread (a reply or follow-up) instead of starting one.</summary>
    public string? ThreadId { get; init; }

    /// <summary>Start the new thread as a child of this one.</summary>
    public string? ParentThreadId { get; init; }

    /// <summary>The message this answers, if any.</summary>
    public string? ParentId { get; init; }

    /// <summary>What you expect back.</summary>
    public Expect? Expect { get; init; }

    /// <summary>Where answers go; defaults to the caller.</summary>
    public MessageTarget? ReplyTo { get; init; }

    /// <summary>Priority; defaults to the role's.</summary>
    public Priority? Priority { get; init; }

    /// <summary>Drop this message if one with the same key was sent recently.</summary>
    public string? DedupeKey { get; init; }

    /// <summary>Give up if not delivered within this many seconds; defaults to the role's message lifetime.</summary>
    public int? ExpiresInSeconds { get; init; }

    /// <summary>Must finish within this many seconds of being sent.</summary>
    public int? DeadlineSeconds { get; init; }

    /// <summary>Labels.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();

    /// <summary>References (PRs, work items, commits).</summary>
    public IReadOnlyList<Ref> Refs { get; set; } = [];

    /// <summary>Files, bundles or patches to attach (local server only).</summary>
    public IReadOnlyList<AttachSpec> Attach { get; set; } = [];

    /// <summary>Already-uploaded attachments (set by the local server after handling <see cref="Attach"/>).</summary>
    public IReadOnlyList<AttachmentRef> Attachments { get; set; } = [];

    /// <summary>Agents (<c>agt_…</c>) that may not take this message, for example the author of the code under review (the original issue).</summary>
    public IReadOnlyList<string> ExcludeAgents { get; set; } = [];

    /// <summary>
    /// Also keep the authors of the work under review from taking it: agents that held non-review
    /// tasks in the same project, or in the parent thread and the threads started from it, regardless of outcome (the original issue).
    /// </summary>
    public bool ExcludeAuthors { get; init; }

    /// <summary>
    /// The model to run the task on, for example <c>claude-fable-5-2</c> (the original issue). Only an agent that reports it takes the task;
    /// when none does, the send fails not-found. A role may limit which models its tasks can name; a task sent to one agent
    /// or to capabilities is outside role limits (as with role budgets) and needs only an agent that reports the model.
    /// Null runs the role's default for the agent (when it runs it), else the agent's own.
    /// </summary>
    public string? Model { get; init; }
}

/// <summary>Result of <c>send</c>.</summary>
public sealed record SendResult
{
    /// <summary>The message id.</summary>
    public required string MessageId { get; init; }

    /// <summary>Its thread; pass it to <c>wait</c>, <c>status</c> or <c>cancel</c>.</summary>
    public required string ThreadId { get; init; }

    /// <summary>Its project.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Where it stands (usually <see cref="ThreadStatus.Queued"/>).</summary>
    public required ThreadStatus Status { get; init; }

    /// <summary>True when an earlier message with the same dedupe key was found and returned instead.</summary>
    public bool Deduplicated { get; init; }

    /// <summary>Online agents that could take it (the same as <see cref="AgentsOnline"/>; 0 means it waits until one connects).</summary>
    public int AvailableAgents { get; init; }

    /// <summary>
    /// Agents that could take it whose runner is connected now, busy or not. 0 means none is connected: it waits
    /// until one connects. Counted for role and capability targets.
    /// </summary>
    public int AgentsOnline { get; init; }

    /// <summary>
    /// Of <see cref="AgentsOnline"/>, those with a free slot now. 0 while some are online means they are all busy
    /// with other tasks: it waits its turn. Counted for role and capability targets.
    /// </summary>
    public int AgentsFree { get; init; }

    /// <summary>
    /// Its place among the role's tasks still waiting to be taken (1 = next), when it went to a role and is
    /// waiting; null otherwise.
    /// </summary>
    public int? QueuePosition { get; init; }

    /// <summary>Agents kept from taking it: <see cref="SendInput.ExcludeAgents"/> plus, with <see cref="SendInput.ExcludeAuthors"/>, the authors found.</summary>
    public IReadOnlyList<string> ExcludedAgents { get; set; } = [];

    /// <summary>Something the sender should know, for example that fewer copies went out than the role asks for because of the exclusions.</summary>
    public string? Notice { get; init; }
}

/// <summary>Input of <c>send_many</c>.</summary>
public sealed record SendManyInput
{
    /// <summary>The messages.</summary>
    public required IReadOnlyList<SendInput> Messages { get; init; }

    /// <summary>Project for every message that doesn't name one.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Deliver one combined result when these threads finish.</summary>
    public JoinSpec? Join { get; init; }
}

/// <summary>Result of <c>send_many</c>.</summary>
public sealed record SendManyResult
{
    /// <summary>Current budget guidance, repeated while a project is at or above 80% of its cap.</summary>
    public string? Notice { get; init; }

    /// <summary>One result per message, in order.</summary>
    public required IReadOnlyList<SendResult> Results { get; init; }

    /// <summary>The join's thread, when a join was asked for.</summary>
    public string? JoinThreadId { get; init; }
}

/// <summary>Input of <c>wait</c>. Give thread ids, a project, or both.</summary>
public sealed record WaitInput
{
    /// <summary>Threads to wait for.</summary>
    public IReadOnlyList<string> ThreadIds { get; set; } = [];

    /// <summary>Wait for this project's open threads.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Copy delivery ids already returned; pass the previous result's seenCopies on subsequent calls.</summary>
    public IReadOnlyList<string> SeenCopies { get; set; } = [];

    /// <summary>Reply message ids already read; pass the previous result's seenReplies on subsequent calls.</summary>
    public IReadOnlyList<string> SeenReplies { get; set; } = [];

    /// <summary>Any or all.</summary>
    public WaitMode Mode { get; set; } = WaitMode.Any;

    /// <summary>
    /// Return after this many seconds even if nothing finished. The default stays under the about 120 s after
    /// which Claude Code ends a tool call; the local server also ends each call at its client's limit (the original issue).
    /// </summary>
    public int TimeoutSeconds { get; set; } = McpCallLimits.DefaultWaitSeconds;
}

/// <summary>Result of <c>wait</c>.</summary>
public sealed record WaitResult
{
    /// <summary>Current budget guidance, repeated while a project is at or above 80% of its cap.</summary>
    public string? Notice { get; init; }

    /// <summary>True when finished includes copies of a broadcast that is still pending.</summary>
    public bool Partial { get; init; }

    /// <summary>Cumulative copy delivery ids returned, for the next wait call.</summary>
    public IReadOnlyList<string> SeenCopies { get; set; } = [];

    /// <summary>Cumulative reply message ids returned, for the next wait call.</summary>
    public IReadOnlyList<string> SeenReplies { get; set; } = [];

    /// <summary>Finished threads, or newly finished broadcast copies when partial is true.</summary>
    public IReadOnlyList<ThreadSummary> Finished { get; set; } = [];

    /// <summary>Threads still open.</summary>
    public IReadOnlyList<string> Pending { get; set; } = [];

    /// <summary>True when the timeout passed first.</summary>
    public bool TimedOut { get; init; }

    /// <summary>Answers posted on the finished threads (replies, join results), oldest first. The local server decrypts their bodies.</summary>
    public IReadOnlyList<ReceivedMessage> Replies { get; set; } = [];
}

/// <summary>Input of <c>watch</c>. Give a thread or a project.</summary>
public sealed record WatchInput
{
    /// <summary>The thread.</summary>
    public string? ThreadId { get; init; }

    /// <summary>The project.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Only events after this event id (the previous result's cursor).</summary>
    public string? After { get; init; }

    /// <summary>Wait up to this many seconds for at least one event.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Most events to return (1–500); pass the cursor to read on.</summary>
    public int Limit { get; set; } = 50;

    /// <summary>Leave out progress reports that a later report on the same thread in this result replaces.</summary>
    public bool Compact { get; set; } = true;
}

/// <summary>Result of <c>watch</c>.</summary>
public sealed record WatchResult
{
    /// <summary>Events, oldest first.</summary>
    public IReadOnlyList<ThreadEvent> Events { get; set; } = [];

    /// <summary>Pass as <see cref="WatchInput.After"/> next time.</summary>
    public string? Cursor { get; init; }

    /// <summary>True when more events wait after the cursor (the result hit its limit).</summary>
    public bool More { get; init; }
}

/// <summary>Input of <c>inbox</c>.</summary>
public sealed record InboxInput
{
    /// <summary>Only this role's messages; default all the caller's roles, its agent and its session inbox.</summary>
    public string? Role { get; init; }

    /// <summary>Most messages to take.</summary>
    public int Max { get; set; } = 1;

    /// <summary>Look without taking (no lease).</summary>
    public bool Peek { get; init; }

    /// <summary>Wait up to this many seconds for a message.</summary>
    public int TimeoutSeconds { get; init; }

    /// <summary>Only this session inbox's messages (<c>inb_…</c>, from <c>open_inbox</c>); it must be one this agent opened (the original issue).</summary>
    public string? InboxId { get; init; }
}

/// <summary>A message as an agent receives it.</summary>
public sealed record ReceivedMessage
{
    /// <summary>The envelope.</summary>
    public required MessageEnvelope Envelope { get; init; }

    /// <summary>The decrypted body (filled by the local server, or by the service for messages that aren't end-to-end encrypted).</summary>
    public MessageBody? Body { get; init; }

    /// <summary>The delivery, when taken (not peeked). Pass it to <c>progress</c> and <c>settle</c>.</summary>
    public DeliveryInfo? Delivery { get; init; }

    /// <summary>Links for a blob body and attachments.</summary>
    public IReadOnlyList<Runner.DownloadLink> Links { get; set; } = [];

    /// <summary>
    /// True when <see cref="Body"/> was decrypted on this machine. The envelope's ciphertext and key
    /// material (body data, wrapped keys, nonces) are then left out to keep results small (the original issue).
    /// </summary>
    public bool Decrypted { get; init; }
}

/// <summary>Result of <c>inbox</c>.</summary>
public sealed record InboxResult
{
    /// <summary>The messages taken (or peeked).</summary>
    public IReadOnlyList<ReceivedMessage> Messages { get; set; } = [];
}

/// <summary>Input of <c>progress</c>.</summary>
public sealed record ProgressInput
{
    /// <summary>The thread being worked on.</summary>
    public required string ThreadId { get; init; }

    /// <summary>The delivery, when known (runs started by a runner have it in their environment).</summary>
    public string? DeliveryId { get; init; }

    /// <summary>What is happening now, one line.</summary>
    public required string Message { get; init; }

    /// <summary>Rough completion, 0–100.</summary>
    public int? Percent { get; init; }

    /// <summary>A runner's request to the sender's mbus (<see cref="ProgressUpdate.Request"/>, the original issue); the local server hides it from agents.</summary>
    public ProgressRequest? Request { get; init; }
}

/// <summary>Result of <c>progress</c>.</summary>
public sealed record ProgressResult
{
    /// <summary>The lease's new end.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>The task was cancelled: stop and settle as cancelled.</summary>
    public bool CancelRequested { get; init; }
}

/// <summary>Input of <c>settle</c>.</summary>
public sealed record SettleInput
{
    /// <summary>The delivered attempt, when the target advertises <c>settleAttemptV1</c>. Injected by the runner's local tools; remote callers use the attempt they received.</summary>
    public int? Attempt { get; init; }

    /// <summary>Optional short notes (at most 12). Local tool only: sealed into reply.friction; only kinds reach the service.</summary>
    public IReadOnlyList<FrictionNote> Friction { get; set; } = [];

    /// <summary>The thread.</summary>
    public required string ThreadId { get; init; }

    /// <summary>The delivery, when known.</summary>
    public string? DeliveryId { get; init; }

    /// <summary>How it ended.</summary>
    public required SettleResult Result { get; init; }

    /// <summary>The answer, for a message that expects a reply (plaintext; the local server encrypts it).</summary>
    public MessageBody? Reply { get; init; }

    /// <summary>Set by the local server in place of <see cref="Reply"/>.</summary>
    public SealedBody? SealedReply { get; init; }

    /// <summary>
    /// Files, bundles or patches to send back with the reply (local server only). A bundle or patch defaults
    /// to the commits after the task's own code (the branch it was sent with).
    /// </summary>
    public IReadOnlyList<AttachSpec> Attach { get; set; } = [];

    /// <summary>The reply's attachments, already uploaded (set by the local server after handling <see cref="Attach"/>).</summary>
    public IReadOnlyList<AttachmentRef> Attachments { get; set; } = [];

    /// <summary>
    /// Set by the local server when a runner started the agent: post the reply now and leave the
    /// delivery open, because the runner settles it after the agent exits.
    /// </summary>
    public bool ReplyOnly { get; init; }

    /// <summary>Set by the local tool when it created an answer solely to hold encrypted friction notes; never deliver it.</summary>
    public bool FrictionOnly { get; init; }
}

/// <summary>Result of <c>settle</c>.</summary>
public sealed record SettleOutput
{
    /// <summary>The thread's status after settling.</summary>
    public required ThreadStatus Status { get; init; }

    /// <summary>The reply message's id, when a reply was sent.</summary>
    public string? ReplyMessageId { get; init; }
}

/// <summary>Input of <c>status</c>. Give thread ids, a project, or neither for the caller's recent threads.</summary>
public sealed record StatusInput
{
    /// <summary>Threads to look up.</summary>
    public IReadOnlyList<string> ThreadIds { get; set; } = [];

    /// <summary>A project to look up (with its threads).</summary>
    public string? ProjectId { get; init; }

    /// <summary>
    /// One short line per thread (<see cref="StatusResult.Tasks"/>) instead of full threads, omitting replies. Default: compact for a
    /// project or your recent threads, full for threads named in <see cref="ThreadIds"/>.
    /// </summary>
    public bool? Compact { get; init; }

    /// <summary>Most threads to return, newest first (1–200; default 50).</summary>
    public int? Limit { get; init; }
}

/// <summary>Result of <c>status</c>.</summary>
public sealed record StatusResult
{
    /// <summary>The threads.</summary>
    public IReadOnlyList<ThreadSummary> Threads { get; set; } = [];

    /// <summary>Replies posted so far on named threads in full status; empty in compact status. Decrypted by the local server.</summary>
    public IReadOnlyList<ReceivedMessage> Replies { get; set; } = [];

    /// <summary>True when a named broadcast has finished copies and other copies pending.</summary>
    public bool Partial { get; init; }

    /// <summary>The project, when asked for.</summary>
    public ProjectInfo? Project { get; init; }

    /// <summary>The threads, one short line each, when the result is compact (<see cref="StatusInput.Compact"/>).</summary>
    public IReadOnlyList<ThreadBrief> Tasks { get; set; } = [];

    /// <summary>True when there are more threads than the limit returned.</summary>
    public bool More { get; init; }
}

/// <summary>One thread in a compact <c>status</c> result: what it is, where it stands and how it ended.</summary>
public sealed record ThreadBrief
{
    /// <summary>The current verification state, absent for tasks without verification requirements.</summary>
    public VerificationResult? Verification { get; init; }

    /// <summary>Current friction kinds when provided, including an empty list when no kind applies.</summary>
    public IReadOnlyList<string>? CurrentFrictionKinds { get; init; }

    /// <summary>The thread.</summary>
    public required string ThreadId { get; init; }

    /// <summary>The first message's title.</summary>
    public string? Title { get; init; }

    /// <summary>The first message's type.</summary>
    public string? Type { get; init; }

    /// <summary>Who it was sent to, as text (<c>role:developer</c>).</summary>
    public string? To { get; init; }

    /// <summary>Where it stands.</summary>
    public required ThreadStatus Status { get; init; }

    /// <summary>The thread it was started from, if any.</summary>
    public string? ParentThreadId { get; init; }

    /// <summary>The agent working on it (or that finished it).</summary>
    public string? AgentId { get; init; }

    /// <summary>That agent's machine.</summary>
    public string? MachineId { get; init; }

    /// <summary>How many times it has been delivered.</summary>
    public int Attempts { get; init; }

    /// <summary>When it last changed.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>How the agent said it ended, once it has.</summary>
    public SettleStatus? Outcome { get; init; }

    /// <summary>The result's summary, or the latest progress line while it runs.</summary>
    public string? Summary { get; init; }

    /// <summary>
    /// The model the task asked for, if it named one; <see cref="Usage"/>'s model is the one that ran (the original issue). Null means
    /// the task named no model and it ran on the role's or the agent's default, which is not a mismatch.
    /// </summary>
    public string? RequestedModel { get; init; }

    /// <summary>The run's usage, once it ended.</summary>
    public Usage? Usage { get; init; }

    /// <summary>The last error (a failed attempt, a decline, the dead-letter reason).</summary>
    public string? LastError { get; init; }
}

/// <summary>Input of <c>cancel</c>. Give a thread or a project.</summary>
public sealed record CancelInput
{
    /// <summary>The thread (its child threads are cancelled too).</summary>
    public string? ThreadId { get; init; }

    /// <summary>The project (all its open threads are cancelled and it ends as cancelled).</summary>
    public string? ProjectId { get; init; }

    /// <summary>Why, shown on the thread.</summary>
    public string? Reason { get; init; }
}

/// <summary>Result of <c>cancel</c>.</summary>
public sealed record CancelResult
{
    /// <summary>Every thread that was cancelled.</summary>
    public IReadOnlyList<string> CancelledThreadIds { get; set; } = [];

    /// <summary>Why nothing (or less than asked) was cancelled, for example "already finished (failed)".</summary>
    public string? Message { get; init; }
}

/// <summary>Input of <c>find_project</c>.</summary>
public sealed record FindProjectInput
{
    /// <summary>A project key or explicitly claimed work item. This lookup never creates anything.</summary>
    public required string Key { get; init; }
}

/// <summary>Read-only discovery result.</summary>
public sealed record FindProjectResult
{
    /// <summary>Visible project, or null if absent or hidden.</summary>
    public ProjectInfo? Project { get; init; }
}

/// <summary>Input of <c>ensure_project</c>. Idempotent on <see cref="Key"/>.</summary>
public sealed record EnsureProjectInput
{
    /// <summary>Immutable execution choice. Omit to preserve an existing project or use workspace execution for a new one.</summary>
    public ProjectExecutionScope? ExecutionScope { get; init; }

    /// <summary>Explicit work items to coordinate atomically (1–50). Ordinary refs are never claimed.</summary>
    public IReadOnlyList<string> ClaimedWorkItems { get; set; } = [];

    /// <summary>Session inbox identity. Filled by the local server; remote callers must open their own inbox.</summary>
    public string? CoordinatorInboxId { get; init; }

    /// <summary>The work item, as <see cref="SourceRef"/> text (<c>ado:org/project#123</c>, <c>gh:owner/repo#42</c>, <c>file:path</c>).</summary>
    public required string Key { get; init; }

    /// <summary>Title (used when the project is created).</summary>
    public required string Title { get; init; }

    /// <summary>References to keep with it.</summary>
    public IReadOnlyList<Ref> Refs { get; set; } = [];

    /// <summary>Budget cap in US dollars (estimated).</summary>
    public decimal? BudgetCapUsd { get; init; }

    /// <summary>Most tasks open at once.</summary>
    public int? MaxFanOut { get; init; }
}

/// <summary>Result of <c>ensure_project</c>.</summary>
public sealed record EnsureProjectResult
{
    /// <summary>The project.</summary>
    public required ProjectInfo Project { get; init; }

    /// <summary>True only after atomically claiming every requested work item for this session.</summary>
    public bool CoordinationAcquired { get; init; }

    /// <summary>The owning inbox, returned on claim success for deliberate resume after an MCP restart.</summary>
    public string? CoordinatorInboxId { get; init; }

    /// <summary>True when this call created it.</summary>
    public bool Created { get; init; }
}

/// <summary>Input of <c>complete_project</c>.</summary>
public sealed record CompleteProjectInput
{
    /// <summary>The project.</summary>
    public required string ProjectId { get; init; }

    /// <summary>What was done, for the dashboard.</summary>
    public required string Summary { get; init; }

    /// <summary><see cref="ProjectStatus.Completed"/> or <see cref="ProjectStatus.Failed"/>.</summary>
    public ProjectStatus Status { get; set; } = ProjectStatus.Completed;

    /// <summary>What it produced (PRs, branches).</summary>
    public IReadOnlyList<Ref> Refs { get; set; } = [];

    /// <summary>
    /// The orchestrating session's own usage (planning, reviewing, merging, local checks). Recorded on the
    /// project as its own line (<c>orchestrator</c>) and counted in the totals (the original issue).
    /// The local server fills it from the client's session log when the agent leaves it out and one can be read.
    /// </summary>
    public Usage? Usage { get; init; }
}

/// <summary>Result of <c>complete_project</c>.</summary>
public sealed record CompleteProjectResult
{
    /// <summary>Why automatic session usage could not be included, when unavailable.</summary>
    public string? Notice { get; init; }

    /// <summary>The project with its final totals.</summary>
    public required ProjectInfo Project { get; init; }
}

/// <summary>Input of <c>directory</c>.</summary>
public sealed record DirectoryInput
{
    /// <summary>Only this role.</summary>
    public string? Role { get; init; }

    /// <summary>Only agents whose machines have all of these capabilities.</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = [];

    /// <summary>Only agents online now.</summary>
    public bool OnlineOnly { get; init; }

    /// <summary>Local MCP only: discover verification approvals for this stable <c>github:&lt;numeric-id&gt;</c> repository identity.</summary>
    public string? VerificationRepositoryId { get; init; }
}

/// <summary>Result of <c>directory</c>.</summary>
public sealed record DirectoryResult
{
    /// <summary>The roles.</summary>
    public IReadOnlyList<DirectoryRole> Roles { get; set; } = [];

    /// <summary>The repository's verification registry, if requested through local MCP and available from the service.</summary>
    public VerificationDirectory? Verification { get; init; }
}

/// <summary>Input of <c>describe_role</c>.</summary>
public sealed record DescribeRoleInput
{
    /// <summary>Role name.</summary>
    public required string Role { get; init; }
}

/// <summary>Result of <c>describe_role</c>.</summary>
public sealed record DescribeRoleResult
{
    /// <summary>The role's settings.</summary>
    public required AgentRole Role { get; init; }

    /// <summary>Its live instructions (what it accepts and returns).</summary>
    public InstructionVersion? Instructions { get; init; }

    /// <summary>Holders online now.</summary>
    public int AgentsOnline { get; init; }
}

/// <summary>Input of <c>subscribe</c>: get events about references delivered as messages.</summary>
public sealed record SubscribeInput
{
    /// <summary>References to watch (a PR, a build, a work item).</summary>
    public IReadOnlyList<Ref> Refs { get; set; } = [];

    /// <summary>Event types wanted, for example <c>pr.merged</c>; empty for all.</summary>
    public IReadOnlyList<string> EventTypes { get; set; } = [];

    /// <summary>Tag delivered events with this project.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Where events go; defaults to the caller's session inbox.</summary>
    public MessageTarget? DeliverTo { get; init; }

    /// <summary>Stop after this many seconds.</summary>
    public int? ExpiresInSeconds { get; init; }
}

/// <summary>Result of <c>subscribe</c>.</summary>
public sealed record SubscribeResult
{
    /// <summary>Subscription id (<c>sub_…</c>).</summary>
    public required string SubscriptionId { get; init; }

    /// <summary>Where events will arrive.</summary>
    public required MessageTarget DeliverTo { get; init; }

    /// <summary>When it stops.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Input of <c>open_inbox</c> (the original issue).</summary>
public sealed record OpenInboxInput
{
    /// <summary>How long it stays open, in seconds (at most the service's inbox lifetime, 24 hours by default).</summary>
    public int? LifetimeSeconds { get; init; }
}

/// <summary>Result of <c>open_inbox</c>.</summary>
public sealed record OpenInboxResult
{
    /// <summary>The inbox (<c>inb_…</c>).</summary>
    public required string InboxId { get; init; }

    /// <summary>What to give <c>send</c> as <c>replyTo</c> (the local server does that for this session by itself).</summary>
    public required InboxTarget ReplyTo { get; init; }

    /// <summary>When it closes; sends to it fail after that.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The agent it belongs to (<c>agt_…</c>): the runner on that agent's machine takes its messages.</summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// Whether a reply that arrives after this session closes resumes this same session through this machine's runner;
    /// false starts a new session for it instead (see <see cref="Notice"/>).
    /// </summary>
    public bool Resumable { get; init; }

    /// <summary>The session the runner resumes (Claude Code's or Codex's session id), when found.</summary>
    public string? SessionId { get; init; }

    /// <summary>Why it isn't resumable, or anything else worth telling the user.</summary>
    public string? Notice { get; init; }
}

/// <summary>Input of <c>retry</c> (local server only): send a finished thread's task again as the next round, preserving its original agent exclusions.</summary>
public sealed record RetryInput
{
    /// <summary>The finished thread whose task to send again.</summary>
    public required string ThreadId { get; init; }

    /// <summary>What changed since the last attempt; put at the top of the body's details.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The repository for repackaging a task's single bundle or patch (default the current directory).
    /// File attachments are sent as they were. Use a fresh send with explicit repositories for multiple code attachments.
    /// </summary>
    public string? Repository { get; init; }
}

/// <summary>
/// Input of <c>request_approval</c> (the original issue): ask a person to approve one exact step before the agent takes it.
/// Only a person signed in to the dashboard answers; the agent waits with <c>wait_approval</c>.
/// </summary>
public sealed record RequestApprovalInput
{
    /// <summary>Author-supplied plain-text impact, not verified targets or authority. Normalize line endings to LF, trim, and map empty to null; at most 1000 Unicode scalars, no invalid surrogates or controls except LF/TAB. Requires <c>approvalAffectedV1</c>.</summary>
    public string? Affected { get; init; }

    /// <summary>What to approve, one short sentence (the approver sees it as the title).</summary>
    public required string Title { get; init; }

    /// <summary>Why, what exactly will happen, and what happens if it is rejected; plain text.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Who decides: <c>member:{subject id or email}</c> or <c>role:{name}</c> (the role's owners). Default: the person
    /// who started the project, else the machine's owner.
    /// </summary>
    public string? Approver { get; init; }

    /// <summary>The thread it is about (usually the task being worked on).</summary>
    public string? ThreadId { get; init; }

    /// <summary>The project it is about.</summary>
    public string? ProjectId { get; init; }

    /// <summary>High-risk: the approver must have signed in within the last few minutes.</summary>
    public bool HighRisk { get; init; }

    /// <summary>Expires unanswered after this many seconds (5 minutes to 7 days; default 24 hours).</summary>
    public int? ExpiresInSeconds { get; init; }

    /// <summary>
    /// Also send the decision as a message here (for example <c>agent:agt_…</c> to be woken through the runner).
    /// Default: no message; the decision is read with <c>wait_approval</c>.
    /// </summary>
    public MessageTarget? ReplyTo { get; init; }
}

/// <summary>Input of <c>wait_approval</c> (the original issue).</summary>
public sealed record WaitApprovalInput
{
    /// <summary>The approval (<c>apr_…</c>) from <c>request_approval</c>.</summary>
    public required string ApprovalId { get; init; }

    /// <summary>Return after this many seconds even if nobody has answered (the local server also stops at its client's limit).</summary>
    public int TimeoutSeconds { get; set; } = McpCallLimits.DefaultWaitSeconds;
}

/// <summary>Where an approval request stands.</summary>
public enum ApprovalState
{
    /// <summary>Nobody has answered yet.</summary>
    [JsonStringEnumMemberName("pending")] Pending,
    /// <summary>A person approved it: go ahead with exactly what was asked.</summary>
    [JsonStringEnumMemberName("approved")] Approved,
    /// <summary>A person rejected it: don't do it.</summary>
    [JsonStringEnumMemberName("rejected")] Rejected,
    /// <summary>Nobody answered in time: treat it as not approved.</summary>
    [JsonStringEnumMemberName("expired")] Expired,
}

/// <summary>Result of <c>request_approval</c> and <c>wait_approval</c> (the original issue).</summary>
public sealed record ApprovalResult
{
    /// <summary>The normalized author-supplied impact shown to the approver, when supplied; not verified targets or authority.</summary>
    public string? Affected { get; init; }

    /// <summary>The approval id (<c>apr_…</c>), for <c>wait_approval</c>.</summary>
    public required string ApprovalId { get; init; }

    /// <summary>pending, approved, rejected or expired.</summary>
    public required ApprovalState Status { get; init; }

    /// <summary>What was asked.</summary>
    public required string Title { get; init; }

    /// <summary>The people who can answer, by name.</summary>
    public IReadOnlyList<string> Approvers { get; set; } = [];

    /// <summary>When it expires unanswered.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Who answered, by name.</summary>
    public string? DecidedBy { get; init; }

    /// <summary>When it was answered or expired.</summary>
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>The approver's comment, if any.</summary>
    public string? Comment { get; init; }

    /// <summary>SHA-256 of the request exactly as the approver saw it.</summary>
    public string? RequestDigest { get; init; }

    /// <summary>True when <c>wait_approval</c>'s timeout passed while it was still pending.</summary>
    public bool TimedOut { get; init; }
}

/// <summary>How long one tool call may block (the original issue): MCP clients end a tool call after a fixed time.</summary>
public static class McpCallLimits
{
    /// <summary>Default <c>wait</c> timeout.</summary>
    public const int DefaultWaitSeconds = 100;

    /// <summary>Longest a local server lets one call block for Claude Code, which ends tool calls at about 120 s.</summary>
    public const int ClaudeCodeSeconds = 110;

    /// <summary>
    /// Longest for Codex without a <c>tool_timeout_sec</c> for mbus in its config (Codex's default is 60 s; with one,
    /// the local server uses it less a margin, the original issue) and for clients whose limit isn't known.
    /// </summary>
    public const int OtherClientSeconds = 55;
}

/// <summary>Input of <c>fetch_attachment</c> (local server only).</summary>
public sealed record FetchAttachmentInput
{
    /// <summary>The thread the attachment is on.</summary>
    public required string ThreadId { get; init; }

    /// <summary>The attachment id (<c>att_…</c>), from the message's <c>attachments</c>.</summary>
    public required string AttachmentId { get; init; }

    /// <summary>The delivery, when the attachment came with a task you were given (from <c>inbox</c>).</summary>
    public string? DeliveryId { get; init; }

    /// <summary>For a git bundle: the repository to fetch it into (default the current directory).</summary>
    public string? Repository { get; init; }

    /// <summary>For a git bundle: the ref to fetch into (default <c>refs/remotes/mbus/{branch}</c>).</summary>
    public string? Into { get; init; }

    /// <summary>For a patch or file: where to save it (default a new temporary folder).</summary>
    public string? Path { get; init; }
}

/// <summary>Result of <c>fetch_attachment</c>.</summary>
public sealed record FetchAttachmentResult
{
    /// <summary>The attachment.</summary>
    public required string AttachmentId { get; init; }

    /// <summary>What it was.</summary>
    public required AttachmentFormat Format { get; init; }

    /// <summary>For a git bundle: the commit fetched.</summary>
    public string? Commit { get; init; }

    /// <summary>For a git bundle: the ref it was fetched into.</summary>
    public string? Ref { get; init; }

    /// <summary>For a patch or file: where it was saved.</summary>
    public string? Path { get; init; }
}
