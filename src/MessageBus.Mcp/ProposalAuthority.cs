namespace MessageBus.Mcp;

/// <summary>Trusted local host origin, independent of request arguments and role tool grants.</summary>
public enum ProposalAuthority
{
    /// <summary>Missing or invalid provenance; never grants access.</summary>
    Unknown,
    /// <summary>A local host started outside any runner task.</summary>
    Interactive,
    /// <summary>The runner verified the exact original interactive session and its log.</summary>
    ResumedCoordinator,
    /// <summary>A task worker, including a worker with wildcard tools or an inbox.</summary>
    Worker,
}
