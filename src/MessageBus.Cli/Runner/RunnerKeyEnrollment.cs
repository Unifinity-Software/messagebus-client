using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Security;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Security;
using MessageBus.Mcp;

namespace MessageBus.Cli.Runner;

/// <summary>
/// A runner container started with a runner API key in <c>MBUS_API_KEY</c> (the original issue): on its first start it makes this
/// machine's key, enrolls with the API key as a shared runner named <c>MBUS_MACHINE_NAME</c> and saves both in mbus's
/// settings folder (keep it on a volume). From then on it signs as that machine and doesn't send the API key at all
/// (<see cref="Configuration.CliConfig.EnrolledWithApiKey"/>), so revoking the key stops new runners, not this one.
/// </summary>
internal static class RunnerKeyEnrollment
{
    /// <summary>Which API key a machine enrolled with, without keeping the key: the start of its SHA-256, lower-case hex.</summary>
    public static string Fingerprint(string apiKey) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey.Trim())))[..16];

    /// <summary>
    /// Enrolls this machine with <c>MBUS_API_KEY</c> unless it already has with that key, then reports its agent scan
    /// signed as the new machine. Returns the settings to carry on with, or null when it didn't enroll (no API key, or
    /// already enrolled with it).
    /// </summary>
    /// <param name="context">The command's settings.</param>
    /// <param name="reload">Reads the settings again (after they are saved).</param>
    /// <param name="log">Status lines.</param>
    /// <param name="onlyRunnerKeys">Ask the server first and leave any other kind of key alone (<c>mbus login</c>); otherwise the server's refusal is the answer.</param>
    /// <param name="ct">Stops it.</param>
    /// <exception cref="CliException">The server refused (not a runner key, revoked, expired).</exception>
    public static async Task<CommandContext?> EnsureAsync(CommandContext context, Func<CommandContext> reload, TextWriter log, bool onlyRunnerKeys, CancellationToken ct)
    {
        var host = context.Host;
        var apiKey = host.GetEnvironmentVariable(EnvVars.ApiKey)?.Trim();
        // CliSettings drops the API key once this machine has enrolled with it.
        if (string.IsNullOrEmpty(apiKey) || (context.Settings.ApiKey is null && context.Store.HasKey) || context.Settings.Server is not { } server)
        {
            return null;
        }
        var settings = context.Settings with { ApiKey = apiKey };
        if (onlyRunnerKeys && !await IsRunnerKeyAsync(context, settings, ct).ConfigureAwait(false))
        {
            return null;
        }

        await log.WriteLineAsync($"Signing this runner in with the API key from {EnvVars.ApiKey}...").ConfigureAwait(false);
        // Validate old trust before enrolling or replacing this machine's identity.
        try
        {
            KeyTrust.BindLegacy(context.Store.Directory, settings.Stored.Server ?? CommandContext.ServerText(server));
        }
        catch (InvalidDataException ex)
        {
            throw new CliException(string.Join(" ", ex.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)));
        }

        using var key = MachineKey.Create();
        var inventory = await AgentScanner.BuildInventoryAsync(host, settings.Stored.MachineId, settings.Location, ct).ConfigureAwait(false);
        using var anonymous = ServerConnection.CreateAnonymousClient(host, server);
        var enrolled = await ServerConnection.EnrollAsync(anonymous, server, new AuthenticationHeaderValue(AuthSchemes.ApiKey, apiKey),
            new MachineEnrollRequest { PublicKey = key.PublicKeyBase64, Name = settings.MachineName, Location = settings.Location, Inventory = inventory }, ct).ConfigureAwait(false);

        context.Store.SaveKeyPem(key.ExportPrivateKeyPem());
        context.Store.Save(settings.Stored with
        {
            Server = CommandContext.ServerText(server),
            MachineId = enrolled.Machine.MachineId,
            WorkspaceId = enrolled.Machine.WorkspaceId,
            WorkspaceName = enrolled.WorkspaceName,
            MachineName = enrolled.Machine.Name,
            Location = enrolled.Machine.Location ?? settings.Location,
            KeyFingerprint = key.Fingerprint,
            EnrolledAt = enrolled.Machine.EnrolledAt,
            EnrolledWithApiKey = Fingerprint(apiKey),
        });
        var signed = reload();
        // The first signed request: proves the saved key works, and files the scan under the new machine.
        using (var client = ServerConnection.CreateAuthenticatedClient(host, signed.Settings, signed.Store, "runner"))
        {
            await ServerConnection.PutInventoryAsync(client, server, inventory with { MachineId = enrolled.Machine.MachineId }, ct).ConfigureAwait(false);
        }
        await log.WriteLineAsync($"This machine is {enrolled.Machine.Name}, a shared runner in the {enrolled.WorkspaceName} workspace."
            + (enrolled.Machine.PendingApproval ? " A workspace admin must approve it before it takes work." : "")).ConfigureAwait(false);
        return signed;
    }

    /// <summary>Whether the key holds the runner scope (<see cref="ApiRoutes.Caller"/>).</summary>
    private static async Task<bool> IsRunnerKeyAsync(CommandContext context, Configuration.CliSettings settings, CancellationToken ct)
    {
        using var client = ServerConnection.CreateAuthenticatedClient(context.Host, settings, context.Store, "cli");
        using var response = await client.GetAsync(ServerConnection.Relative(ApiRoutes.Caller), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return body.RootElement.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array
            && scopes.EnumerateArray().Any(s => s.GetString() == ApiKeyScopes.Runner);
    }
}
