using System.Text.Json.Serialization;

namespace MessageBus.Contracts.Messaging;

/// <summary>
/// One thread: a task (its first message) and everything said about it. Threads form a tree: a task
/// sent while working on another becomes its child. The bus keeps no plan; this is a record of
/// what was sent and how it ended.
/// </summary>
public sealed record ThreadSummary
{
    /// <summary>The current verification state, absent for tasks without verification requirements.</summary>
    public VerificationResult? Verification { get; init; }

    /// <summary>Current friction kinds when provided, including an empty list when no kind applies.</summary>
    public IReadOnlyList<string>? CurrentFrictionKinds { get; init; }

    /// <summary>Immutable original execution owner for restricted work and its descendants.</summary>
    public string? ExecutionOwnerSubjectId { get; init; }

    /// <summary>When the current failure was dealt with.</summary>
    public DateTimeOffset? HandledAt { get; init; }
    /// <summary>The person's display name, or retry.</summary>
    public string? HandledBy { get; init; }
    /// <summary>The optional reason for handling the failure.</summary>
    public string? HandledReason { get; init; }
    /// <summary>This caller may handle or undo the current failure.</summary>
    public bool CanHandle { get; init; }
    /// <summary>Setup or task, for a failed task only.</summary>
    public string? FailureCategory { get; init; }

    /// <summary>Thread id (<c>thr_…</c>).</summary>
    public required string ThreadId { get; init; }

    /// <summary>The workspace it belongs to.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>The project it is part of, if any.</summary>
    public string? ProjectId { get; init; }

    /// <summary>The thread this one was started from, if any.</summary>
    public string? ParentThreadId { get; init; }

    /// <summary>Threads started from this one.</summary>
    public IReadOnlyList<string> ChildThreadIds { get; set; } = [];

    /// <summary>The first message's id.</summary>
    public required string RootMessageId { get; init; }

    /// <summary>The first message's type.</summary>
    public required string Type { get; init; }

    /// <summary>The first message's title.</summary>
    public string? Title { get; init; }

    /// <summary>The first message's target.</summary>
    public required MessageTarget Target { get; init; }

    /// <summary>Who started it.</summary>
    public required SenderIdentity Sender { get; init; }

    /// <summary>What the sender expects back.</summary>
    public Expect? Expect { get; init; }

    /// <summary>Where it stands.</summary>
    public required ThreadStatus Status { get; init; }

    /// <summary>When it was started.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it last changed.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When it reached a final status, if it has.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The agent working on it (or that finished it).</summary>
    public string? AgentId { get; init; }

    /// <summary>That agent's machine.</summary>
    public string? MachineId { get; init; }

    /// <summary>How many times it has been delivered.</summary>
    public int Attempts { get; init; }

    /// <summary>Hop count of the first message.</summary>
    public int HopCount { get; init; }

    /// <summary>Number of messages on the thread.</summary>
    public int MessageCount { get; init; }

    /// <summary>The latest progress report, if any.</summary>
    public ProgressUpdate? LastProgress { get; init; }

    /// <summary>The settle result, once settled, failed or cancelled by the agent.</summary>
    public SettleResult? Result { get; init; }

    /// <summary>The last error seen (failed attempt, dead-letter reason text).</summary>
    public string? LastError { get; init; }

    /// <summary>Why it was dead-lettered, when it was.</summary>
    public DeadLetterReason? DeadLetterReason { get; init; }

    /// <summary>The join it is waiting on, when <see cref="Status"/> is <see cref="ThreadStatus.WaitingOnJoin"/>.</summary>
    public JoinSpec? Join { get; init; }

    /// <summary>When it must finish by, if a deadline was set.</summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>Total stored body size of the thread's messages, in bytes (ciphertext when encrypted).</summary>
    public long BodyBytes { get; init; }

    /// <summary>Total size of the thread's attachments, in bytes.</summary>
    public long AttachmentBytes { get; init; }

    /// <summary>
    /// The model the task asked for (<c>send</c>'s <c>model</c>, the original issue); null when it named none, so it ran on the role's or the agent's default (not a mismatch). The model that ran is
    /// <see cref="SettleResult.Usage"/>'s, so a mismatch (a role's cheaper model, the original issue) shows side by side (the original issue).
    /// </summary>
    public string? RequestedModel { get; init; }

    /// <summary>
    /// For a task sent to several agents at once (broadcast, first-N): how each copy went, oldest
    /// copy first. Filled when one thread is read (status, the Threads page) and by
    /// <c>wait</c>; empty in thread lists and for single-copy threads. A finished multi-copy thread's
    /// <see cref="Result"/> is its best copy's, with its summary starting "N of M copies succeeded."
    /// </summary>
    public IReadOnlyList<ThreadCopy> Copies { get; set; } = [];
}

/// <summary>One copy of a task sent to several agents: who took it and how it went.</summary>
public sealed record ThreadCopy
{
    /// <summary>In wait results, this finished copy was not in seenCopies.</summary>
    public bool New { get; init; }

    /// <summary>The copy's delivery (<c>dlv_…</c>).</summary>
    public required string DeliveryId { get; init; }

    /// <summary>The agent that took it (or it was made for).</summary>
    public string? AgentId { get; init; }

    /// <summary>That agent's machine.</summary>
    public string? MachineId { get; init; }

    /// <summary>
    /// Where the copy stands: queued, working, settled (succeeded), failed, cancelled or
    /// dead-lettered (gave up after its attempts).
    /// </summary>
    public required ThreadStatus Status { get; init; }

    /// <summary>How many times it was delivered.</summary>
    public int Attempts { get; init; }

    /// <summary>The copy's result once it ended: summary, error, and the agent and model in <see cref="SettleResult.Usage"/>.</summary>
    public SettleResult? Result { get; init; }

    /// <summary>The last error seen on this copy (a failed attempt, why it was given up).</summary>
    public string? LastError { get; init; }
}

/// <summary>
/// How a task ended. Comes from the runner (process exit) or the agent's <c>settle</c> call, never
/// from a claim in message text. The dashboard and project totals are built from these.
/// </summary>
public sealed record SettleResult
{
    /// <summary>Plain friction metadata (one kind per encrypted note); no note text.</summary>
    public IReadOnlyList<string> FrictionKinds { get; set; } = [];

    /// <summary>How it ended.</summary>
    public required SettleStatus Status { get; init; }

    /// <summary>One or two sentences on what was done (plain text, shown on the dashboard).</summary>
    public string? Summary { get; init; }

    /// <summary>What it produced: PRs, commits, branches, files, attachments.</summary>
    public IReadOnlyList<Ref> Refs { get; set; } = [];

    /// <summary>Model usage and time for the run, as reported by the agent CLI (an estimate).</summary>
    public Usage? Usage { get; init; }

    /// <summary>The agent process's exit code, when the runner settled it.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Runner-only reason no work was attempted: not-started, unreadable or refused; null for a real attempt.</summary>
    public string? FailureCategory { get; init; }

    /// <summary>Error text for failed or abandoned runs.</summary>
    public string? Error { get; init; }
}

/// <summary>What one run used. Self-reported by the runner from the agent CLI's output; always labelled an estimate.</summary>
public sealed record Usage
{
    /// <summary>Per-model token counts when a session used several models; the other count fields remain aggregate totals.</summary>
    public IReadOnlyList<Usage>? Models { get; init; }

    /// <summary>The agent that ran it.</summary>
    public Machines.AgentKind? AgentKind { get; init; }

    /// <summary>Model vendor, for example <c>anthropic</c> or <c>openai</c>.</summary>
    public string? Vendor { get; init; }

    /// <summary>Model name as the CLI reported it.</summary>
    public string? Model { get; init; }

    /// <summary>Input tokens, cached reads included.</summary>
    public long TokensIn { get; init; }

    /// <summary>
    /// Of <see cref="TokensIn"/>, the tokens read from the model's prompt cache, which cost less (the original issue): Claude
    /// Code's cache reads, Codex's <c>cached_input_tokens</c>. Zero when the CLI doesn't say.
    /// </summary>
    public long CachedTokensIn { get; init; }

    /// <summary>
    /// Of <see cref="TokensIn"/>, the tokens written to the prompt cache, which cost more than normal input (the original issue):
    /// Claude's cache writes. Zero when the CLI doesn't say (Codex doesn't charge for them).
    /// </summary>
    public long CacheWriteTokensIn { get; init; }

    /// <summary>
    /// Of <see cref="CacheWriteTokensIn"/>, those written to Claude's one-hour cache, which cost more than five-minute
    /// writes. Zero when the CLI doesn't say which: every write is then priced as a five-minute write.
    /// </summary>
    public long CacheWrite1hTokensIn { get; init; }

    /// <summary>Output tokens.</summary>
    public long TokensOut { get; init; }

    /// <summary>Estimated cost in US dollars, when known.</summary>
    public decimal? EstimatedCostUsd { get; init; }

    /// <summary>Time the agent was actually running, in seconds.</summary>
    public double ActiveSeconds { get; init; }
}

/// <summary>A progress report on a task, from the agent (<c>progress</c> tool) or its runner.</summary>
public sealed record ProgressUpdate
{
    /// <summary>What is happening now, one line.</summary>
    public required string Message { get; init; }

    /// <summary>Rough completion, 0–100, if the agent can tell.</summary>
    public int? Percent { get; init; }

    /// <summary>When it was reported.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// Set by a runner whose task waits for something only the sender's mbus can give (the original issue): the sender's mbus answers it
    /// by itself, the model never sees it. Null for an ordinary report.
    /// </summary>
    public ProgressRequest? Request { get; init; }
}

/// <summary>What a run waits for from the sender's mbus (<see cref="ProgressUpdate.Request"/>, the original issue).</summary>
public sealed record ProgressRequest
{
    /// <summary>What it needs: <see cref="ProgressRequestKinds.GitHistory"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The task's attachment it is about (a git bundle), when the task has more than one.</summary>
    public string? AttachmentId { get; init; }

    /// <summary>Where to send the answer: an inbox the run opened for it.</summary>
    public required MessageTarget ReplyTo { get; init; }
}

/// <summary>Values of <see cref="ProgressRequest.Kind"/>, and the message types that answer them.</summary>
public static class ProgressRequestKinds
{
    /// <summary>
    /// The whole history of the branch the task's git bundle carries: the bundle holds only the commits after one the
    /// runner was expected to have, and its copy of the repository doesn't have it.
    /// </summary>
    public const string GitHistory = "git-history";

    /// <summary>The type of the message that answers <see cref="GitHistory"/>: a git bundle of the branch's whole history.</summary>
    public const string GitHistoryMessageType = "mbus.git-history";
}

/// <summary>
/// A join: deliver one combined result when enough of a set of child threads finish, or the
/// timeout passes. Lets an orchestrator end its run and be woken with the results.
/// </summary>
public sealed record JoinSpec
{
    /// <summary>Wait for any one, or all.</summary>
    public WaitMode Mode { get; set; } = WaitMode.All;

    /// <summary>The threads joined; filled in by the service for <c>send_many</c>.</summary>
    public IReadOnlyList<string> ThreadIds { get; set; } = [];

    /// <summary>Where the combined result goes; defaults to the sender.</summary>
    public MessageTarget? To { get; init; }

    /// <summary>Give up waiting after this many seconds and deliver what has finished.</summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// Something that happened on a thread, as streamed by the <c>watch</c> tool and shown on the
/// dashboard timeline. On the wire it has a <c>kind</c> discriminator.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ThreadStatusChanged), ThreadStatusChanged.Kind)]
[JsonDerivedType(typeof(ThreadProgressed), ThreadProgressed.Kind)]
[JsonDerivedType(typeof(ThreadMessageAdded), ThreadMessageAdded.Kind)]
[JsonDerivedType(typeof(ThreadSettled), ThreadSettled.Kind)]
[JsonDerivedType(typeof(ThreadMoved), ThreadMoved.Kind)]
public abstract record ThreadEvent
{
    /// <summary>Event id (<c>evt_…</c>); ids sort in the order events happened, so the last one seen is a cursor.</summary>
    public required string EventId { get; init; }

    /// <summary>The thread.</summary>
    public required string ThreadId { get; init; }

    /// <summary>The thread's project, if any.</summary>
    public string? ProjectId { get; init; }

    /// <summary>When it happened.</summary>
    public required DateTimeOffset At { get; init; }
}

/// <summary>The thread moved to a new status.</summary>
public sealed record ThreadStatusChanged : ThreadEvent
{
    /// <summary>Discriminator: <c>status</c>.</summary>
    public const string Kind = "status";

    /// <summary>The new status.</summary>
    public required ThreadStatus Status { get; init; }

    /// <summary>Why, when there's more to say (for example the dead-letter reason or who cancelled it).</summary>
    public string? Reason { get; init; }

    /// <summary>The agent involved, if any.</summary>
    public string? AgentId { get; init; }

    /// <summary>The machine involved, if any.</summary>
    public string? MachineId { get; init; }
}

/// <summary>The agent reported progress.</summary>
public sealed record ThreadProgressed : ThreadEvent
{
    /// <summary>Discriminator: <c>progress</c>.</summary>
    public const string Kind = "progress";

    /// <summary>The report.</summary>
    public required ProgressUpdate Progress { get; init; }
}

/// <summary>A message was added to the thread (a reply, or a follow-up).</summary>
public sealed record ThreadMessageAdded : ThreadEvent
{
    /// <summary>Discriminator: <c>message</c>.</summary>
    public const string Kind = "message";

    /// <summary>The new message's id.</summary>
    public required string MessageId { get; init; }

    /// <summary>Its type.</summary>
    public required string Type { get; init; }

    /// <summary>Its title.</summary>
    public string? Title { get; init; }

    /// <summary>Who sent it.</summary>
    public required SenderIdentity Sender { get; init; }
}

/// <summary>The task was settled.</summary>
public sealed record ThreadSettled : ThreadEvent
{
    /// <summary>Discriminator: <c>settled</c>.</summary>
    public const string Kind = "settled";

    /// <summary>The result.</summary>
    public required SettleResult Result { get; init; }
}

/// <summary>
/// The thread was moved to another project, or into or out of one (the original issue), with its replies (and, when asked, its
/// sub-tasks). <see cref="ThreadEvent.ProjectId"/> is the project it is in from now on.
/// </summary>
public sealed record ThreadMoved : ThreadEvent
{
    /// <summary>Discriminator: <c>moved</c>.</summary>
    public const string Kind = "moved";

    /// <summary>The project it was in; null when it was in none.</summary>
    public string? FromProjectId { get; init; }

    /// <summary>That project's title when it moved.</summary>
    public string? FromTitle { get; init; }

    /// <summary>The project it is in now; null when it was taken out of its project.</summary>
    public string? ToProjectId { get; init; }

    /// <summary>That project's title when it moved.</summary>
    public string? ToTitle { get; init; }

    /// <summary>Who moved it, as the dashboard names them.</summary>
    public string? MovedBy { get; init; }

    /// <summary>Why, when they said.</summary>
    public string? Reason { get; init; }
}

/// <summary>One delivery: a copy of a message offered to one agent, and its lease.</summary>
public sealed record DeliveryInfo
{
    /// <summary>Delivery id (<c>dlv_…</c>). Settle, progress, heartbeat and cancel all name it.</summary>
    public required string DeliveryId { get; init; }

    /// <summary>The message delivered.</summary>
    public required string MessageId { get; init; }

    /// <summary>Its thread.</summary>
    public required string ThreadId { get; init; }

    /// <summary>The agent it went to.</summary>
    public string? AgentId { get; init; }

    /// <summary>That agent's machine.</summary>
    public string? MachineId { get; init; }

    /// <summary>The role it was delivered for, if any.</summary>
    public string? Role { get; init; }

    /// <summary>Which delivery attempt this is (1 for the first).</summary>
    public required int Attempt { get; init; }

    /// <summary>Its state.</summary>
    public required DeliveryStatus Status { get; init; }

    /// <summary>When the lease ends unless a heartbeat extends it.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }
}

/// <summary>A dead-lettered message, as listed on the Dead letters page. Kept <see cref="MessageLimits.DeadLetterRetentionDays"/> days.</summary>
public sealed record DeadLetter
{
    /// <summary>Opaque id for replaying or discarding it (set by the service when listing).</summary>
    public string? Id { get; init; }

    /// <summary>The message.</summary>
    public required MessageEnvelope Envelope { get; init; }

    /// <summary>The role whose dead letters it is in (null for capability or direct targets).</summary>
    public string? Role { get; init; }

    /// <summary>Why.</summary>
    public required DeadLetterReason Reason { get; init; }

    /// <summary>The last error reported by an attempt, if any.</summary>
    public string? LastError { get; init; }

    /// <summary>How many times it was delivered.</summary>
    public int Attempts { get; init; }

    /// <summary>The agent that last tried it (null when no agent ever took it).</summary>
    public string? LastAgentId { get; init; }

    /// <summary>The machine of the agent that last tried it.</summary>
    public string? LastMachineId { get; init; }

    /// <summary>When it was dead-lettered.</summary>
    public required DateTimeOffset DeadLetteredAt { get; init; }

    /// <summary>When it will be deleted.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Body of <c>POST /api/w/{ws}/deadletters/{id}/replay</c> (the original issue). Empty to send the message again to where it went.
/// </summary>
public sealed record ReplayDeadLetterInput
{
    /// <summary>The role to send it to instead of its own; null for its own.</summary>
    public string? ToRole { get; init; }

    /// <summary>
    /// For an end-to-end encrypted message going to another role: its body, opened and sealed again for that
    /// role's key by a machine that holds the original role's key (<c>mbus deadletters replay</c>). The service
    /// can't do it: it never holds the plaintext, so without this a replay to another role is refused.
    /// </summary>
    public Mcp.SealedBody? SealedBody { get; init; }

    /// <summary>
    /// With <see cref="SealedBody"/> (the original issue): the message's attachments in the same order, each encrypted one downloaded,
    /// opened and uploaded again sealed for the new role's key by that machine; the rest as they were.
    /// </summary>
    public IReadOnlyList<AttachmentRef>? Attachments { get; init; }
}
