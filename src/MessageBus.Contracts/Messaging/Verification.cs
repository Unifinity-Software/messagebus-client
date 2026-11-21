using System.Text.Json.Serialization;

namespace MessageBus.Contracts.Messaging;

/// <summary>A sender's required verification for a role-targeted task.</summary>
public sealed record VerificationRequest
{
    /// <summary>The workspace's approved environment identifier.</summary>
    public required string Environment { get; init; }

    /// <summary>Required check keys, normalized and pinned by the service.</summary>
    public required IReadOnlyList<string> RequiredChecks { get; init; }
}

/// <summary>The service's immutable verification definition for one runner delivery.</summary>
public sealed record VerificationSnapshot
{
    /// <summary>The approved workspace environment identifier.</summary>
    public required string Environment { get; init; }

    /// <summary>The fixed execution recipe, initially <c>node-npm-ci</c>.</summary>
    public required string Recipe { get; init; }

    /// <summary>The approved environment version.</summary>
    public required string Version { get; init; }

    /// <summary>The digest of the effective immutable environment definition.</summary>
    public required string Digest { get; init; }

    /// <summary>The exact Node version approved for this delivery.</summary>
    public required string NodeVersion { get; init; }

    /// <summary>The exact npm version approved for this delivery.</summary>
    public required string NpmVersion { get; init; }

    /// <summary>The trusted repository identity assigned by the service.</summary>
    public required string Repository { get; init; }

    /// <summary>The service-authorized HTTPS remote used to establish provenance.</summary>
    public required string Remote { get; init; }

    /// <summary>The full immutable input commit.</summary>
    public required string Revision { get; init; }

    /// <summary>The full immutable base commit.</summary>
    public required string BaseRevision { get; init; }

    /// <summary>The normalized required check keys.</summary>
    public required IReadOnlyList<string> RequiredChecks { get; init; }
}

/// <summary>Verification progress, independent of task completion.</summary>
public enum VerificationState
{
    /// <summary>Verification is still due.</summary>
    [JsonStringEnumMemberName("pending")] Pending,
    /// <summary>A required environment or check is unavailable or denied.</summary>
    [JsonStringEnumMemberName("blocked")] Blocked,
    /// <summary>A required check ran and failed.</summary>
    [JsonStringEnumMemberName("failed")] Failed,
    /// <summary>Every required check ran and passed.</summary>
    [JsonStringEnumMemberName("passed")] Passed,
}

/// <summary>The recorded verification for one task or attempt.</summary>
public sealed record VerificationResult
{
    /// <summary>The verification state.</summary>
    public required VerificationState State { get; init; }

    /// <summary>The workspace environment identifier.</summary>
    public required string Environment { get; init; }

    /// <summary>A reason for the current state, when available.</summary>
    public string? Reason { get; init; }

    /// <summary>The immutable commit that was checked, when available.</summary>
    public string? Revision { get; init; }

    /// <summary>Outcomes for individual required checks, when available.</summary>
    public IReadOnlyList<VerificationCheck>? Checks { get; init; }
}

/// <summary>The recorded outcome for one required check.</summary>
public sealed record VerificationCheck
{
    /// <summary>The stable check key.</summary>
    public required string Name { get; init; }

    /// <summary>The check's state.</summary>
    public required VerificationState State { get; init; }

    /// <summary>A reason for the check's state, when available.</summary>
    public string? Reason { get; init; }
}
