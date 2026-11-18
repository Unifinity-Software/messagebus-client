using System.CommandLine;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using Spectre.Console;

namespace MessageBus.Cli.Keys;

/// <summary>
/// <c>mbus keys</c>: the encryption keys as this computer sees them (the original issue, the original issue). Private keys exist only on
/// members' computers, so replacing a key and writing a recovery kit happen here, on an admin's computer,
/// not in the dashboard.
/// <list type="bullet">
/// <item><c>list</c>: each role and workspace key with its fingerprint, whether this computer trusts it and whether it must be replaced; and the admin computers whose signatures it trusts.</item>
/// <item><c>trust &lt;role|workspace|mch_…&gt;</c>: accept a key whose fingerprint changed, after checking it.</item>
/// <item><c>rotate &lt;role|workspace&gt;</c>: replace a key now and grant the new one to the computers that should hold it.</item>
/// <item><c>recovery-kit [--out file]</c>: write every key this computer holds to a passphrase-protected file.</item>
/// </list>
/// </summary>
internal static class KeysCommand
{
    public static Command Create(CliHost host, CommonOptions options)
    {
        var list = new Command("list", "Show the encryption keys, their fingerprints and whether this computer trusts them.");
        list.SetAction((parse, ct) => WithKeysAsync(host, parse, options, async (keys, context) =>
        {
            var (all, signers) = await keys.ListAsync(ct);
            if (keys.DeviceFingerprint is { } deviceFingerprint)
            {
                context.Console.WriteLine($"This computer: {context.Settings.MachineName} ({context.Settings.Stored.MachineId})");
                context.Console.WriteLine("Device key fingerprint (from this computer's local key):");
                context.Console.WriteLine("  " + Grouped(deviceFingerprint));
            }
            if (all.Count == 0)
            {
                context.Console.WriteLine("No keys yet. A role gets its key the first time an admin's computer sends it a message.");
                return 0;
            }
            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Key").AddColumn("Version").AddColumn("Fingerprint").AddColumn("This computer").AddColumn("Replace");
            foreach (var key in all)
            {
                var info = key.State.Key!;
                table.AddRow(
                    new Text(key.Name),
                    new Text($"v{info.Version}"),
                    new Text(Grouped(info.Fingerprint)),
                    new Markup(TrustText(key)),
                    new Markup(key.State.RotationNeeded ? "[yellow]needed[/]" : "[grey]no[/]"));
            }
            context.Console.Write(table);
            foreach (var key in all.Where(k => k.Trust.Status == TrustStatus.Changed))
                context.Console.MarkupLineInterpolated($"[yellow]{key.Name}:[/] {key.Trust.Problem} Check it, then run: mbus keys trust {key.Name}");
            foreach (var key in all.Where(k => k.State.RotationNeeded))
                context.Console.MarkupLineInterpolated(
                    $"[yellow]{key.Name}:[/] {string.Join(", ", key.State.MustLoseAccess.Select(Who))} still has the current key. An admin replaces it with: mbus keys rotate {key.Name}");

            if (signers.Count > 0)
            {
                context.Console.WriteLine();
                context.Console.WriteLine("Admin computers whose signatures this computer trusts:");
                var trusted = new Table().Border(TableBorder.Rounded).AddColumn("Computer").AddColumn("Fingerprint").AddColumn("Now");
                foreach (var signer in signers)
                    trusted.AddRow(new Text(signer.MachineId), new Text(Grouped(signer.Fingerprint)),
                        new Markup(signer.Trust.Status == TrustStatus.Changed ? "[red]changed[/]" : "[green]same[/]"));
                context.Console.Write(trusted);
            }
            return 0;
        }, ct));

        var trustName = new Argument<string?>("key") { Description = "A role name, workspace, or an admin computer's machine id (mch_…).", Arity = ArgumentArity.ZeroOrOne };
        var admins = new Option<bool>("--admins") { Description = "Check and trust the admin computers that signed this workspace's current keys." };
        var yes = new Option<bool>("--yes") { Description = "Don't ask for confirmation." };
        var trust = new Command("trust", "Check and trust admin computers, or accept a key after checking its fingerprint with an admin.") { trustName, admins, yes };
        trust.SetAction((parse, ct) => WithKeysAsync(host, parse, options, async (keys, context) =>
        {
            var name = parse.GetValue(trustName)?.Trim();
            if (parse.GetValue(admins))
            {
                if (name is not null) throw new CliException("Choose a key or --admins, not both.", 2);
                return await TrustAdminComputersAsync(keys, context, parse.GetValue(yes), ct);
            }
            if (string.IsNullOrWhiteSpace(name)) throw new CliException("Name a role, workspace, a computer, or use --admins.", 2);
            PublicKeyInfo? computer = null;
            if (name.StartsWith("mch_", StringComparison.Ordinal))
            {
                computer = await keys.ComputerKeyAsync(name, ct);
                context.Console.WriteLine($"The computer {name}'s device key fingerprint is:");
                context.Console.WriteLine("  " + Grouped(computer.Fingerprint));
                context.Console.WriteLine("Ask an admin to run mbus keys list on that computer and compare its local device key fingerprint.");
            }
            else
            {
                var (all, _) = await keys.ListAsync(ct);
                var shown = all.FirstOrDefault(k => k.Name == name)
                            ?? throw new CliException(name == "workspace" ? "The workspace has no key yet." : $"The role '{name}' has no key yet.");
                context.Console.MarkupLineInterpolated($"The {name} key messagebus.ai shows now is version {shown.State.Key!.Version}, fingerprint:");
                context.Console.WriteLine("  " + Grouped(shown.State.Key.Fingerprint));
                if (shown.Trust.Problem is { } problem) context.Console.MarkupLineInterpolated($"[yellow]{problem}[/]");
            }
            if (!parse.GetValue(yes) && !context.Confirm(computer is null ? "Does this match the fingerprint an admin sees on the Security page? Trust it" : "Does this match the local device key fingerprint on that computer? Trust it"))
            {
                context.Console.WriteLine("Nothing changed.");
                return 1;
            }
            var accepted = await keys.TrustAsync(name, computer?.Fingerprint, computer?.KeyId, ct);
            context.Console.MarkupLineInterpolated($"[green]Trusted {name} ({Grouped(accepted.Fingerprint)}).[/]");
            return 0;
        }, ct));

        var rotateName = new Argument<string>("key") { Description = "A role name, or workspace." };
        var rotate = new Command("rotate", "Replace a role's or the workspace's key and give the new one to the computers that should have it.") { rotateName };
        rotate.SetAction((parse, ct) => WithKeysAsync(host, parse, options, async (keys, context) =>
        {
            var name = parse.GetValue(rotateName)!.Trim();
            var (key, removed) = await keys.RotateAsync(name == "workspace" ? null : name, ct);
            context.Console.MarkupLineInterpolated($"[green]Replaced the {name} key: now version {key.Version}, fingerprint {Grouped(key.Fingerprint)}.[/]");
            if (removed.Count > 0)
                context.Console.WriteLine($"{string.Join(", ", removed.Select(Who))} can't read anything sealed to the new key.");
            return 0;
        }, ct));

        var output = new Option<string?>("--out") { Description = "Where to write the kit (default: mbus-recovery-kit-<workspace>.txt in your home folder)." };
        var kit = new Command("recovery-kit", "Write the keys this computer holds to a file protected by a passphrase.") { output };
        kit.SetAction((parse, ct) => WithKeysAsync(host, parse, options, async (keys, context) =>
        {
            await keys.StartAsync(ct);
            if (!keys.CanManageKeys) throw new CliException("Only a workspace admin's own computer can write a recovery kit.");
            var entries = await keys.RecoveryKeysAsync(ct);
            if (entries.Count == 0) throw new CliException("This computer holds no role or workspace keys, so there is nothing to put in a kit.");

            var path = Path.GetFullPath(parse.GetValue(output) ?? Path.Combine(host.HomeDirectory, $"mbus-recovery-kit-{keys.WorkspaceId}.txt"));
            if (File.Exists(path)) throw new CliException($"{path} already exists. Choose another file with --out.");
            var passphrase = Ask(host, $"Passphrase (at least {RecoveryKit.MinPassphraseLength} characters): ");
            if (passphrase.Length < RecoveryKit.MinPassphraseLength)
                throw new CliException($"Use a passphrase of at least {RecoveryKit.MinPassphraseLength} characters.");
            if (Ask(host, "The same passphrase again: ") != passphrase) throw new CliException("The passphrases don't match. Nothing was written.");

            var text = RecoveryKit.Create(keys.WorkspaceId, entries, passphrase, host.Time.GetUtcNow());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) file.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var writer = new StreamWriter(path, System.Text.Encoding.UTF8, file)) await writer.WriteAsync(text);
            await keys.RecordRecoveryKitAsync(ct);
            context.Console.MarkupLineInterpolated($"[green]Wrote {entries.Count} key(s) to {path}.[/]");
            context.Console.WriteLine("Keep the file and its passphrase in separate safe places. messagebus.ai can't recover either.");
            return 0;
        }, ct));

        return new Command("keys", "This computer's view of the encryption keys: fingerprints, trust, replacing keys and recovery kits.")
        {
            list, trust, rotate, kit,
        };
    }

    internal static async Task<int> TrustAdminComputersAsync(MachineKeys keys, CommandContext context, bool yes, CancellationToken ct)
    {
        var computers = await keys.AdminComputersAsync(ct);
        if (computers.Count == 0)
        {
            context.Console.WriteLine("No admin computers have keys to check yet. Later, run: mbus keys trust --admins");
            return 0;
        }
        context.Console.WriteLine("These admin computers signed the workspace's keys. The service's listing alone isn't proof.");
        context.Console.WriteLine("Ask an admin to run mbus keys list on each computer and read out its local device key fingerprint. Compare it with this listing:");
        foreach (var computer in computers)
            context.Console.WriteLine($"  {computer.Name ?? "Unnamed computer"} ({computer.Key.Owner}): {Grouped(computer.Key.Fingerprint)}");
        if (!yes && !context.Confirm("Do these match? Trust these admin computers"))
        {
            context.Console.WriteLine("No admin computers were trusted. Later, run: mbus keys trust --admins");
            return 1;
        }
        foreach (var computer in computers)
        {
            var accepted = await keys.TrustAsync(computer.Key.Owner!, computer.Key.Fingerprint, computer.Key.KeyId, ct);
            context.Console.MarkupLineInterpolated($"[green]Trusted {computer.Key.Owner} ({Grouped(accepted.Fingerprint)}).[/]");
        }
        return 0;
    }

    private static Task<int> WithKeysAsync(CliHost host, ParseResult parse, CommonOptions options,
        Func<MachineKeys, CommandContext, Task<int>> body, CancellationToken ct) =>
        WithKeysAsync(host, parse, options, (keys, _, context) => body(keys, context), ct);

    /// <summary>Runs <paramref name="body"/> with this enrolled computer's keys and its signed connection to the service.</summary>
    internal static async Task<int> WithKeysAsync(CliHost host, ParseResult parse, CommonOptions options,
        Func<MachineKeys, HttpClient, CommandContext, Task<int>> body, CancellationToken ct)
    {
        var context = CommandContext.Load(host, parse, options);
        var settings = context.Settings;
        if (settings.Server is null || settings.ApiKey is not null || settings.Stored is not { WorkspaceId: { } workspace, MachineId: { } machine }
            || !settings.CanAuthenticate(context.Store))
            throw new CliException("Keys live on enrolled computers. Run: mbus login --server <url>");

        using var http = ServerConnection.CreateAuthenticatedClient(host, settings, context.Store, "cli");
        using var keys = new MachineKeys(http, workspace, machine, context.Store.Directory, host.Error, host.Time, context.Store.LoadKeyPem);
        try
        {
            return await body(keys, http, context);
        }
        catch (McpToolException e)
        {
            throw new CliException(e.Error.Message);
        }
        catch (HttpRequestException e)
        {
            throw new CliException("Couldn't reach messagebus.ai: " + e.Message);
        }
    }

    private static string Ask(CliHost host, string prompt)
    {
        host.Out.Write(prompt);
        host.Out.Flush();
        return host.In.ReadLine() ?? "";
    }

    private static string TrustText(KeyListing key) => key.Trust.Status switch
    {
        TrustStatus.Changed => "[red]changed: not used[/]",
        TrustStatus.New => "[grey]not used yet[/]",
        TrustStatus.Rotated => "[green]replaced by a trusted admin[/]",
        _ => key.HeldHere ? "[green]trusted, holds it[/]" : "[green]trusted[/]",
    };

    private static string Who(KeyAccessLossInfo machine) =>
        machine.Reason == KeyAccessLoss.MachineRemoved ? $"{machine.MachineId} (removed)" : $"{machine.Name ?? machine.MachineId} (left the role)";

    /// <summary>The full fingerprint in groups of four, the way people read it out.</summary>
    internal static string Grouped(string fingerprint) =>
        string.Join(' ', Enumerable.Range(0, fingerprint.Length / 4).Select(i => fingerprint.Substring(i * 4, 4)));
}
