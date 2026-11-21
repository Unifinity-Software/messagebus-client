using System.CommandLine;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Server;

namespace MessageBus.Cli.Runner.Service;

/// <summary>
/// <c>mbus runner install</c> and <c>mbus runner uninstall</c> (the original issue): register the runner with this machine's service
/// manager for the signed-in user (<see cref="RunnerServiceDefinitions"/>), or remove it. <c>mbus runner stop</c> (the original issue)
/// stops it and leaves it registered. All three stop the runner the same way (<see cref="StopRunnerAsync"/>) and say it
/// stopped only when it has.
/// </summary>
internal static class RunnerServiceCommand
{
    /// <summary>How long a runner gets to stop on its own (hand its tasks back) before its process is ended.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(15);

    public static void AddTo(Command runner, CliHost host, CommonOptions options)
    {
        var keepAwake = RunnerCliCommand.KeepAwakeOption();
        var codexSandbox = RunnerCliCommand.CodexSandboxOption();
        var agentTools = RunnerCliCommand.AgentToolsOption();
        var slots = RunnerCliCommand.RemovedSlotsOption(); // the original issue: ignored, never passed on to the runner
        var workDir = new Option<string?>("--work-dir") { Description = "Folder for each task's working copy (default ~/.mbus/runs)." };
        var print = new Option<bool>("--print") { Description = "Show what would be registered and the commands it runs, and change nothing." };
        var force = new Option<bool>("--force") { Description = "Install even when the mbus set up before is a newer version." };
        var keyStore = new Option<string?>("--key-store") { Description = "On macOS: file or keychain. Persist where this machine keeps its key." };
        var installService = WindowsServiceOption();
        var account = new Option<string?>("--account") { Description = @"With --service, use a provisioned DOMAIN\gmsa$ account." };
        var install = new Command("install", "Start the runner at sign-in, or configure a dedicated Windows service.") { slots, workDir, print, force, keepAwake, codexSandbox, agentTools, keyStore, installService, account };
        install.SetAction(async (parse, ct) =>
        {
            if (Security.RunContext.IsInsideRun(host.GetEnvironmentVariable)) throw new CliException(Security.RunContext.KeyRefused);
            if (parse.GetValue(installService))
            {
                if (!OperatingSystem.IsWindows()) throw WindowsOnly();
                if (parse.GetValue(force) || new[] { workDir, keepAwake, codexSandbox, agentTools, keyStore, slots }.Any(option => parse.GetValue(option) is not null))
                    throw new CliException("The dedicated Windows service uses its own work directory, key store, and runner settings.");
                return await WindowsServiceCommands.InstallAsync(host, parse.GetValue(account), parse.GetValue(options.Server), parse.GetValue(print), ct);
            }
            if (parse.GetValue(account) is not null) throw new CliException("--account requires --service.");
            var context = CommandContext.Load(host, parse, options);
            var choice = parse.GetValue(keyStore)?.Trim().ToLowerInvariant();
            if (choice is not null && (choice is not ("file" or "keychain") || !OperatingSystem.IsMacOS()))
                throw new CliException("--key-store accepts file or keychain on macOS.");
            if (context.Settings.Server is null || !context.Settings.CanAuthenticate(context.Store))
            {
                throw ServerConnection.NotSignedIn();
            }
            if (parse.GetResult(slots) is not null)
            {
                await host.Error.WriteLineAsync(RunnerCliCommand.SlotsIgnored);
            }
            List<string> runnerArgs = [];
            if (parse.GetValue(workDir) is { } dir) runnerArgs.AddRange(["--work-dir", Path.GetFullPath(dir, host.CurrentDirectory)]);
            var awakeSetting = parse.GetValue(keepAwake) ?? RunnerEnvironment.NonEmpty(host.GetEnvironmentVariable(RunnerEnvVars.KeepAwake));
            try
            {
                var awake = RunnerOptions.ParseKeepAwake(awakeSetting);
                if (awakeSetting is not null) runnerArgs.AddRange(["--keep-awake", awake.ToString().ToLowerInvariant()]);
                var codex = RunnerOptions.ParseCodexSandbox(parse.GetValue(codexSandbox) ?? host.GetEnvironmentVariable(Agents.CodexAdapter.SandboxOverrideVariable));
                if (codex is not null) runnerArgs.AddRange(["--codex-sandbox", codex]);
                var toolsSetting = parse.GetValue(agentTools) ?? host.GetEnvironmentVariable(RunnerOptions.AgentToolsVariable);
                var tools = RunnerOptions.ParseAgentTools(toolsSetting);
                if (toolsSetting is not null) runnerArgs.AddRange(["--agent-tools", tools]);
            }
            catch (FormatException ex)
            {
                throw new CliException(ex.Message);
            }
            var plan = PlanFor(host, context, runnerArgs, choice);
            if (plan.UpdateUnavailableReason is { } reason)
                await host.Out.WriteLineAsync("Dotnet-tool updates are unavailable on this machine: " + reason);

            // the original issue: the agents' sign-ins from this shell, which the service wouldn't otherwise get.
            var signIns = AgentEnvironmentStore.FromShell(host.GetEnvironmentVariable);
            var stored = context.Store.AgentEnvironment;
            if (parse.GetValue(print))
            {
                await WritePlanAsync(host, plan, plan.Install);
                if (signIns.Count > 0)
                {
                    await host.Out.WriteLineAsync();
                    await host.Out.WriteLineAsync($"Stored for its agents, in {stored.Path}: {string.Join(", ", signIns.Keys)}");
                }
                return 0;
            }
            if (!parse.GetValue(force) && await NewerInstalledAsync(host, plan, MbusStart(host), ct) is { } newer)
            {
                throw new CliException(newer);
            }
            // the original issue: whatever runs now is replaced, never joined by a second runner. The service manager's own stop ends
            // it (and the Windows loop) while the stop request is in place, so one exiting for an update meanwhile isn't
            // started again. One that won't stop leaves the request in place (a restart loop mbus couldn't end then keeps
            // it stopped) and nothing is registered.
            var directory = context.Store.Directory;
            // The task (or launchd agent) has one name per user: one set up with other settings is never replaced from here.
            var registration = await RegisteredAsync(host, plan, directory, ct);
            if (registration.Kind is not (Registered.None or Registered.Here))
            {
                throw new CliException(NotOurs(registration) + (registration.Kind == Registered.Elsewhere
                    ? $" To set it up with these settings instead, first run mbus runner uninstall with {EnvVars.Home} set to {registration.Folder}."
                    : $" Remove the {plan.Name} {(plan.Platform == ServicePlatform.Windows ? "task" : "launchd agent")} yourself, then run mbus runner install again."));
            }
            ConfigStore.KeyStoreChange? keyStoreChange = null;
            string? fileKeyStoreReason = null;
            if (OperatingSystem.IsMacOS())
            {
                var savedKeyStore = context.Store.Load().KeyStore;
                fileKeyStoreReason = choice is not null ? "--key-store file was requested"
                    : savedKeyStore is not null ? "it is the saved choice for this machine"
                    : string.Equals(host.GetEnvironmentVariable(ConfigStore.KeyStoreVariable)?.Trim(), "file", StringComparison.OrdinalIgnoreCase)
                        ? "MBUS_KEY_STORE=file is set in this session"
                        : "the system key store is unavailable";
                if (choice is null && savedKeyStore is null && host.OutIsTerminal && (!ReferenceEquals(host.In, Console.In) || !Console.IsInputRedirected))
                {
                    await host.Out.WriteLineAsync("A file works while the Keychain is locked; programs running as you can read it without Keychain protection.");
                    choice = context.Confirm("Is this an unattended runner Mac (nobody signs in to unlock the keychain)? Store its key in a file only your account can read:") ? "file" : "keychain";
                    fileKeyStoreReason = "you chose file storage for an unattended Mac";
                }
                choice ??= savedKeyStore ?? context.Store.KeyStoreName;
                keyStoreChange = context.Store.PrepareKeyStoreChange(choice);
                plan = PlanFor(host, context, runnerArgs, choice);
            }
            StopResult stopped;
            try
            {
                stopped = await StopRunnerAsync(host, directory, registration.Kind == Registered.Here ? plan.Stop : [], Installs(host, plan), "install", ct);
            }
            catch
            {
                keyStoreChange?.Abandon();
                throw;
            }
            if (stopped.Problem is { } problem)
            {
                // the original issue: the runner that is still running keeps reading its key where it was.
                keyStoreChange?.Abandon();
                throw new CliException(problem);
            }
            // Only now, with no runner reading the old copy, does the key move for good.
            if (keyStoreChange?.Commit() is { } keyWarning)
            {
                await host.Error.WriteLineAsync(keyWarning);
            }
            if (choice == "file" && fileKeyStoreReason is not null)
            {
                await host.Out.WriteLineAsync($"The runner keeps its key in a file only your account can read, not the Keychain, because {fileKeyStoreReason}.");
            }
            if (stopped.Ended && plan.Platform == ServicePlatform.Windows)
            {
                // A restart loop that outlived its runner starts it again after its delay: it must still see the request.
                await Task.Delay(TimeSpan.FromSeconds(RunnerServiceDefinitions.WindowsRestartDelaySeconds + 3), host.Time, ct);
                if (RunnerInstance.IsHeld(directory))
                {
                    throw StillRunning(RunnerInstance.ReadHolder(directory)?.Pid ?? -1, "install");
                }
            }
            RunnerInstance.ClearStop(directory);
            if (signIns.Count > 0)
            {
                stored.Save(signIns);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(plan.DefinitionPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.LogPath)!);
            await File.WriteAllTextAsync(plan.DefinitionPath, plan.Definition, plan.DefinitionEncoding, ct);
            await RunAsync(host, plan.Install, ct);
            // the original issue: with another mbus first on PATH, the commands below name this one by its full path.
            var mbusOnPath = Hosting.MbusOnPath.For(host);
            var started = plan.Platform != ServicePlatform.MacOs || await RunnerStartup.WaitAsync(directory, host.Time, ct);
            if (started)
            {
                await host.Out.WriteLineAsync(mbusOnPath.Advice(
                    $"{(plan.Platform == ServicePlatform.MacOs ? "The runner has started and" : "The runner now")} starts when you sign in and again after each update{(plan.Platform == ServicePlatform.Windows ? " (within a minute)" : "")}. " +
                    $"Its output goes to {plan.LogPath}. To stop it: mbus runner stop. To remove it: mbus runner uninstall"));
            }
            await WriteSignInsAsync(host, signIns.Count > 0 ? signIns.Keys : stored.Load().Keys, kept: signIns.Count == 0, stored.Path, plan.Platform, mbusOnPath);
            if (mbusOnPath.Warning() is { } otherMbus)
            {
                await host.Error.WriteLineAsync(otherMbus);
            }
            if (string.Equals(Path.GetFileNameWithoutExtension(host.MbusCommand.Command), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                await host.Error.WriteLineAsync("This mbus runs from a build folder, so it can't update itself. Install a released mbus and run mbus runner install again.");
            }
            if (!started)
            {
                await host.Error.WriteLineAsync($"The launch agent is installed, but the runner hasn't started within {RunnerStartup.StartWait.TotalSeconds:0} seconds. "
                    + $"To start it: launchctl kickstart gui/{host.RunnerServices().UserId}/{plan.Name}. "
                    + $"Check {plan.LogPath} and {Path.Combine(Path.GetDirectoryName(plan.LogPath)!, "runner-errors.log")} for the reason.");
                return 1;
            }
            return 0;
        });
        runner.Subcommands.Add(install);

        var uninstallPrint = new Option<bool>("--print") { Description = "Show the commands it would run, and change nothing." };
        var uninstallService = WindowsServiceOption();
        var uninstall = new Command("uninstall", "Remove the runner's automatic start and stop it now. Running tasks go back to the queue.") { uninstallPrint, uninstallService };
        uninstall.SetAction(async (parse, ct) =>
        {
            if (parse.GetValue(uninstallService))
            {
                if (!OperatingSystem.IsWindows()) throw WindowsOnly();
                return await WindowsServiceCommands.UninstallAsync(host, parse.GetValue(uninstallPrint), ct);
            }
            var context = CommandContext.Load(host, parse, options);
            var plan = PlanFor(host, context, []);
            if (parse.GetValue(uninstallPrint))
            {
                await WritePlanAsync(host, plan, plan.Uninstall);
                return 0;
            }
            var directory = context.Store.Directory;
            // the original issue: only what this settings folder registered is removed. Not registered, there is nothing to remove, and
            // the wait for the runner still has to run.
            var registration = await RegisteredAsync(host, plan, directory, ct);
            var ours = registration.Kind == Registered.Here;
            var foreign = registration.Kind is Registered.Elsewhere or Registered.Unknown;
            var installed = File.Exists(plan.DefinitionPath) && (!foreign || RunnerProcesses.IsInside(plan.DefinitionPath, directory));
            var stopped = await StopRunnerAsync(host, directory, ours ? plan.Uninstall : [], Installs(host, plan), "uninstall", ct);
            if (installed)
            {
                File.Delete(plan.DefinitionPath);
            }
            if (context.Store.AgentEnvironment.Delete())
            {
                await host.Out.WriteLineAsync("The agent sign-ins stored for it are deleted.");
            }
            var note = foreign ? " " + NotOurs(registration) : "";
            if (stopped.Problem is { } problem)
            {
                throw new CliException(problem + note);
            }
            await host.Out.WriteLineAsync(foreign ? (stopped.WasRunning ? "The runner is stopped." : "No runner is running here.") + note
                : ours ? "The runner is stopped and no longer starts when you sign in."
                : stopped.WasRunning ? "The runner is stopped. It wasn't set up to start when you sign in."
                : "The runner isn't installed on this computer, and no runner is running here.");
            return 0;
        });
        runner.Subcommands.Add(uninstall);

        var stop = new Command("stop", "Stop the runner now and keep it stopped, also after you sign in again. Running tasks go back to the queue.");
        stop.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.Load(host, parse, options);
            var manager = host.RunnerServices();
            // Linux has no registration of ours (a systemd unit is your own): the stop request and the lock do it all.
            var plan = manager.Platform == ServicePlatform.Other ? null : PlanFor(host, context, []);
            // Only a task this settings folder registered is stopped and disabled (the original issue): one that isn't there needs
            // nothing, and one set up with other settings (MBUS_HOME) runs another runner, left alone.
            var registration = await RegisteredAsync(host, plan, context.Store.Directory, ct);
            var ours = registration.Kind == Registered.Here;
            var disable = ours ? plan!.Disable : null;
            var stopped = await StopRunnerAsync(host, context.Store.Directory, ours ? plan!.Stop : [], Installs(host, plan), "stop", ct);
            var note = registration.Kind is Registered.Elsewhere or Registered.Unknown ? " " + NotOurs(registration) : null;
            string? notDisabled = null;
            if (disable is not null && await manager.RunAsync(disable, ct) is { ExitCode: not 0 } failed)
            {
                notDisabled = $"Windows still starts the runner at sign-in: turning off the {RunnerServiceDefinitions.TaskName} task failed (exit {failed.ExitCode}"
                    + $"{(failed.Output.Length > 0 ? ": " + failed.Output : "")}). Turn it off in Task Scheduler, or run mbus runner uninstall.";
            }
            if (stopped.Problem is { } problem)
            {
                throw new CliException((notDisabled is null ? problem : problem + " " + notDisabled) + note);
            }
            if (notDisabled is not null)
            {
                throw new CliException((stopped.WasRunning ? "The runner is stopped, but " : "No runner is running here, but ") + char.ToLowerInvariant(notDisabled[0]) + notDisabled[1..]);
            }
            await host.Out.WriteLineAsync((stopped.WasRunning ? "The runner is stopped." : "No runner is running here.") + (note
                ?? (plan is null || !ours
                ? $" It stays stopped until you start mbus runner once in a terminal, or delete {Path.Combine(context.Store.Directory, RunnerInstance.StopFile)}."
                : " It stays stopped, also after you sign in again. To have it start at sign-in again: mbus runner install")));
            return 0;
        });
        runner.Subcommands.Add(stop);

        // the original issue: see, replace or delete the stored agent sign-ins without installing again (and on Linux, where a systemd
        // unit you write yourself starts the runner).
        var save = new Option<bool>("--save") { Description = "Store the agent sign-in variables set in this shell, replacing the stored ones." };
        var clear = new Option<bool>("--clear") { Description = "Delete the stored agent sign-ins." };
        var envService = WindowsServiceOption();
        var env = new Command("env", "Show which agent sign-ins (such as CLAUDE_CODE_OAUTH_TOKEN) are stored for the runner's agents: names only, never values.") { save, clear, envService };
        env.SetAction(async (parse, ct) =>
        {
            if (parse.GetValue(save) && parse.GetValue(clear))
            {
                throw new CliException("Use --save or --clear, not both.");
            }
            if (parse.GetValue(envService))
            {
                if (!OperatingSystem.IsWindows()) throw WindowsOnly();
                return await WindowsServiceCommands.EnvironmentAsync(host, parse.GetValue(save), parse.GetValue(clear), ct);
            }
            var context = CommandContext.Load(host, parse, options);
            var stored = context.Store.AgentEnvironment;
            if (parse.GetValue(clear))
            {
                await host.Out.WriteLineAsync(stored.Delete()
                    ? "The stored agent sign-ins are deleted. From the next task, agents get only the runner's own environment."
                    : "No agent sign-ins are stored.");
                return 0;
            }
            if (parse.GetValue(save))
            {
                var signIns = AgentEnvironmentStore.FromShell(host.GetEnvironmentVariable);
                if (signIns.Count == 0)
                {
                    throw new CliException($"None of {string.Join(", ", AgentEnvironmentStore.Names)} is set in this shell, so nothing was stored.");
                }
                stored.Save(signIns);
                await host.Out.WriteLineAsync($"Stored for the runner's agents: {string.Join(", ", signIns.Keys)} (in {stored.Path}, readable only by you). The runner uses them from its next task.");
                return 0;
            }
            var names = stored.Load().Keys.ToList();
            await host.Out.WriteLineAsync(names.Count > 0
                ? $"Stored for the runner's agents: {string.Join(", ", names)} (in {stored.Path}). To replace them: mbus runner env --save from a shell where they are set. To delete them: mbus runner env --clear"
                : $"No agent sign-ins are stored. To store one, set it in this shell ({MessageBus.Cli.Agents.SignInChecks.ClaudeTokenVariable} from claude setup-token, or an agent's API key), then run mbus runner install or mbus runner env --save.");
            return 0;
        });
        runner.Subcommands.Add(env);
    }

    internal static Option<bool> WindowsServiceOption() => new("--service")
    {
        Description = "Use the dedicated Windows runner service.",
    };

    internal static CliException WindowsOnly() => new("--service is available only on Windows.");

    /// <summary>Says which agent sign-ins the service has (names only, never values) and how to change them.</summary>
    private static async Task WriteSignInsAsync(CliHost host, IEnumerable<string> names, bool kept, string path, ServicePlatform platform, Hosting.MbusOnPath mbus)
    {
        var list = names.ToList();
        if (list.Count > 0)
        {
            await host.Out.WriteLineAsync(mbus.Advice($"{(kept ? "It keeps the agent sign-ins stored before" : "Stored for its agents")}: {string.Join(", ", list)} (in {path}, readable only by you). "
                + "To change them, run mbus runner install again from a shell where they are set. To delete them: mbus runner env --clear"));
        }
        else if (platform == ServicePlatform.MacOs)
        {
            // The service can't open the login keychain, where Claude Code keeps its sign-in.
            await host.Out.WriteLineAsync(mbus.Advice($"No agent sign-in variable is set in this shell. If Claude Code can't sign in under the runner, run claude setup-token, set {MessageBus.Cli.Agents.SignInChecks.ClaudeTokenVariable} to the token, and run mbus runner install again."));
        }
    }

    /// <summary>How long a runner that was ended gets to let go of its lock.</summary>
    public static readonly TimeSpan EndWait = TimeSpan.FromSeconds(3);

    /// <summary>How <see cref="StopRunnerAsync"/> ended.</summary>
    /// <param name="WasRunning">A runner held the lock when it began.</param>
    /// <param name="Ended">mbus ended a runner's processes (it didn't stop when asked).</param>
    /// <param name="Problem">Why a runner may still run, for the person; null when none does.</param>
    internal sealed record StopResult(bool WasRunning, bool Ended, string? Problem);

    /// <summary>
    /// Stops the runner in <paramref name="directory"/> (the original issue): writes the stop request, runs <paramref name="steps"/>
    /// (the service manager's own stop or removal), and waits up to <see cref="StopWait"/> for the runner to let go of
    /// its lock. One that hasn't is ended with everything it started (on Windows its restart loop first), but only while
    /// each process is still the one it wrote down (id, file and start time), and never the caller or one of its
    /// parents. Then no runner from this mbus may run any more: one from before the lock holds none and ignores the
    /// request, so a free lock alone doesn't prove it stopped.
    /// </summary>
    /// <param name="host">The CLI host.</param>
    /// <param name="directory">mbus's settings folder.</param>
    /// <param name="steps">The service manager's stop or removal.</param>
    /// <param name="installs">The <c>mbus</c> files a runner here would start from.</param>
    /// <param name="command">The <c>mbus runner</c> subcommand, for what to run again.</param>
    /// <param name="ct">Stops waiting.</param>
    /// <exception cref="CliException">This command runs inside a task the runner started.</exception>
    internal static async Task<StopResult> StopRunnerAsync(CliHost host, string directory, IReadOnlyList<ServiceStep> steps, IReadOnlyCollection<string> installs,
        string command, CancellationToken ct)
    {
        var manager = host.RunnerServices();
        var table = manager.Processes;
        var callers = RunnerProcesses.Protected(Environment.ProcessId, table);
        var running = RunnerInstance.IsHeld(directory);
        var holder = running ? RunnerInstance.ReadHolder(directory) : null;
        if (holder is not null && (callers.Contains(holder.Runner.Pid) || holder.Loop is { } l && callers.Contains(l.Pid))
            || RunnerProcesses.Ancestry(Environment.ProcessId, table).Skip(1).Any(p => RunnerProcesses.IsRunner(p, installs)))
        {
            // Stopping it would end this command (and the task it runs) with it.
            throw new CliException("Run this from a terminal on this computer, not from a task the runner started.");
        }
        // The runner's own processes (on Windows the mbus.exe on PATH waiting for it) exit with it: not other runners.
        var own = holder is null ? new HashSet<int>() : RunnerProcesses.Ancestry(holder.Runner.Pid, table).TakeWhile(p => RunnerProcesses.IsMbusFile(p.ExecutablePath, installs))
            .Select(p => p.Pid).Append(holder.Runner.Pid).ToHashSet();

        RunnerInstance.RequestStop(directory);
        await RunAsync(host, steps, ct);
        var ended = false;
        if (!await RunnerInstance.WaitForReleaseAsync(directory, host.Time, StopWait, ct))
        {
            holder = RunnerInstance.ReadHolder(directory) ?? holder;
            if (holder is null)
            {
                return new StopResult(true, false, StillRunning(-1, command).Message);
            }
            await host.Error.WriteLineAsync($"The runner (process {holder.Pid}) didn't stop within {StopWait.TotalSeconds:0} seconds, so mbus is ending it.");
            foreach (var (target, isLoop) in new[] { (holder.Loop, true), (holder.Runner, false) })
            {
                if (target is null)
                {
                    continue;
                }
                var match = RunnerProcesses.Check(target, table, callers);
                string? why = null;
                EndOutcome outcome;
                try
                {
                    outcome = match switch
                    {
                        RunnerProcesses.Match.Gone => EndOutcome.Gone,
                        // Read again through the process that is then ended: still the one written down, and still a
                        // runner (or the restart loop).
                        RunnerProcesses.Match.Same => manager.EndProcessTree(target, isLoop
                            ? now => RunnerProcesses.IsRestartLoop(now, table.Get(now.ParentPid), installs)
                            : now => RunnerProcesses.IsRunner(now, installs)),
                        _ => EndOutcome.Refused,
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Part of the tree couldn't be ended (Kill's AggregateException), or reading it failed: the lock
                    // and the other runners are checked below all the same, and a stop still turns the task off.
                    outcome = EndOutcome.NotEnded;
                    why = ex is AggregateException all
                        ? string.Join("; ", all.Flatten().InnerExceptions.Select(e => e.Message).Distinct())
                        : ex.Message;
                }
                switch (outcome)
                {
                    case EndOutcome.Ended:
                        ended = true;
                        break;
                    case EndOutcome.NotEnded:
                        ended = true;
                        await host.Error.WriteLineAsync(why is null
                            ? $"mbus couldn't end process {target.Pid}."
                            : $"mbus couldn't end process {target.Pid} or some of the processes it started: {why}");
                        break;
                    case EndOutcome.Refused when isLoop && match != RunnerProcesses.Match.Ancestor:
                        // The loop exited and its id went to another process: nothing to end there; the runner still is.
                        break;
                    case EndOutcome.Refused:
                        return new StopResult(true, ended, $"mbus didn't end process {target.Pid}: it isn't the runner process mbus wrote down any more. "
                            + $"End the runner yourself, then run mbus runner {command} again.");
                }
            }
            if (!await RunnerInstance.WaitForReleaseAsync(directory, host.Time, EndWait, ct))
            {
                return new StopResult(true, ended, StillRunning(holder.Pid, command).Message);
            }
        }
        var others = RunnerProcesses.OtherRunners(table, installs, callers.Union(own).ToHashSet(), directory);
        foreach (var untraced in others.Untraced)
        {
            await host.Error.WriteLineAsync($"Another mbus runner is running (process {untraced.Pid}). mbus can't tell whether it uses these settings, so it left it alone.");
        }
        if (others.Here is [var other, ..])
        {
            return new StopResult(true, ended, $"An older runner may still be running: process {other.Pid}. End it, then run mbus runner {command} again.");
        }
        return new StopResult(running, ended, null);
    }

    /// <summary>The <c>mbus</c> files a runner here starts from: this one, and the one the registered service starts.</summary>
    private static IReadOnlyCollection<string> Installs(CliHost host, RunnerServicePlan? plan)
    {
        var installs = new List<string>();
        void Add(string? path)
        {
            if (path is { Length: > 0 } && !string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                installs.Add(Path.GetFullPath(path, host.CurrentDirectory));
            }
        }
        Add(MbusStart(host)[0]);
        if (plan is not null && File.Exists(plan.DefinitionPath))
        {
            Add(RegisteredMbus(plan)?[0]);
        }
        return installs;
    }

    private static CliException StillRunning(int pid, string command) => new(pid > 0
        ? $"The runner is still running (process {pid}) and mbus couldn't end it. End process {pid} yourself, then run mbus runner {command} again."
        : $"A runner is still running here and mbus couldn't tell which process it is. End the mbus runner process yourself, then run mbus runner {command} again.");

    /// <summary>
    /// Why install refuses: the runner registered now starts a newer mbus than this one (an old copy elsewhere on PATH,
    /// say), which installing would replace with an older version. Null when it doesn't, or that can't be told.
    /// </summary>
    private static async Task<string?> NewerInstalledAsync(CliHost host, RunnerServicePlan plan, IReadOnlyList<string> mbus, CancellationToken ct)
    {
        if (!File.Exists(plan.DefinitionPath) || RegisteredMbus(plan) is not { Count: > 0 } registered || !File.Exists(registered[0])
            || string.Equals(Path.GetFullPath(registered[0]), Path.GetFullPath(mbus[0]), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return null;
        }
        var (exit, output) = await host.RunnerServices().RunAsync(new ServiceStep(registered[0], [.. registered.Skip(1), "--version"], MayFail: true), ct);
        var version = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (exit != 0 || Contracts.Releases.ReleaseVersion.Compare(CliHost.Version, version) is not < 0)
        {
            return null;
        }
        return $"The runner set up here starts a newer mbus ({version}, {string.Join(' ', registered)}) than this one ({CliHost.Version}, {string.Join(' ', mbus)}). "
            + "Run mbus runner install with the newer one, or add --force to set up this one anyway.";
    }

    /// <summary>Whose the runner registered with the service manager is.</summary>
    internal enum Registered
    {
        /// <summary>None is registered.</summary>
        None,

        /// <summary>One that runs with this settings folder.</summary>
        Here,

        /// <summary>One that runs with another settings folder (<c>MBUS_HOME</c>).</summary>
        Elsewhere,

        /// <summary>One whose settings folder can't be read from its definition.</summary>
        Unknown,
    }

    /// <summary>What <see cref="RegisteredAsync"/> found.</summary>
    /// <param name="Kind">Whose it is.</param>
    /// <param name="Folder">The settings folder it runs with (<see cref="Registered.Elsewhere"/>).</param>
    internal sealed record Registration(Registered Kind, string? Folder = null, IReadOnlyList<string>? Command = null);

    /// <summary>
    /// Whose the registered runner is (the original issue). The Scheduled Task and the launchd agent have one fixed name per user, so a
    /// terminal with another <c>MBUS_HOME</c> would otherwise stop, disable or remove another settings folder's runner. A
    /// definition mbus wrote passes <c>--log-file</c> inside its settings folder: on Windows read from the registered task
    /// itself (<c>schtasks /Query /XML</c>), on macOS from the plist launchd loads.
    /// </summary>
    internal static async Task<Registration> RegisteredAsync(CliHost host, RunnerServicePlan? plan, string directory, CancellationToken ct)
    {
        if (plan is null)
        {
            return new Registration(Registered.None);
        }
        string definition;
        if (plan.Exists is { } exists)
        {
            var (exit, output) = await host.RunnerServices().RunAsync(exists, ct);
            if (exit != 0)
            {
                return new Registration(Registered.None);
            }
            definition = output;
        }
        else
        {
            if (!File.Exists(plan.DefinitionPath))
            {
                return new Registration(Registered.None);
            }
            try
            {
                definition = await File.ReadAllTextAsync(plan.DefinitionPath, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Registration(Registered.Unknown);
            }
        }
        if (RegisteredCommand(plan.Platform, definition) is not { } command
            || command.SkipWhile(t => t != "runner").SkipWhile(t => t != "--log-file").Skip(1).FirstOrDefault() is not { Length: > 0 } log)
        {
            return new Registration(Registered.Unknown);
        }
        // The log is <settings folder>/logs/runner.log.
        return RunnerProcesses.IsInside(log, directory)
            ? new Registration(Registered.Here, Command: command)
            : new Registration(Registered.Elsewhere, Path.GetDirectoryName(Path.GetDirectoryName(log)) ?? log, command);
    }

    /// <summary>Says the registered runner was left alone because it isn't this settings folder's.</summary>
    private static string NotOurs(Registration registration) => registration.Kind == Registered.Elsewhere
        ? $"The runner set up to start at sign-in on this computer uses the settings in {registration.Folder}, so mbus left it alone."
        : "mbus can't tell which settings the runner set up to start at sign-in on this computer uses, so it left it alone.";

    /// <summary>The command before <c>runner</c> in the registered definition: the mbus it starts.</summary>
    private static List<string>? RegisteredMbus(RunnerServicePlan plan)
    {
        string text;
        try
        {
            text = File.ReadAllText(plan.DefinitionPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var command = RegisteredCommand(plan.Platform, text)?.TakeWhile(t => t != "runner").ToList();
        return command is { Count: > 0 } ? command : null;
    }

    /// <summary>The command line a service definition (task XML, plist) starts, split into its tokens; null when it can't be read.</summary>
    internal static List<string>? RegisteredCommand(ServicePlatform platform, string definition)
    {
        try
        {
            var root = System.Xml.Linq.XDocument.Parse(definition.Trim()).Root!;
            IEnumerable<string> tokens;
            if (platform == ServicePlatform.MacOs)
            {
                var items = root.Element("dict")!.Elements().ToList();
                var index = items.FindIndex(e => e.Name == "key" && e.Value == "ProgramArguments");
                tokens = items[index + 1].Elements("string").Select(e => e.Value);
                var argv = tokens.ToArray();
                if (argv is ["/bin/sh", "-c", var shell] && shell.LastIndexOf("; exec ", StringComparison.Ordinal) is var exec && exec >= 0)
                    tokens = ShTokens(shell[(exec + 7)..]);
            }
            else
            {
                var arguments = root.Descendants().First(e => e.Name.LocalName == "Arguments").Value;
                var start = arguments.IndexOf(" do (", StringComparison.Ordinal);
                if (start < 0)
                {
                    return null; // not the loop mbus writes
                }
                var line = arguments[(start + 5)..];
                tokens = CmdTokens(line);
                if (line.StartsWith("(if exist ", StringComparison.Ordinal))
                {
                    // The quoted hook can itself contain parentheses. Find the block terminator outside quotes.
                    var quoted = false;
                    for (var i = 0; i + 3 < line.Length; i++)
                    {
                        if (line[i] == '"') quoted = !quoted;
                        if (!quoted && line.AsSpan(i).StartsWith(") & ")) { tokens = CmdTokens(line[(i + 4)..]); break; }
                    }
                }
            }
            var command = tokens.ToList();
            return command.Count > 0 ? command : null;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or NullReferenceException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static bool HasUpdateStep(ServicePlatform platform, string definition, string directory, string? shim)
    {
        try
        {
            var root = System.Xml.Linq.XDocument.Parse(definition);
            var hook = Path.Combine(directory, "update", platform == ServicePlatform.Windows ? "wrapper.cmd" : "wrapper.sh");
            var command = RegisteredCommand(platform, definition);
            if (shim is null || command is not { Count: > 0 } || !Update.DotnetToolInstall.SamePath(command[0], shim)) return false;
            if (platform == ServicePlatform.Windows)
            {
                var args = root.Descendants().First(e => e.Name.LocalName == "Arguments").Value;
                return args.Contains($"(if exist \"{hook}\" call \"{hook}\") & ", StringComparison.Ordinal);
            }
            return root.Descendants("array").SelectMany(e => e.Elements("string")).Any(e => e.Value.Contains(
                $"if [ -f {Update.DotnetToolUpdater.Sh(hook)} ]; then /bin/sh {Update.DotnetToolUpdater.Sh(hook)}; fi; exec ", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException or ArgumentException) { return false; }
    }

    private static IEnumerable<string> ShTokens(string line)
    {
        var token = new System.Text.StringBuilder();
        bool quoted = false, started = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' ) { quoted = !quoted; started = true; }
            else if (c == '\\' && !quoted && i + 1 < line.Length) { token.Append(line[++i]); started = true; }
            else if (c == ' ' && !quoted) { if (started) { yield return token.ToString(); token.Clear(); started = false; } }
            else { token.Append(c); started = true; }
        }
        if (started) yield return token.ToString();
    }

    /// <summary>Splits a command line written by the definition (each token bare or in quotes, a quote inside doubled).</summary>
    private static IEnumerable<string> CmdTokens(string line)
    {
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && line[i] == ' ') i++;
            if (i >= line.Length) yield break;
            var token = new System.Text.StringBuilder();
            if (line[i] == '"')
            {
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            token.Append('"');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    token.Append(line[i++]);
                }
            }
            else
            {
                while (i < line.Length && line[i] != ' ') token.Append(line[i++]);
            }
            yield return token.ToString();
        }
    }

    /// <summary>The command that starts this mbus (its executable, then any arguments before the command).</summary>
    private static IReadOnlyList<string> MbusStart(CliHost host)
    {
        var launch = host.MbusCommand;
        // The MCP launch command ends with "mcp"; what comes before it starts this mbus.
        return launch.CommandLineStart
            ?? [launch.Command, .. launch.Args.Count > 0 && launch.Args[^1] == "mcp" ? launch.Args.Take(launch.Args.Count - 1) : launch.Args];
    }

    private static RunnerServicePlan PlanFor(CliHost host, CommandContext context, IReadOnlyList<string> runnerArgs, string? keyStore = null)
    {
        var mbus = MbusStart(host);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        // launchd starts agents with a bare PATH: give the runner the one it has now (git, dotnet, node, the agents).
        if (host.GetEnvironmentVariable("PATH") is { Length: > 0 } path) environment["PATH"] = path;
        foreach (var name in new[] { "MBUS_UPDATE_NUGET_SOURCE", "DOTNET_CLI_HOME" })
            if (host.GetEnvironmentVariable(name) is { Length: > 0 } value) environment[name] = value;
        if (host.GetEnvironmentVariable(EnvVars.Home) is { Length: > 0 } mbusHome) environment[EnvVars.Home] = mbusHome;
        // So the runner knows a service manager started it (its sign-in advice differs, the original issue). Never a secret here: the
        // plist is readable by every user of the Mac.
        environment[RunnerServiceDefinitions.ServiceVariable] = "1";
        if (OperatingSystem.IsMacOS()) environment[ConfigStore.KeyStoreVariable] = keyStore ?? context.Store.Load().KeyStore ?? context.Store.KeyStoreName;
        return RunnerServiceDefinitions.Plan(host.RunnerServices(), mbus, runnerArgs, host.HomeDirectory, context.Store.Directory, environment);
    }

    private static async Task RunAsync(CliHost host, IReadOnlyList<ServiceStep> steps, CancellationToken ct)
    {
        var manager = host.RunnerServices();
        foreach (var step in steps)
        {
            var (exit, output) = await manager.RunAsync(step, ct);
            if (exit != 0 && !step.MayFail)
            {
                throw new CliException($"{step} failed (exit {exit}): {output}");
            }
        }
    }

    private static async Task WritePlanAsync(CliHost host, RunnerServicePlan plan, IReadOnlyList<ServiceStep> steps)
    {
        await host.Out.WriteLineAsync($"{plan.DefinitionPath}:");
        await host.Out.WriteLineAsync(plan.Definition);
        await host.Out.WriteLineAsync();
        await host.Out.WriteLineAsync("Commands:");
        foreach (var step in steps)
        {
            await host.Out.WriteLineAsync("  " + step);
        }
    }
}
