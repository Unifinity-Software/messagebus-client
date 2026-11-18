using MessageBus.Cli.Commands;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Machines;
using MessageBus.Mcp;

namespace MessageBus.Cli.Readiness;

/// <summary>A fresh role-key check on a scan or reconnect, separate from whole-machine readiness.</summary>
internal static class RoleKeyChecks
{
    internal static async Task<MachineInventory> CheckAsync(CommandContext context, MachineInventory inventory, CancellationToken ct)
    {
        var readable = await ReadAsync(context, ct).ConfigureAwait(false);
        return readable is null ? inventory : inventory with { RoleKeysReadable = readable };
    }

    internal static async Task<IReadOnlyDictionary<string, bool>?> ReadAsync(CommandContext context, CancellationToken ct)
    {
        if (context.Settings.Stored is not { WorkspaceId: { } workspace, MachineId: { } machine }
            || context.Settings.ApiKey is not null) return null;
        try
        {
            using var http = ServerConnection.CreateAuthenticatedClient(context.Host, context.Settings, context.Store, "runner");
            using var keys = new MachineKeys(http, workspace, machine, context.Store.Directory,
                context.Host.Error, context.Host.Time, context.Store.LoadKeyPem);
            return await keys.RoleReadinessAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await context.Host.Error.WriteLineAsync($"Couldn't check role keys: {ex.Message}");
            return null; // An unavailable check must not clear an earlier refusal.
        }
    }
}
