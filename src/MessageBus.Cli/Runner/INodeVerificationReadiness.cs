namespace MessageBus.Cli.Runner;

/// <summary>
/// Adapter supplied by the shared protected-execution lifecycle (#496), scoped to the current
/// runner session and Node recipe. There is deliberately no production implementation yet.
/// </summary>
internal interface INodeVerificationReadiness
{
    /// <summary>
    /// True only while negotiated session/generation/recipe/policy/proof readiness is current,
    /// unexpired and not revoked, including bounded storage, authenticated receipts and verified
    /// cleanup. This is re-read at enrollment, inventory and delivery admission; it does not
    /// replace the shared lifecycle's per-delivery acknowledgment or child-launch authority.
    /// </summary>
    bool IsReady { get; }
}
