using System.Security.Cryptography;
using System.Text.Json;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Keys;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;

namespace MessageBus.Cli.Readiness;

/// <summary>The runner and doctor check the current workspace key before offering agents for work. Never changes trust.</summary>
internal static class WorkspaceKeyReadiness
{
    internal readonly record struct CheckResult(string? Problem, bool CouldNotCheck = false);

    public static async Task<CheckResult> CheckAsync(CommandContext context, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (context.Settings.Server is null || !context.Settings.CanAuthenticate(context.Store)
                || context.Settings.Stored.WorkspaceId is not { } workspace) return default;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var http = ServerConnection.CreateAuthenticatedClient(context.Host, context.Settings, context.Store, "cli");
            using var response = await http.GetAsync($"api/w/{Uri.EscapeDataString(workspace)}/keys", timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var keys = MessageBusJson.Deserialize<IReadOnlyList<PublicKeyInfo>>(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false))
                ?? throw new JsonException("The server returned no key directory.");
            var directory = new KeyDirectory(keys);
            if (directory.Current(KeyScope.Workspace) is not { } key) return default; // No key yet: the first sender creates it.
            using var own = context.Store.LoadKeyPem() is { } pem ? KeyPair.FromPem(pem) : null;
            var ownId = keys.Where(k => k.Scope == KeyScope.Machine && k.Owner == context.Settings.Stored.MachineId
                && k.Fingerprint == own?.PublicKey.Fingerprint).OrderByDescending(k => k.Version).FirstOrDefault()?.KeyId;
            var pins = KeyTrust.Load(context.Store.Directory, workspace, http.BaseAddress!.AbsoluteUri);
            var trust = KeyTrust.Check(pins, key, directory, workspace, ownId);
            // A run opens this computer's grants before checking the current key. A valid grant
            // can vouch for a replacement even when its admin computer is new here.
            if (trust.Status == TrustStatus.Changed && own is not null && ownId is not null
                && context.Settings.Stored.MachineId is { } machine)
            {
                using var grantsResponse = await http.GetAsync($"api/w/{Uri.EscapeDataString(workspace)}/keys/grants?machineId={Uri.EscapeDataString(machine)}", timeout.Token).ConfigureAwait(false);
                grantsResponse.EnsureSuccessStatusCode();
                var grants = MessageBusJson.Deserialize<IReadOnlyList<KeyGrant>>(await grantsResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false))
                    ?? throw new JsonException("The server returned no key grants.");
                foreach (var grant in grants.Where(g => g.RevokedAt is null && g.MachineId == machine && directory.For(g)?.KeyId == key.KeyId))
                {
                    if (directory.Find(grant.SigningKeyId) is not { } signer
                        || KeyTrust.CheckSigner(pins, signer, ownId).Status == TrustStatus.Changed
                        || pins.Keys.GetValueOrDefault(grant.Role)?.Any(k => k.Version == key.Version && k.Fingerprint != key.Fingerprint) == true) continue;
                    try
                    {
                        using var opened = GrantCrypto.Open(grant, workspace, key, signer, new DeviceKeyRing(ownId, own));
                        return default; // The run still has to open and remember it; checking never saves trust.
                    }
                    catch (CryptographicException) { } // A real run ignores grants it cannot open.
                }
            }
            return trust.Status == TrustStatus.Changed
                ? new CheckResult($"This runner's computer hasn't accepted the current workspace key. {trust.Problem} Fingerprint: {KeysCommand.Grouped(key.Fingerprint)}. Compare it with the Security page, or mbus keys list on an admin's computer, before running mbus keys trust workspace on this runner's computer, then restart the runner, or Rescan on the Runners page.")
                : default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var note = $"This runner's computer couldn't check the current workspace key ({ex.GetType().Name}). Check its connection and sign-in, then restart the runner, or Rescan on the Runners page.";
            return new CheckResult(note, CouldNotCheck: true);
        }
    }

    private sealed class DeviceKeyRing(string keyId, KeyPair key) : IKeyRing
    {
        public KeyPair? Find(string id) => id == keyId ? key : null;
    }

    /// <summary>Blocks only a definite changed key; an incomplete check leaves the scanned readiness intact.</summary>
    public static MachineInventory Apply(MachineInventory inventory, CheckResult check)
    {
        if (check.CouldNotCheck || check.Problem is not { } problem) return inventory;
        var agents = inventory.Agents.Select(a => a.Found ? a with
        {
            NotReady = a.NotReady is null ? problem : problem + " " + a.NotReady,
        } : a).ToList();
        var blocked = agents.Where(a => a.Found).Select(a => CapabilityNames.Format(CapabilityNames.AgentPrefix,
            MessageBus.Cli.Runner.Agents.AgentKinds.Name(a.Kind))).ToHashSet(StringComparer.Ordinal);
        return inventory with { Agents = agents, Capabilities = [.. inventory.Capabilities.Where(c => !blocked.Contains(c))] };
    }
}
