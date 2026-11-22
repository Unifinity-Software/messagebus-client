using System.Net;
using MessageBus.Contracts.Machines;

namespace MessageBus.Cli.Server;

/// <summary>
/// Keeps <c>MachineInventory</c> uploads compatible with services released before <see cref="AgentKind.Antigravity"/>
/// existed: an older service rejects the unknown <c>"antigravity"</c> enum value with HTTP 400. When that happens,
/// the client retries once without the Antigravity entry so existing agents continue to enroll and report.
/// </summary>
internal static class MachineInventoryCompatibility
{
    private const string AntigravityCapability = "agent:antigravity";

    public static bool HasAntigravity(MachineInventory inventory) =>
        inventory.Agents.Any(a => a.Kind == AgentKind.Antigravity)
        || inventory.Capabilities.Any(c => string.Equals(c, AntigravityCapability, StringComparison.OrdinalIgnoreCase));

    public static MachineInventory WithoutAntigravity(MachineInventory inventory) => inventory with
    {
        Agents = [.. inventory.Agents.Where(a => a.Kind != AgentKind.Antigravity)],
        Capabilities = [.. inventory.Capabilities.Where(c => !string.Equals(c, AntigravityCapability, StringComparison.OrdinalIgnoreCase))],
    };

    public static async Task<bool> IsLegacyInventoryRejectionAsync(HttpResponseMessage response, CancellationToken ct) =>
        response.StatusCode == HttpStatusCode.BadRequest && !await ServerConnection.IsWorkspaceRequiredAsync(response, ct).ConfigureAwait(false);
}
