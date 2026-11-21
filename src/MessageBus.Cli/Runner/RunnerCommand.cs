namespace MessageBus.Cli.Runner;

/// <summary>
/// <c>mbus runner</c> in the foreground: start the engine, report this machine's agents, serve the
/// service's requests until stopped (Ctrl+C) or until an update is due, then let running tasks return
/// to the queue. Running as a Windows service or launchd agent is a follow-up (see the issue).
/// </summary>
public static class RunnerCommand
{
    /// <summary>Exit code after the service asked for an update and every run finished: restart with the new version.</summary>
    public const int UpdateExitCode = 75;

    /// <summary>Runs the runner.</summary>
    /// <param name="host">Who this machine is and how to reach the server.</param>
    /// <param name="options">Slots, work folder, agent commands.</param>
    /// <param name="transport">The runner connection (the original issue).</param>
    /// <param name="log">Status lines.</param>
    /// <param name="time">Clock.</param>
    /// <param name="cancellationToken">Stops the runner.</param>
    /// <param name="updater">Installs a requested update once every task has finished (the original issue); null leaves that to whoever restarts the runner.</param>
    /// <param name="extensions">Optional parts (decrypted bundles, the original issue), made with the runner's service client.</param>
    /// <param name="view">On a terminal, the start-up summary that replaces the start-up lines (the original issue); null prints them.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(IRunnerHostContext host, RunnerOptions options, IRunnerTransport transport, TextWriter log, TimeProvider time, CancellationToken cancellationToken,
        Update.IRunnerUpdater? updater = null, Func<HttpClient, RunnerExtensions>? extensions = null, Action<RunnerEngine>? started = null,
        RunnerStartupView? view = null)
    {
        // the original issue: what the summary says again isn't printed as lines too.
        var covered = view?.Covered ?? log;
        if (OperatingSystem.IsWindows())
            await log.WriteLineAsync("Codex on Windows: " + Agents.CodexAdapter.WindowsModeDescription(options.CodexSandbox)).ConfigureAwait(false);
        if (RunnerOptions.ParseAgentTools(options.AgentTools ?? host.GetEnvironmentVariable(RunnerOptions.AgentToolsVariable)) == "refuse")
            await log.WriteLineAsync("This computer's owner refuses extra agent tools for role runs (MBUS_RUNNER_AGENT_TOOLS).").ConfigureAwait(false);
        else
            await log.WriteLineAsync("Role runs may keep extra agent tools that a role names and this computer's Codex or Claude Code has set up; MBUS_RUNNER_AGENT_TOOLS=refuse turns this off.").ConfigureAwait(false);
        using var sleep = MacSleepProtection.Start(options.KeepAwake, log);
        using var http = host.CreateServiceClient();
        // the original issue: run isolation and per-run repository credentials (owner settings in SandboxEnvVars).
        // the original issue: each run's repository token comes from the workspace's GitHub App / Azure DevOps connection.
        // Keep per-user toolchains (~/.dotnet, ~/.local/bin, Homebrew …) on every run's PATH. The start-up
        // check (with the scan below) and handing back runs that failed because of the machine.
        var toolPaths = Readiness.ToolPaths.RunVariables(host.HomeDirectory, host.GetEnvironmentVariable);
        // the original issue: the macOS sandbox's start-up check also starts each agent this runner would start, inside it.
        var probeAgents = Agents.AgentKinds.Launchable
            .Select(kind => (Name: Agents.AgentKinds.Command(kind), Path: host.LocateCommand(options.AgentCommands.TryGetValue(kind, out var c) ? c : Agents.AgentKinds.Command(kind))))
            .Where(a => a.Path is not null)
            .Select(a => new Sandbox.SandboxProbeAgent(a.Name, a.Path!))
            .ToList();
        var parts = extensions?.Invoke(http) ?? new RunnerExtensions();
        RunnerEngine? engineRef = null; // set below; runs only start once it is.
        // A clean Mac has /usr/bin/git, but running it opens Apple's installer. Startup checks,
        // commit identity and housekeeping must all use a real toolchain or skip Git entirely.
        var git = await MacGitSetup.InstalledGitPathAsync(host.LocateCommand("git"), cancellationToken).ConfigureAwait(false);
        var isolation = Sandbox.RunIsolationOptions.FromEnvironment(
            host.GetEnvironmentVariable, options.WorkRoot, host.HomeDirectory, git, host.MachineName)
            with
            {
                Credentials = new Sandbox.ServiceRepoCredentials(http, log), Telemetry = new Sandbox.ServiceRunTelemetry(http, log), ProbeAgents = probeAgents, ToolVariables = toolPaths,
                // the original issue: the owner's own projects may use the owner's git sign-in, when they chose it on My account.
                UseOwnerGit = delivery => engineRef?.Policy.UsesOwnerGit(delivery) == true,
                // the original issue: each run's mbus tools are served from here, and runs can't read mbus's settings folder (macOS).
                RunTools = parts.Tools is not null,
                KeyStore = options.KeyStore,
                SettingsDirectory = host.GetEnvironmentVariable(Hosting.EnvVars.Home) is { Length: > 0 } mbusHome ? Path.GetFullPath(mbusHome) : Path.Combine(host.HomeDirectory, ".mbus"),
                // the original issue: the hosts every run may reach, whatever its role.
                NetworkHosts = Sandbox.RunNetwork.BaseHosts(host.GetEnvironmentVariable, host.ServerUrl),
            };
        // the original issue: on Windows, one proxy for every run, on a port kept across restarts.
        using var sharedNetwork = Sandbox.RunNetworkProxy.UsedFor(isolation) && isolation.SettingsDirectory is { } settings
            ? Sandbox.RunNetworkProxy.StartStable(settings, covered, isolation.Upstream)
            : null;
        isolation = isolation with { SharedNetwork = sharedNetwork };
        var sandbox = Sandbox.RunIsolation.Create(isolation, covered);
        var verification = await NodeVerification.EnrollAsync(host.GetEnvironmentVariable,
            sandbox is Sandbox.RunIsolation { SupportsVerificationAgent: true } && parts.Tools is not null && options.KeyStore == "file",
            parts.ReturnCommits is not null, time, covered, isolation.Credentials, isolation.WorkRoot, cancellationToken,
            parts.VerificationReadiness).ConfigureAwait(false);
        var readiness = new Readiness.RunnerReadiness(covered, time, parts.Advice);
        var engine = new RunnerEngine(options, host, new HttpRunnerServiceClient(http), time, log,
            parts with
            {
                SessionUpdater = updater as Update.ISessionRunnerUpdater,
                Sandbox = new Readiness.ToolPathSandbox(sandbox, toolPaths,
                    () => Readiness.ToolPaths.RunVariables(host.HomeDirectory, host.GetEnvironmentVariable)),
                Readiness = readiness,
                Verification = verification,
                WorkActivityChanged = working => sleep?.SetWorking(working),
                // Isolation off: runs inherit this machine's environment, its mbus sign-in included.
                Tools = sandbox is NoSandbox ? null : parts.Tools,
            });
        engineRef = engine;
        started?.Invoke(engine);
        if (parts.ToolSetup is { } setup) await setup.RecoverAsync(cancellationToken).ConfigureAwait(false);
        readiness.RescanWith(engine.RescanAsync);
        // the original issue, the original issue: finished tasks' folders deleted, unused repository mirrors removed, the rest packed, at start and daily.
        var housekeeping = RunnerHousekeeping.Start(options.WorkRoot, git, host.GetEnvironmentVariable, log, time);
        await using (housekeeping.ConfigureAwait(false))
        await using (engine.ConfigureAwait(false))
        {
            // the original issue, the original issue: the dashboard chooses how many run at once (it starts at 1 until the setting arrives).
            var slots = engine.GetStatus();
            await covered.WriteLineAsync($"mbus {RunnerEnvironment.Version} runner on {host.MachineName}{(host.Location is { } l ? $" ({l})" : "")}: {slots.Slots} task(s) at once (the dashboard can set up to {slots.SlotCeiling}), runs in {options.WorkRoot}, server {host.ServerUrl}").ConfigureAwait(false);
            if (toolPaths.TryGetValue("PATH", out var runPath))
            {
                var added = runPath.Split(Path.PathSeparator).Except((host.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
                await covered.WriteLineAsync($"Tasks also get these folders on PATH: {string.Join(", ", added)}{(toolPaths.ContainsKey("DOTNET_ROOT") ? " (and DOTNET_ROOT)" : "")}.").ConfigureAwait(false);
            }
            try
            {
                await engine.RescanAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                await log.WriteLineAsync($"Couldn't send this machine's agent list: {ex.Message}").ConfigureAwait(false);
            }
            // the original issue: whose work to run, before the first task can arrive (and again on each connect: ReverseConnectionTransport).
            await engine.RefreshPolicyAsync(cancellationToken).ConfigureAwait(false);
            if (updater is Update.ISessionRunnerUpdater { Waiting: { } waiting }) engine.ResumeSavedUpdate(waiting);
            view?.Show(new RunnerStartupView.Facts { Host = host, Options = options, Engine = engine, Sandbox = sandbox, Isolation = isolation, ToolPaths = toolPaths });

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var serving = transport.RunAsync(new RunnerRouteDispatcher(engine, log), stop.Token);
            var installed = false;
            var tried = false;
            while (true)
            {
                var ready = engine.UpdateReady;
                await Task.WhenAny(serving, ready).ConfigureAwait(false);
                // the original issue: with an updater, the update is installed while the runner is still connected, so the Runners
                // page follows it, and an update that isn't installed costs nothing: the runner takes work again.
                if (updater is null || serving.IsCompleted || !ready.IsCompletedSuccessfully)
                {
                    break;
                }
                var due = await ready.ConfigureAwait(false);
                if (!engine.IsCurrentUpdate(due)) continue;
                tried = true;
                await log.WriteLineAsync($"Starting to install mbus {due.Version}.").ConfigureAwait(false);
                if (updater is not Update.ISessionRunnerUpdater)
                    await engine.ReportUpdate(Contracts.Releases.RunnerUpdateStage.Installing, due.Version).ConfigureAwait(false);
                if (await updater.InstallAsync(due, cancellationToken).ConfigureAwait(false))
                {
                    if (!engine.IsCurrentUpdate(due)) continue;
                    await engine.ReportUpdate(Contracts.Releases.RunnerUpdateStage.Restarting, due.Version).ConfigureAwait(false);
                    installed = true;
                    break;
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                if (!engine.IsCurrentUpdate(due)) continue;
                if (updater is Update.ISessionRunnerUpdater { WaitingAgain: true }) { engine.ResumeSessionWait(due); continue; }
                await log.WriteLineAsync("Nothing was installed, so this runner takes work again.").ConfigureAwait(false);
                engine.ResumeAfterUpdate(updater.LastResult);
                tried = false;
            }
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await serving.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            if (installed)
            {
                await log.WriteLineAsync(updater is Update.ISessionRunnerUpdater
                    ? "Every task has finished; restarting to apply the update through the service."
                    : "Every task has finished and the new mbus is in place; restarting.").ConfigureAwait(false);
                return UpdateExitCode;
            }
            // The transport may end because of an update too (a 4426 close; it waits for the tasks first): the server no
            // longer accepts this version, so there is no carrying on if the update isn't installed.
            if (!tried && engine.UpdateReady.IsCompletedSuccessfully)
            {
                var update = await engine.UpdateReady.ConfigureAwait(false);
                if (updater is null)
                {
                    await log.WriteLineAsync($"The server asked for mbus {update.Version}. Every task has finished; install it and start the runner again.").ConfigureAwait(false);
                    return UpdateExitCode;
                }
                // the original issue: installed → exit 75 so the service manager starts the new version; not installed → 1,
                // so it starts this version again, which takes work again.
                await log.WriteLineAsync($"Starting to install mbus {update.Version}.").ConfigureAwait(false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (await updater.InstallAsync(update, cancellationToken).ConfigureAwait(false))
                    {
                        if (!engine.IsCurrentUpdate(update)) return 1;
                        await log.WriteLineAsync("Every task has finished; restarting through the service update step.").ConfigureAwait(false);
                        return UpdateExitCode;
                    }
                    if (!engine.IsCurrentUpdate(update)) return 1;
                    if (updater is not Update.ISessionRunnerUpdater { WaitingAgain: true }) break;
                    engine.ResumeSessionWait(update);
                    update = await engine.UpdateReady.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                return 1;
            }
            await log.WriteLineAsync("Stopping: running tasks go back to the queue.").ConfigureAwait(false);
            // The connection gave up on its own (refused, or closed for good): not a clean stop.
            return cancellationToken.IsCancellationRequested ? 0 : 1;
        }
    }
}
