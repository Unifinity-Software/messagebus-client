using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Runner.Service;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Update;

/// <summary>The interactive tool update, sharing the runner's private applier and offline recovery.</summary>
internal static class DotnetToolUpdateCommand
{
    internal const string RunnerMessage = "Use Update now on the Runners page for this runner, or stop it first with mbus runner stop";

    internal static async Task<int> RunAsync(CliHost host, DotnetToolInstall tool, string? requested, string? channel,
        bool check, bool yes, CancellationToken ct, Func<string, string, string, int>? handoff = null,
        string? currentVersion = null, string? entryAssembly = null)
    {
        var assemblyVersion = currentVersion ?? CliHost.Version;
        channel = string.IsNullOrWhiteSpace(channel) ? host.GetEnvironmentVariable(UpdateEnvVars.Channel) ?? "stable" : channel;
        if (channel is not ("stable" or "preview")) throw new CliException($"'{channel}' isn't a release channel name.", 2);
        var source = host.GetEnvironmentVariable("MBUS_UPDATE_NUGET_SOURCE");
        try
        {
            var current = tool.ToolsPath is null ? assemblyVersion : tool.Package?.Version
                ?? throw new IOException(DotnetToolPackage.IdentityUnknown);
            var version = string.IsNullOrEmpty(requested) ? await DotnetToolUpdater.LatestAsync(source, channel, ct) : requested;
            var comparison = DotnetToolPackage.Compare(version, current);
            var command = tool.UpdateCommand(version);
            await host.Out.WriteLineAsync(comparison switch
            {
                0 => $"mbus {current} is up to date.",
                < 0 => $"This mbus ({current}) is newer than the release feed's {version}.",
                _ => $"mbus {version} is available (this is {current}).",
            });
            if (check || comparison == 0 || (string.IsNullOrEmpty(requested) && comparison < 0)) return 0;
            if (tool.ToolsPath is null) throw new CliException(tool.RefusalMessage(version));
            var manager = host.RunnerServices();
            async Task CheckAsync()
            {
                // A registered service may use another settings folder. Read its definition rather than assuming ours.
                var plan = manager.Platform == ServicePlatform.Other ? null : RunnerServiceDefinitions.Plan(manager,
                    [tool.Shim ?? "mbus"], [], host.HomeDirectory, host.ConfigDirectory, new Dictionary<string, string>());
                var registration = await RunnerServiceCommand.RegisteredAsync(host, plan, host.ConfigDirectory, ct);
                var runnerUsesInstall = registration.Command?.TakeWhile(t => t != "runner").Any(p => UsesInstall(tool, p)) == true;
                var holders = ToolInstallHolders.FinalCheck(tool, manager.Processes, Environment.ProcessId,
                    new HashSet<int> { Environment.ProcessId });
                if (holders.Count > 0)
                    throw new CliException($"Update stopped: {holders.Count} processes are using this mbus install:\n"
                        + string.Join('\n', holders.Select(h => $"  {h.Pid} {h.Name}")) + "\nClose these sessions and try again."
                        + (runnerUsesInstall ? "\n" + RunnerMessage : ""));
                if (runnerUsesInstall && (RunnerInstance.IsHeld(host.ConfigDirectory)
                    || registration.Folder is { } folder && RunnerInstance.IsHeld(folder))) throw new CliException(RunnerMessage);
            }
            await CheckAsync();
            if (!yes)
            {
                if (!host.OutIsTerminal)
                {
                    await host.Out.WriteLineAsync("Run: " + command);
                    return 0;
                }
                await host.Out.WriteAsync($"Update now with {command}? [y/N] ");
                var answer = await host.In.ReadLineAsync(ct);
                if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
                {
                    await host.Out.WriteLineAsync("Update cancelled.");
                    return 0;
                }
            }
            await CheckAsync();
            var dotnet = ExecutableLocator.Find(host, "dotnet")?.Path ?? throw new CliException("The dotnet SDK could not be found on this machine.");
            // Keep manual state separate from pending service updates and other manual attempts.
            var directory = Path.Combine(host.ConfigDirectory, "manual-updates", Guid.NewGuid().ToString("N"));
            var updater = new DotnetToolUpdater(tool, directory, dotnet, host.Error, RunnerUpdateRecord.In(directory),
                assemblyVersion, host.Time, () => manager.Processes, source, entryAssembly, manual: true);
            if (!await updater.InstallAsync(new UpdateRequest { Version = version, Channel = channel, Pinned = !string.IsNullOrEmpty(requested) }, ct))
                throw new CliException(updater.WaitingAgain ? "Update stopped: processes are using this mbus install. Close these sessions and try again."
                    : updater.LastResult?.Reason ?? "The update could not be prepared.");
            await CheckAsync();
            var root = Path.Combine(directory, "update");
            var assembly = Path.Combine(root, "applier", Path.GetFileName(entryAssembly ?? System.Reflection.Assembly.GetEntryAssembly()!.Location));
            await host.Out.FlushAsync(ct);
            await host.Error.FlushAsync(ct);
            return (handoff ?? Handoff)(dotnet, assembly, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or System.Text.Json.JsonException
            or KeyNotFoundException or InvalidOperationException or Win32Exception)
        {
            throw new CliException("Couldn't update the dotnet tool: " + DotnetToolUpdater.Diagnostic(ex.Message));
        }
    }

    internal static bool UsesInstall(DotnetToolInstall tool, string path)
    {
        if (tool.ToolsPath is null || !Path.IsPathFullyQualified(path)) return false;
        return DotnetToolInstall.SamePath(path, tool.Shim ?? Path.Combine(tool.ToolsPath, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus"))
            || RunnerProcesses.IsInside(path, Path.Combine(tool.ToolsPath, ".store", "messagebus.ai.cli"));
    }

    internal static int Handoff(string dotnet, string assembly, string root)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo(dotnet) { UseShellExecute = false };
            foreach (var arg in ApplierArguments(true, Environment.ProcessId, assembly, root)) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new IOException("Couldn't start the update applier.");
            return 0; // The child shares this console and waits for this process to release .store.
        }
        // Replacing our process releases every loaded .store assembly before dotnet updates the tool.
        var args = new[] { dotnet }.Concat(ApplierArguments(false, Environment.ProcessId, assembly, root)).Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var argv = Marshal.AllocHGlobal((args.Length + 1) * IntPtr.Size);
        try
        {
            for (var i = 0; i < args.Length; i++) Marshal.WriteIntPtr(argv, i * IntPtr.Size, args[i]);
            Marshal.WriteIntPtr(argv, args.Length * IntPtr.Size, IntPtr.Zero);
            Exec(dotnet, argv);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't start the update applier.");
        }
        finally
        {
            foreach (var arg in args) Marshal.FreeCoTaskMem(arg);
            Marshal.FreeHGlobal(argv);
        }
    }

    internal static IReadOnlyList<string> ApplierArguments(bool windows, int pid, string assembly, string root) =>
        [assembly, DotnetToolUpdater.PrivateCommand, windows ? "cli-wait-" + pid : "cli", root];

    [DllImport("libc", EntryPoint = "execv", SetLastError = true)]
    private static extern int Exec([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr argv);
}
