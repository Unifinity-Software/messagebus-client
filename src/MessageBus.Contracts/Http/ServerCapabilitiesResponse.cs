namespace MessageBus.Contracts.Http;

/// <summary>The capability subset of the actual target's <c>GET /api/server</c> response.</summary>
public sealed record ServerCapabilitiesResponse
{
    /// <summary>The server product; must equal <c>messagebus.ai</c> before trusting capabilities.</summary>
    public string? Product { get; init; }

    /// <summary>Optional supported protocol features. Absent or unknown features imply no support.</summary>
    public IReadOnlyList<string>? Capabilities { get; init; }
}
