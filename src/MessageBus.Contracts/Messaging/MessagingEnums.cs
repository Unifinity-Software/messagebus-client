using System.Text.Json.Serialization;

namespace MessageBus.Contracts.Messaging;

/// <summary>What the sender expects back. Absent means nothing (an event or notice).</summary>
public enum Expect
{
    /// <summary>An answer message on the same thread, returned to the sender (request/reply).</summary>
    [JsonStringEnumMemberName("reply")] Reply,
    /// <summary>A settle result: status, summary, refs and usage (a task).</summary>
    [JsonStringEnumMemberName("outcome")] Outcome,
}

/// <summary>Delivery priority. Higher priorities are offered first within a role.</summary>
public enum Priority
{
    /// <summary>After everything else.</summary>
    [JsonStringEnumMemberName("low")] Low,
    /// <summary>The default.</summary>
    [JsonStringEnumMemberName("normal")] Normal,
    /// <summary>Before normal and low.</summary>
    [JsonStringEnumMemberName("high")] High,
}

/// <summary>
/// Where a thread (one task and everything said about it) stands. The dashboard, the <c>status</c>
/// and <c>wait</c> tools and project totals all use these values.
/// </summary>
public enum ThreadStatus
{
    /// <summary>Stored and waiting in a queue for an agent.</summary>
    [JsonStringEnumMemberName("queued")] Queued,
    /// <summary>
    /// Claimed by an agent through <c>inbox</c> and not yet reporting progress. A runner's take goes straight to
    /// <see cref="Working"/> (the original issue).
    /// </summary>
    [JsonStringEnumMemberName("delivered")] Delivered,
    /// <summary>An agent is working on it (progress or heartbeats are arriving).</summary>
    [JsonStringEnumMemberName("working")] Working,
    /// <summary>An answer arrived (for <see cref="Expect.Reply"/>).</summary>
    [JsonStringEnumMemberName("replied")] Replied,
    /// <summary>Finished successfully with a settle result (for <see cref="Expect.Outcome"/>).</summary>
    [JsonStringEnumMemberName("settled")] Settled,
    /// <summary>Finished unsuccessfully: the agent settled it as failed.</summary>
    [JsonStringEnumMemberName("failed")] Failed,
    /// <summary>Moved to the role's dead letters (see <see cref="DeadLetterReason"/>).</summary>
    [JsonStringEnumMemberName("dead-lettered")] DeadLettered,
    /// <summary>Cancelled by the sender, a person, or a cancelled parent thread or project.</summary>
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    /// <summary>Waiting for a set of child threads to finish (a join).</summary>
    [JsonStringEnumMemberName("waiting-on-join")] WaitingOnJoin,
}

/// <summary>How a task ended, as reported by the runner or by the agent's <c>settle</c> call.</summary>
public enum SettleStatus
{
    /// <summary>Done. The thread becomes <see cref="ThreadStatus.Settled"/>.</summary>
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    /// <summary>Ran and failed; not retried. The thread becomes <see cref="ThreadStatus.Failed"/>.</summary>
    [JsonStringEnumMemberName("failed")] Failed,
    /// <summary>
    /// Couldn't finish for a reason that another attempt may fix (the agent crashed, the machine
    /// is shutting down, the run timed out). The message returns to the queue and the attempt counts
    /// toward the role's maximum attempts.
    /// </summary>
    [JsonStringEnumMemberName("abandoned")] Abandoned,
    /// <summary>Stopped because of a cancel request.</summary>
    [JsonStringEnumMemberName("cancelled")] Cancelled,
}

/// <summary>Why a message was moved to dead letters.</summary>
public enum DeadLetterReason
{
    /// <summary>Delivered the role's maximum number of times without settling.</summary>
    [JsonStringEnumMemberName("max-attempts")] MaxAttempts,
    /// <summary>Its lifetime ended before anyone finished it.</summary>
    [JsonStringEnumMemberName("expired")] Expired,
    /// <summary>It was caused by too long a chain of agent-to-agent messages (the workspace's hop limit).</summary>
    [JsonStringEnumMemberName("hop-limit")] HopLimit,
    /// <summary>Its body or an attachment was over the role's limit.</summary>
    [JsonStringEnumMemberName("too-large")] TooLarge,
    /// <summary>No agent holds the role (or matches the capabilities asked for) and none joined in time.</summary>
    [JsonStringEnumMemberName("no-holder")] NoHolder,
    /// <summary>The role had used its daily budget, and it sends new work to dead letters then (the original issue).</summary>
    [JsonStringEnumMemberName("budget-reached")] BudgetReached,
    /// <summary>It names a model its role no longer allows (the role's list changed after it was sent, the original issue).</summary>
    [JsonStringEnumMemberName("model-not-allowed")] ModelNotAllowed,
    /// <summary>The continuation's role is unavailable to its target agent.</summary>
    [JsonStringEnumMemberName("role-not-held")] RoleNotHeld,
}

/// <summary>State of one delivery (one copy of a message offered to one agent).</summary>
public enum DeliveryStatus
{
    /// <summary>Waiting in a queue.</summary>
    [JsonStringEnumMemberName("pending")] Pending,
    /// <summary>Leased to an agent; the lease is kept alive by heartbeats.</summary>
    [JsonStringEnumMemberName("leased")] Leased,
    /// <summary>Settled by the agent or runner.</summary>
    [JsonStringEnumMemberName("settled")] Settled,
    /// <summary>Lease lost or abandoned; the message went back to the queue.</summary>
    [JsonStringEnumMemberName("abandoned")] Abandoned,
    /// <summary>Taken by others first (first-N delivery) or cancelled before it ran.</summary>
    [JsonStringEnumMemberName("withdrawn")] Withdrawn,
}

/// <summary>Which set of threads a <c>wait</c> or a join waits for.</summary>
public enum WaitMode
{
    /// <summary>Return when any one of them finishes.</summary>
    [JsonStringEnumMemberName("any")] Any,
    /// <summary>Return when all of them finish.</summary>
    [JsonStringEnumMemberName("all")] All,
}

/// <summary>The kind of identity that sent a message. Set by the service, never taken from message text.</summary>
public enum SenderKind
{
    /// <summary>A person, from the dashboard or <c>mbus send</c>.</summary>
    [JsonStringEnumMemberName("person")] Person,
    /// <summary>A coding agent on an enrolled machine.</summary>
    [JsonStringEnumMemberName("agent")] Agent,
    /// <summary>A caller using an API key (CI, cloud agents).</summary>
    [JsonStringEnumMemberName("api-key")] ApiKey,
    /// <summary>The service itself (routes, joins, status events).</summary>
    [JsonStringEnumMemberName("system")] System,
}

/// <summary>How an attachment was packaged by <c>mbus attach</c>.</summary>
public enum AttachmentFormat
{
    /// <summary>A <c>git bundle</c> of a branch (full history, buildable).</summary>
    [JsonStringEnumMemberName("git-bundle")] GitBundle,
    /// <summary>A patch (<c>git format-patch</c> or a unified diff).</summary>
    [JsonStringEnumMemberName("patch")] Patch,
    /// <summary>A plain file.</summary>
    [JsonStringEnumMemberName("file")] File,
}
