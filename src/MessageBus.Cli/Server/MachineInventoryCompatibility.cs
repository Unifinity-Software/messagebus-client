using MessageBus.Contracts.Machines;

namespace MessageBus.Cli.Server;

/// <summary>
/// Capability-gates <see cref="AgentKind.Antigravity"/> on <see cref="MachineInventory"/> uploads: the client sends
/// <c>antigravity</c> and <c>agent:antigravity</c> only when the target server advertises
/// <see cref="CapabilityToken"/> in <c>GET /api/server</c>'s <c>capabilities</c> array. Services deployed before
/// <see cref="AgentKind.Antigravity"/> existed receive the compatible agent list on the first request without retries.
/// </summary>
internal static class MachineInventoryCompatibility
{
    /// <summary>Capability token advertised by <c>GET /api/server</c> once the service accepts <see cref="AgentKind.Antigravity"/>.</summary>
    public const string CapabilityToken = "agentKindAntigravityV1";

    private const string AntigravityCapability = "agent:antigravity";

    public static bool SupportsAntigravity(ServerInfoResponse? info) =>
        info?.Capabilities is { } capabilities
        && capabilities.Contains(CapabilityToken, StringComparer.Ordinal);

    public static bool HasAntigravity(MachineInventory inventory) =>
        inventory.Agents.Any(a => a.Kind == AgentKind.Antigravity)
        || inventory.Capabilities.Any(c => string.Equals(c, AntigravityCapability, StringComparison.OrdinalIgnoreCase));

    public static MachineInventory ForServer(MachineInventory inventory, bool supportsAntigravity) =>
        supportsAntigravity || !HasAntigravity(inventory) ? inventory : WithoutAntigravity(inventory);

    public static MachineInventory WithoutAntigravity(MachineInventory inventory) => inventory with
    {
        Agents = [.. inventory.Agents.Where(a => a.Kind != AgentKind.Antigravity)],
        Capabilities = [.. inventory.Capabilities.Where(c => !string.Equals(c, AntigravityCapability, StringComparison.OrdinalIgnoreCase))],
    };
}
