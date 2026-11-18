namespace MessageBus.Contracts.Messaging;

/// <summary>A short, encrypted explanation of what made a task harder.</summary>
public sealed record FrictionNote
{
    /// <summary>One of the fixed FrictionKinds.</summary>
    public required string Kind { get; init; }
    /// <summary>One sentence, at most 500 characters. Never sent as service metadata.</summary>
    public required string Note { get; init; }
}

/// <summary>The fixed vocabulary shared by local tools and service metadata.</summary>
public static class FrictionKinds
{
    /// <summary>Whether a kind belongs to the fixed vocabulary.</summary>
    public static bool IsValid(string? kind) => kind is
        "code-mismatch" or "build-or-test" or "refused" or "missing-context" or "merge-or-rebase" or "other";

    /// <summary>Counts notes, including multiple notes of the same kind.</summary>
    public static IReadOnlyDictionary<string, long> Count(IEnumerable<string> kinds) =>
        kinds.GroupBy(k => k, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.LongCount(), StringComparer.Ordinal);
}
