using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;

namespace MessageBus.Contracts.Mcp;

/// <summary>Change the recipient of a queued task without changing its thread or project.</summary>
public sealed record RetargetInput
{
    /// <summary>The thread containing the queued task.</summary>
    public required string ThreadId { get; init; }
    /// <summary>The new role, agent or capabilities target; inbox targets are refused.</summary>
    public required MessageTarget To { get; init; }
    /// <summary>An optional timeline reason, at most 500 characters.</summary>
    public string? Reason { get; init; }
    /// <summary>Preview routing and resealing requirements without changing or uploading anything.</summary>
    public bool DryRun { get; init; }
    /// <summary>The current task message to replace; required for a mutation to prevent stale previews.</summary>
    public string? ExpectedTaskMessageId { get; init; }
    /// <summary>A caller-generated idempotency id; required for a mutation and retained for retries.</summary>
    public string? OperationId { get; init; }
    /// <summary>A replacement encrypted body, filled by the local client only when its recipient key changes.</summary>
    public SealedBody? SealedBody { get; init; }
    /// <summary>The ordered original references and resealed replacements, filled by the local client.</summary>
    public IReadOnlyList<AttachmentRef>? Attachments { get; init; }
}

/// <summary>The outcome or preview of a same-thread queued retarget.</summary>
public sealed record RetargetResult
{
    /// <summary>The unchanged thread.</summary>
    public required string ThreadId { get; init; }
    /// <summary>The replacement task message (the current message in a preview).</summary>
    public required string MessageId { get; init; }
    /// <summary>The task message being replaced.</summary>
    public required string PreviousMessageId { get; init; }
    /// <summary>The previous recipient.</summary>
    public required MessageTarget From { get; init; }
    /// <summary>The new recipient.</summary>
    public required MessageTarget To { get; init; }
    /// <summary>The task's status, as reported by the service.</summary>
    public required ThreadStatus Status { get; init; }
    /// <summary>This is a preview, with no mutation.</summary>
    public bool DryRun { get; init; }
    /// <summary>This operation was already committed; the historical outcome is returned.</summary>
    public bool AlreadyApplied { get; init; }
    /// <summary>At least one encrypted input needs resealing on a machine holding its old key.</summary>
    public bool NeedsReseal { get; init; }
    /// <summary>The public destination key, when encrypted inputs need a recipient.</summary>
    public PublicKeyInfo? Recipient { get; init; }
    /// <summary>The number of task copies in the new routing.</summary>
    public int Copies { get; init; }
    /// <summary>Eligible agents currently connected.</summary>
    public int AgentsOnline { get; init; }
    /// <summary>Eligible connected agents with a free slot.</summary>
    public int AgentsFree { get; init; }
    /// <summary>The task's place in its destination queue, when known.</summary>
    public int? QueuePosition { get; init; }
    /// <summary>Routing or independent-task settings the caller should know about.</summary>
    public string? Notice { get; init; }
}

/// <summary>Optional retarget information on an existing thread message event.</summary>
public sealed record RetargetMetadata
{
    /// <summary>The task message replaced by this event's message.</summary>
    public required string PreviousMessageId { get; init; }
    /// <summary>The previous recipient.</summary>
    public required MessageTarget From { get; init; }
    /// <summary>The new recipient.</summary>
    public required MessageTarget To { get; init; }
    /// <summary>The caller's optional reason.</summary>
    public string? Reason { get; init; }
}
