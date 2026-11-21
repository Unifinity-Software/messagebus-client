using System.CommandLine;
using System.Net.Http.Headers;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Keys;
using MessageBus.Cli.Security;
using MessageBus.Cli.Server;
using MessageBus.Cli.SignIn;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Security;
using MessageBus.Mcp;
using Spectre.Console;

namespace MessageBus.Cli.Commands;

/// <summary>
/// <c>mbus login</c>: checks the server, signs the person in through the service (approve in the
/// dashboard's browser page, the original issue; a saved sign-in is reused) or uses a one-time enrollment token,
/// makes this machine's key pair, enrolls the machine with its first agent scan and saves the
/// settings. With <c>MBUS_API_KEY</c> set (a container) there is nothing to sign in to: it saves the
/// server and reports the scan with the key.
/// </summary>
internal static class LoginCommand
{
    public static Command Create(CliHost host, CommonOptions options)
    {
        var location = new Option<string?>("--location") { Description = "Where this machine is, as the dashboard shows it, for example \"Mac mini\"." };
        var name = new Option<string?>("--name") { Description = "This machine's name in the dashboard. Defaults to its host name." };
        var workspace = new Option<string?>("--workspace") { Description = "The workspace to join, if you belong to more than one." };
        var token = new Option<string?>("--token") { Description = "A one-time enrollment token from the dashboard, for a shared runner, instead of signing in." };
        var noBrowser = new Option<bool>("--no-browser") { Description = "Print the sign-in page's address instead of opening a browser." };

        var command = new Command("login", "Sign this machine in to a messagebus.ai server.") { location, name, workspace, token, noBrowser };
        command.SetAction((parse, ct) => RunAsync(
            CommandContext.Load(host, parse, options),
            new LoginChoices(parse.GetValue(location), parse.GetValue(name), parse.GetValue(workspace), parse.GetValue(token),
                !parse.GetValue(noBrowser)),
            () => CommandContext.Load(host, parse, options),
            ct));
        return command;
    }

    private sealed record LoginChoices(string? Location, string? Name, string? Workspace, string? Token, bool OpenBrowser);

    private static async Task<int> RunAsync(CommandContext context, LoginChoices choices, Func<CommandContext> reload, CancellationToken ct)
    {
        try { return await RunCoreAsync(context, choices, reload, ct); }
        catch (ServerBindingException ex) { throw new CliException(ex.Message, 1, ex); }
    }

    private static async Task<int> RunCoreAsync(CommandContext context, LoginChoices choices, Func<CommandContext> reload, CancellationToken ct)
    {
        var host = context.Host;
        var settings = context.Settings;
        var server = settings.Server ?? throw new CliException("Which server? Run: mbus login --server <url>", 2);
        var serverText = CommandContext.ServerText(server);
        var location = string.IsNullOrWhiteSpace(choices.Location) ? settings.Location : choices.Location.Trim();
        var machineName = string.IsNullOrWhiteSpace(choices.Name) ? settings.MachineName : choices.Name.Trim();
        var console = context.Console;

        await context.Store.WithProfileSecurityLockAsync(tx => { tx.DemandEnrollmentTrust(); return Task.FromResult(true); }, ct);

        using var anonymous = ServerConnection.CreateAnonymousClient(host, server);
        var info = await ServerConnection.GetServerInfoAsync(anonymous, server, ct);
        console.MarkupLineInterpolated($"Server: [bold]{serverText}[/] (messagebus.ai {info.Version ?? "unknown version"})");

        // the original issue: a runner API key enrolls this machine as a shared runner, once; any other key is used as it is.
        if (!string.IsNullOrWhiteSpace(host.GetEnvironmentVariable(EnvVars.ApiKey)) && settings.ApiKey is null && context.Store.HasKey)
        {
            await Runner.RunnerKeyEnrollment.EnsureAsync(context, reload, host.Out, onlyRunnerKeys: true, ct);
            console.MarkupLineInterpolated($"[green]Signed in.[/] This machine is the shared runner [bold]{settings.Stored.MachineName}[/], enrolled with the API key from {EnvVars.ApiKey}.");
            return 0;
        }
        if (settings.ApiKey is not null)
        {
            if (await Runner.RunnerKeyEnrollment.EnsureAsync(context, reload, host.Out, onlyRunnerKeys: true, ct) is not null)
            {
                return 0;
            }
            return await SaveForApiKeyAsync(context, server, machineName, location, ct);
        }

        // Signing in again to the same server keeps the machine's id, so the dashboard shows one machine.
        var stored = settings.Stored;
        var changedServer = stored.Server is { } oldServer && ServerEndpoint.Parse(oldServer) != ServerEndpoint.Parse(serverText);
        var previousMachineId = changedServer ? null : stored.MachineId;
        await context.Store.WithProfileSecurityLockAsync(tx => { tx.DemandStagedEnrollment(); return Task.FromResult(true); }, ct);
        using var key = MachineKey.Create();
        MachineInventory? inventory = null;
        string? entraDeviceId = null;
        async Task<MachineEnrollRequest> EnrollBodyAsync()
        {
            if (inventory is null)
            {
                console.MarkupLine("Looking for coding agents on this machine...");
                inventory = await AgentScanner.BuildInventoryAsync(host, previousMachineId, location, ct);
                entraDeviceId = await EntraDevice.IdAsync(ct);
            }
            await context.Store.WithProfileSecurityLockAsync(tx => { tx.DemandEnrollmentTrust(); return Task.FromResult(true); }, ct);
            return new MachineEnrollRequest
            {
                WorkspaceId = string.IsNullOrWhiteSpace(choices.Workspace) ? null : choices.Workspace.Trim(),
                PublicKey = key.PublicKeyBase64,
                Name = machineName,
                Location = location,
                Inventory = inventory,
                EntraDeviceId = entraDeviceId,
                // the original issue: signed now, for this machine key, so a compliant device can skip approval.
                DeviceProof = entraDeviceId is null ? null : EntraDevice.Prove(entraDeviceId, key.PublicKeyBase64, host.Time.GetUtcNow()),
            };
        }

        MachineEnrollResponse enrolled;
        ServerBindingSnapshot? enrollmentBinding;
        var enrollToken = choices.Token ?? host.GetEnvironmentVariable(EnvVars.EnrollToken);
        if (!string.IsNullOrWhiteSpace(enrollToken))
        {
            (enrolled, enrollmentBinding) = await ServerConnection.EnrollSuppliedAsync(host, context.Store, server,
                new AuthenticationHeaderValue(AuthSchemes.EnrollmentToken, enrollToken.Trim()), EnrollBodyAsync, ct);
        }
        else
        {
            (enrolled, enrollmentBinding) = await EnrollSignedInAsync(context, info, machineName, location, choices.OpenBrowser, EnrollBodyAsync, ct);
        }

        var config = await context.Store.WithProfileSecurityLockAsync(async transaction =>
        {
            using var gate = new ServerSendGate(host, context.Store, server);
            var target = new Uri(CliSettings.BaseAddress(server), "api/machines/enroll");
            var prepared = await gate.PrepareSendAsync(target, ct, transaction);
            if (prepared.Binding != enrollmentBinding) throw new ServerBindingException("The enrollment authority changed before identity publication.");
            await prepared.ConsumeAsync(target, _ => Task.FromResult(true), ct, transaction);
            var current = transaction.Load();
            if (current with { ServerBinding = stored.ServerBinding } != stored)
                throw new ServerBindingException("The profile changed during enrollment; its identity was preserved.");
            await ServerConnection.PutEnrollmentInventoryAsync(host, context.Store, server, key,
                enrolled.Machine.MachineId, enrolled.Machine.WorkspaceId, inventory!, enrollmentBinding, transaction, ct);
            return transaction.CommitEnrollment(current, current with
            {
                Server = serverText, ServerBinding = enrollmentBinding,
                MachineId = enrolled.Machine.MachineId, WorkspaceId = enrolled.Machine.WorkspaceId,
                WorkspaceName = enrolled.WorkspaceName, MachineName = enrolled.Machine.Name,
                Location = enrolled.Machine.Location ?? location, KeyFingerprint = key.Fingerprint,
                EnrolledAt = enrolled.Machine.EnrolledAt, EnrolledWithApiKey = null,
                PendingRunnerEnrollment = null, RunnerEnrollmentBinding = null,
                PendingRunnerIdentity = null,
            }, key.ExportPrivateKeyPem(), ct);
        }, ct);

        // The key was proved before publication, so failure could not replace the previous identity.
        var signedSettings = CliSettings.Resolve(host, config, serverText) with { ApiKey = null };

        console.MarkupLineInterpolated($"[green]Signed in.[/] This machine is [bold]{config.MachineName}[/] in the [bold]{config.WorkspaceName}[/] workspace.");
        if (enrolled.Machine.PendingApproval)
        {
            console.MarkupLine("[yellow]An admin must approve this machine before it can take work.[/]");
        }
        if (IsOlder(CliHost.Version, enrolled.MinCliVersion))
        {
            console.MarkupLineInterpolated($"[yellow]This server needs mbus {enrolled.MinCliVersion} or later (this is {CliHost.Version}). Update mbus.[/]");
        }
        context.WriteAgents(inventory!.Agents);
        if (changedServer)
        {
            console.MarkupLineInterpolated($"Starting a new trust record for {KeyTrust.NormalizeServer(serverText)}.");
            if (!host.OutIsTerminal || (ReferenceEquals(host.In, Console.In) && Console.IsInputRedirected) || enrolled.Machine.PendingApproval)
                console.WriteLine("Check and trust this workspace's admin computers: mbus keys trust --admins");
            else
            {
                try
                {
                    using var http = ServerConnection.CreateAuthenticatedClient(host, signedSettings, context.Store, "cli");
                    using var keys = new MachineKeys(http, config.WorkspaceId!, config.MachineId, context.Store.Directory, host.Error, host.Time, context.Store.LoadKeyPem);
                    await KeysCommand.TrustAdminComputersAsync(keys, context, yes: false, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    console.MarkupLineInterpolated($"[yellow]Signed in, but couldn't check admin computers: {ex.Message}[/]");
                    console.WriteLine("Try again later: mbus keys trust --admins");
                }
            }
        }
        console.MarkupLine("Next: run [bold]mbus connect[/] to set up these agents to use messagebus.ai.");
        return 0;
    }

    /// <summary>
    /// Enrolls with the person's mbus sign-in: the saved one if it still works, else a new one
    /// approved in the browser. A person in several workspaces who named none is shown the choices.
    /// </summary>
    private static async Task<(MachineEnrollResponse Response, ServerBindingSnapshot? Binding)> EnrollSignedInAsync(CommandContext context, ServerInfoResponse info, string machineName,
        string? location, bool openBrowser, Func<Task<MachineEnrollRequest>> body, CancellationToken ct)
    {
        var session = new CliSession(context.Host, context.Store, context.Settings.Server!);
        if (session.Saved is { } saved)
        {
            context.Console.MarkupLineInterpolated(
                $"Using your saved sign-in ({saved.AccountName ?? saved.AccountEmail ?? "you"}). To sign in as someone else, run mbus logout first.");
        }
        else
        {
            await BrowserSignIn.SignInAsync(context, session, info, machineName, location, openBrowser, ct);
        }

        // Capture before the session's independently gated send. A legacy-to-isolated transition during
        // the send is conservatively refused by finalization instead of guessing the issuing authority.
        using var gate = new ServerSendGate(context.Host, context.Store, context.Settings.Server!);
        var prepared = await gate.PrepareSendAsync(new Uri(CliSettings.BaseAddress(context.Settings.Server!), "api/machines/enroll"), ct);
        var request = await body();
        var response = await session.SendAsync(() => ServerConnection.EnrollRequest(request), ct);
        if (response is null)
        {
            // The saved sign-in had ended (expired, or signed out from My account): sign in again.
            context.Console.MarkupLine("[yellow]Your saved sign-in has ended.[/]");
            await BrowserSignIn.SignInAsync(context, session, info, machineName, location, openBrowser, ct);
            response = await session.SendAsync(() => ServerConnection.EnrollRequest(request), ct)
                ?? throw new CliException("The server didn't accept the new sign-in. Run mbus login again.");
        }

        using (response)
        {
            if (await ServerConnection.IsWorkspaceRequiredAsync(response, ct))
            {
                throw await ChooseWorkspaceAsync(session, ct);
            }
            return (await ServerConnection.ReadEnrollAsync(response, ct), prepared.Binding);
        }
    }

    private static async Task<CliException> ChooseWorkspaceAsync(CliSession session, CancellationToken ct)
    {
        using var response = await session.SendAsync(ServerConnection.WorkspacesRequest, ct);
        var lines = response is null
            ? []
            : (await ServerConnection.ReadWorkspacesAsync(response, ct)).Select(w => $"  mbus login --workspace {w.WorkspaceId}    ({w.Name}, {w.Role})");
        return new CliException(string.Join(Environment.NewLine,
            new[] { "You belong to more than one workspace. Choose the one for this machine:" }.Concat(lines)), 2);
    }

    /// <summary>Container mode: the API key is the credential, so only the settings and the scan are needed.</summary>
    private static async Task<int> SaveForApiKeyAsync(CommandContext context, Uri server, string machineName, string? location, CancellationToken ct)
    {
        var supplied = new CliConfig { Server = CommandContext.ServerText(server), MachineName = machineName, Location = location };
        var inventory = await AgentScanner.BuildInventoryAsync(context.Host, null, location, ct);
        var settings = context.Settings with { Stored = supplied };
        using (var client = ServerConnection.CreateAuthenticatedClient(context.Host, settings, context.Store, "cli"))
        {
            await ServerConnection.PutInventoryAsync(client, server, inventory, ct);
        }
        await context.Store.WithProfileSecurityLockAsync(async transaction =>
        {
            transaction.DemandEnrollmentTrust();
            using var gate = new ServerSendGate(context.Host, context.Store, server);
            var destination = new Uri(CliSettings.BaseAddress(server), "api/machines/inventory");
            var prepared = await gate.PrepareSendAsync(destination, ct, transaction);
            await prepared.ConsumeAsync(destination, _ => Task.FromResult(true), ct, transaction);
            var current = transaction.Load();
            if (current with { ServerBinding = context.Settings.Stored.ServerBinding } != context.Settings.Stored)
                throw new ServerBindingException("The profile changed during API-key login; its identity was preserved.");
            transaction.Save(supplied with { KeyStore = current.KeyStore, ServerBinding = prepared.Binding });
            return true;
        }, ct);
        context.Console.MarkupLineInterpolated($"[green]Using the API key from {EnvVars.ApiKey}.[/] Saved the server for this machine ({machineName}).");
        context.WriteAgents(inventory.Agents);
        return 0;
    }

    private static bool IsOlder(string current, string minimum) =>
        Version.TryParse(StripSuffix(current), out var have) && Version.TryParse(StripSuffix(minimum), out var need) && have < need;

    private static string StripSuffix(string version)
    {
        var dash = version.IndexOfAny(['-', '+']);
        return dash < 0 ? version : version[..dash];
    }
}
