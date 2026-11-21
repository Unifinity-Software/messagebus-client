using MessageBus.Contracts.Roles;

namespace MessageBus.Contracts.Mcp;

/// <summary>Request an editable human role review. Session provenance is filled by the local host.</summary>
public sealed record ProposeRoleInput
{
    /// <summary>Create or update; deletion is unsupported.</summary>
    public required RoleProposalOperation Operation { get; init; }
    /// <summary>Complete requested role definition.</summary>
    public required RoleDefinitionV1 Definition { get; init; }
    /// <summary>Why the change is needed.</summary>
    public required string Rationale { get; init; }
    /// <summary>Retry key; changed content with the same key conflicts.</summary>
    public required string IdempotencyKey { get; init; }
}

/// <summary>Read a proposal bound to this original local coordinator record.</summary>
public sealed record GetRoleProposalInput
{
    /// <summary>The proposal id.</summary>
    public required string ProposalId { get; init; }
}

/// <summary>Wait at most 30 seconds for authoritative proposal state.</summary>
public sealed record WaitRoleProposalInput
{
    /// <summary>The proposal id.</summary>
    public required string ProposalId { get; init; }
    /// <summary>Bounded wait, 0–30 seconds. Pending at the deadline is not rejection.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
