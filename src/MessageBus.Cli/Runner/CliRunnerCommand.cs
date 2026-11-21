// Plugs `mbus runner` into the CLI from the original issue and the runner connection from the original issue.
using System.CommandLine;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using MessageBus.Runner.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MessageBus.Cli
{
    public static partial class CliApp
    {
        static partial void AddRunnerCommands(RootCommand root, CliHost host, CommonOptions options) =>
            root.Subcommands.Add(RunnerCliCommand.Create(host, options));
    }
}

namespace MessageBus.Cli.Runner
{
    /// <summary><c>mbus runner [--work-dir path] [--log-file path]</c>.</summary>
    internal static class RunnerCliCommand
    {
        /// <summary>
        /// <c>--slots</c>, removed (the original issue): Tasks at once comes from the dashboard only. Still accepted, hidden and ignored
        /// with <see cref="SlotsIgnored"/>, so a start-up task installed with it keeps starting the runner after an update.
        /// </summary>
        public static Option<string?> RemovedSlotsOption() => new("--slots") { Hidden = true };

        /// <summary>The one line an old <c>--slots</c> gets.</summary>
        public const string SlotsIgnored = "--slots is ignored: choose Tasks at once for this machine on the dashboard.";

        internal static Option<string?> KeepAwakeOption()
        {
            var option = new Option<string?>("--keep-awake")
            {
                Description = "macOS idle sleep protection: always (default), tasks (only while running tasks), or off. The display may sleep and manual sleep still works.",
            };
            option.Validators.Add(result =>
            {
                try { RunnerOptions.ParseKeepAwake(result.GetValueOrDefault<string?>()); }
                catch (FormatException ex) { result.AddError(ex.Message); }
            });
            return option;
        }

        internal static Option<string?> CodexSandboxOption()
        {
            var codexSandbox = new Option<string?>("--codex-sandbox") { Description = "On Windows, require Codex's sandbox: always, or unelevated for its restricted-token sandbox." };
            codexSandbox.Validators.Add(result =>
            {
                try { RunnerOptions.ParseCodexSandbox(result.GetValueOrDefault<string?>()); }
                catch (FormatException ex) { result.AddError(ex.Message); }
            });
            return codexSandbox;
        }

        internal static Option<string?> AgentToolsOption()
        {
            var option = new Option<string?>("--agent-tools") { Description = "Let role runs keep named tools from this computer's agent settings: allow (default) or refuse." };
            option.Validators.Add(result =>
            {
                try { RunnerOptions.ParseAgentTools(result.GetValueOrDefault<string?>()); }
                catch (FormatException ex) { result.AddError(ex.Message); }
            });
            return option;
        }

        public static Command Create(CliHost host, CommonOptions options)
        {
            var slots = RemovedSlotsOption();
            var keepAwake = KeepAwakeOption();
            var codexSandbox = CodexSandboxOption();
            var agentTools = AgentToolsOption();
            var workDir = new Option<string?>("--work-dir") { Description = "Folder for each task's working copy (default ~/.mbus/runs)." };
            var logFile = new Option<string?>("--log-file")
            {
                Description = "Write the runner's output to this file, starting a new one when it reaches 10 MB and keeping the last three (mbus runner install sets it).",
            };
            var command = new Command("runner", "Keep this machine available for work: start an agent for each task it receives.") { slots, workDir, logFile, keepAwake, codexSandbox, agentTools };
            command.SetAction(async (parse, ct) =>
            {
                // the original issue: as a service, the runner keeps its own log (rotated by size) instead of an ever-growing redirect.
                using var file = parse.GetValue(logFile) is { Length: > 0 } path ? new Service.RunnerLogFile(Path.GetFullPath(path, host.CurrentDirectory)) : null;
                // the original issue: every line says when; on a terminal the start-up lines become one summary (RunnerStartupView).
                var terminal = RunnerStartupView.Wanted(host.ErrorIsTerminal, file is not null);
                var log = TextWriter.Synchronized(new Output.TimestampedWriter(file ?? host.Error, host.Time, timeOnly: terminal));
                if (parse.GetResult(slots) is not null)
                {
                    await log.WriteLineAsync(SlotsIgnored + " To stop passing it at start-up, run mbus runner install again.");
                }
                // After an update a service manager starts the new version; started by hand, the runner does (the original issue).
                var supervised = RunnerRestart.IsSupervised(file is not null, host.GetEnvironmentVariable, RunnerRestart.ParentProcess());
                // the original issue: one runner per settings folder, and a stop request (mbus runner stop, uninstall) that sticks: a
                // service manager that starts it again gets exit 0 at once and leaves it stopped. Started by hand, you
                // mean it to run.
                var settings = host.ConfigDirectory;
                if (supervised && Service.RunnerInstance.StopRequested(settings))
                {
                    await log.WriteLineAsync("Not starting: the runner was stopped with mbus runner stop or uninstall. To start it again: " + (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                        ? "mbus runner install"
                        : $"start mbus runner once in a terminal, or delete {Path.Combine(settings, Service.RunnerInstance.StopFile)}"));
                    return 0;
                }
                var (acquired, holder) = await Service.RunnerInstance.AcquireAsync(settings, host.Time, Service.ProcessTable.ForThisMachine(), OwnInstalls(), supervised, ct);
                using var instance = acquired;
                if (instance is null)
                {
                    await log.WriteLineAsync($"A runner is already running here{(holder is { } h ? $" (pid {h.Pid})" : "")}. To replace it: mbus runner stop, then start this one.");
                    return 0;
                }
                if (!supervised)
                {
                    // Only once this one runs: a duplicate started by hand during a stop must not undo it.
                    Service.RunnerInstance.ClearStop(settings);
                }
                using var watch = instance.WatchStop(host.Time, ct);
                ct = watch.Token;
                RunnerStartupView? view = null;
                try
                {
                    // Validate before signing in or waiting for a key, so bad service settings reach its log.
                    RunnerOptions runnerOptions;
                    try
                    {
                        runnerOptions = RunnerOptions.FromEnvironment(host.GetEnvironmentVariable, host.HomeDirectory, parse.GetValue(workDir), keepAwake: parse.GetValue(keepAwake), codexSandbox: parse.GetValue(codexSandbox), agentTools: parse.GetValue(agentTools));
                    }
                    catch (FormatException ex)
                    {
                        throw new CliException(ex.Message);
                    }
                    // the original issue: a container with a runner API key enrolls itself the first time.
                    var loaded = CommandContext.Load(host, parse, options);
                    loaded.Store.RunnerKeyReads = true;
                    loaded.Store.KeyReadLog = message => log.WriteLine(message);
                    if (loaded.Settings.Stored.MachineId is not null)
                    {
                        using var keyLogFile = file is null ? new Service.RunnerLogFile(Path.Combine(settings, "logs", "runner.log")) : null;
                        var keyLog = new KeyWaitLog(log, file is null
                            ? new Output.TimestampedWriter(keyLogFile!, host.Time, timeOnly: false)
                            : new Output.TimestampedWriter(host.Error, host.Time, timeOnly: false));
                        await RunnerKeyWait.WaitAsync(loaded.Store.LoadKeyPem, loaded.Store.KeyVault.Description, keyLog, host.Time, ct);
                    }
                    var context = await RunnerKeyEnrollment.EnsureAsync(loaded, () => CommandContext.Load(host, parse, options), log, onlyRunnerKeys: false, ct) ?? loaded;
                    context.Store.RunnerKeyReads = true;
                    context.Store.KeyReadLog = loaded.Store.KeyReadLog;
                    runnerOptions = runnerOptions with { KeyStore = context.Store.KeyStoreName };
                    if (context.Settings.Server is null || !context.Settings.CanAuthenticate(context.Store))
                    {
                        throw ServerConnection.NotSignedIn();
                    }
                    view = terminal ? new RunnerStartupView(StartupConsole(host), log, context.Settings.Stored.WorkspaceName ?? context.Settings.Stored.WorkspaceId) : null;
                    var runnerHost = new CliRunnerHostContext(context);
                    // the original issue: how the last update ended, kept beside the settings and reported at every start.
                    var lastUpdate = Update.RunnerUpdateRecord.In(context.Store.Directory);
                    var updater = Update.RunnerUpdateSetup.TryCreate(host, context.Settings, view?.Updates ?? log, lastUpdate, out var updates);
                    if (!supervised && updater is Update.ISessionRunnerUpdater)
                    {
                        updater = null;
                        updates = updates with { CanUpdate = false, Reason = "This runner was started in a terminal; run mbus runner install so it updates itself." };
                    }
                    if (updater is Update.DotnetToolUpdater toolUpdater) toolUpdater.Startup();
                    view?.UpdatesOn = updater is not null;
                    // the original issue: the dashboard's Connect asks here, in this terminal, before changing an agent's settings. A
                    // runner writing to a log file (a service) or with its input redirected has nobody to ask.
                    var interactive = file is null
                        && (!ReferenceEquals(host.In, Console.In) || (!Console.IsInputRedirected && Environment.UserInteractive));
                    var confirmer = new TerminalConfirmer(host.In, log, interactive, host.Time);
                    RunnerEngine? engine = null;
                    var connect = new AgentConnectRequests(host, confirmer, log, c => engine?.RescanAsync(c) ?? Task.CompletedTask);
                    // the original issue: a run waiting for its owner's approval is asked about here too, and answered as the person
                    // signed in to mbus (never the machine); without that sign-in the runner says how to answer instead.
                    var runApprovals = new RunApprovalPrompt(confirmer, Approvals.ApprovalsClient.For(context) is { } approvals
                        ? (id, approve, c) => AnswerRunApprovalAsync(approvals, id, approve, c)
                        : null, log);
                    var lines = view?.Log ?? log;
                    // the original issue: another mbus first on PATH would run advice that says "mbus …"; say so, and name this one in it.
                    var mbus = MbusOnPath.For(host);
                    if (mbus.Warning() is { } otherMbus)
                    {
                        await lines.WriteLineAsync(otherMbus);
                    }
                    view?.Advice = mbus.Differs ? mbus.Advice : null;
                    var exitCode = await RunnerCommand.RunAsync(runnerHost, runnerOptions, new ReverseConnectionTransport(runnerHost, lines, host.Time, updater is not null),
                        lines, host.Time, ct, updater, http => new RunnerExtensions
                        {
                            Bundles = MachineBundleSource.Create(context, http), Tools = RunToolsServer.Create(context), Connect = connect.RequestAsync,
                            SendKeptAnswer = RunToolsServer.KeptAnswerSender(context),
                            ReturnCommits = RunCodeReturn.Create(context),
                            // the original issue: the sessions this machine's mbus tools opened inboxes for, to resume them.
                            Sessions = new MessageBus.Mcp.SessionRecords(context.Store.Directory),
                            LastUpdate = () => lastUpdate.Load(CliHost.Version),
                            // the original issue: an update this mbus can't install is refused with the reason, and the runner carries on.
                            Updates = updates,
                            KeepUpdateResult = report => lastUpdate.Save(report, log),
                            RunApprovals = runApprovals,
                            ToolSetup = new ToolSetupRequests(OperatingSystem.IsMacOS() ? new MacGitSetup(host) : new WindowsGitSetup(host), http,
                                () => engine?.TryPauseForToolSetup(), c => engine?.RescanAsync(c) ?? Task.CompletedTask,
                                context.Store.Directory, runnerHost.ServerUrl, log, host.Time),
                            // the original issue: the name it signed in under goes stale when the workspace is renamed.
                            WorkspaceNamed = name => context.Store.RememberWorkspaceName(context.Settings.Stored.WorkspaceId, name),
                            Advice = mbus.Differs ? mbus.Advice : null,
                        }, created => engine = created, view);
                    if (exitCode == RunnerCommand.UpdateExitCode && updater is Update.RunnerSelfUpdater { Installed: true } && !supervised && Update.UpdateSetup.TargetPath(host) is { } installed)
                    {
                        RunnerRestart.Request(installed, log);
                    }
                    return exitCode;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return 0;
                }
                catch (CliException ex) when (file is not null)
                {
                    // Why it couldn't start (not signed in, say) belongs in the same log.
                    await log.WriteLineAsync(ex.Message);
                    return ex.ExitCode;
                }
                finally
                {
                    // Stopped before the summary was drawn: what was held is still printed.
                    view?.Release();
                }
            });
            Service.RunnerServiceCommand.AddTo(command, host, options); // the original issue: mbus runner install / uninstall
            return command;
        }

        private sealed class KeyWaitLog(TextWriter first, TextWriter second) : TextWriter
        {
            public override System.Text.Encoding Encoding => first.Encoding;
            public override async Task WriteLineAsync(string? value)
            {
                await first.WriteLineAsync(value);
                await second.WriteLineAsync(value);
            }
        }

        /// <summary>This process's file and, for a version's file, the <c>mbus</c> on PATH it belongs to (the original issue).</summary>
        private static IReadOnlyCollection<string> OwnInstalls()
        {
            if (Environment.ProcessPath is not { } self)
            {
                return [];
            }
            return Update.VersionedInstall.StableFor(self) is { } stable ? [stable, self] : [self];
        }

        /// <summary>The terminal the start-up summary is drawn on (stderr, where the runner's lines go).</summary>
        private static Spectre.Console.IAnsiConsole StartupConsole(CliHost host) => Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Out = new Spectre.Console.AnsiConsoleOutput(host.Error),
            Ansi = Spectre.Console.AnsiSupport.Detect,
            ColorSystem = Spectre.Console.ColorSystemSupport.Detect,
            Interactive = Spectre.Console.InteractionSupport.No,
        });

        /// <summary>Records a yes or no typed in the runner's terminal (the original issue) and says what happened.</summary>
        private static async Task<string> AnswerRunApprovalAsync(Approvals.ApprovalsClient approvals, string approvalId, bool approve, CancellationToken ct)
        {
            var result = await approvals.DecideAsync(approvalId, approve, null, ct).ConfigureAwait(false);
            if (result.Succeeded) return approve ? "Approved: it starts in a moment." : "Declined: it goes to another machine.";
            return result.Code == Approvals.ApprovalCodes.SignInAgain && result.DashboardUrl is { } url
                ? $"This one is high-risk, so answer it in the dashboard after signing in again: {url}"
                : $"Couldn't record your answer: {result.Message}";
        }
    }

    /// <summary>The runner's view of the signed-in machine (the original issue's settings, key and agent scan).</summary>
    internal sealed class CliRunnerHostContext(CommandContext context) : IRunnerHostContext
    {
        public Uri ServerUrl => context.Settings.Server!;

        public AgentCapacityStore CapacityStore => context.Store.AgentCapacity;

        public string? MachineId => context.Settings.Stored.MachineId ?? context.Host.GetEnvironmentVariable(RunnerEnvVars.MachineId);

        public string MachineName => context.Settings.MachineName;

        public string? Location => context.Settings.Location;

        public RunnerPolicy Policy { get; } = RunnerEnvironment.ReadPolicy(context.Host.GetEnvironmentVariable);

        public IReadOnlyDictionary<string, AgentKind> AgentIds { get; } = RunnerEnvironment.ReadAgentIds(context.Host.GetEnvironmentVariable);

        public string HomeDirectory => context.Host.HomeDirectory;

        public HttpClient CreateServiceClient() =>
            ServerConnection.CreateAuthenticatedClient(context.Host, context.Settings, context.Store, "runner");

        public async Task ConnectAsync(ConnectRequest request, Func<CancellationToken, Task> connect, CancellationToken ct)
        {
            ServerConnection.DemandStoredCredentialScope(context.Settings);
            using var gate = new ServerSendGate(context.Host, context.Store, ServerUrl);
            var signer = context.Settings.ApiKey is null
                ? ServerConnection.CreateSigner(context.Host, context.Settings, context.Store) : null;
            await RunnerHandshake.ConnectAsync(gate, request, signer, context.Settings.ApiKey,
                context.Settings.Stored.WorkspaceId, connect, ct).ConfigureAwait(false);
        }

        public IReadOnlyList<KeyValuePair<string, string>> CreateConnectHeaders(string method, string pathAndQuery)
        {
            var headers = new List<KeyValuePair<string, string>>
            {
                new(MbusHeaders.ClientComponent, "runner"),
                new(MbusHeaders.ClientVersion, CliHost.Version),
            };
            if (context.Settings.Stored.WorkspaceId is { } workspace)
            {
                headers.Add(new(MbusHeaders.Workspace, workspace));
            }
            if (context.Settings.ApiKey is { } apiKey)
            {
                headers.Add(new("Authorization", $"{Contracts.Security.AuthSchemes.ApiKey} {apiKey}"));
            }
            else if (ServerConnection.CreateSigner(context.Host, context.Settings, context.Store) is { } signer)
            {
                headers.AddRange(signer.CreateHeaders(method, pathAndQuery, []));
            }
            return headers;
        }

        public string? LocateCommand(string command) =>
            Path.IsPathFullyQualified(command)
                ? (File.Exists(command) ? command : null)
                : ExecutableLocator.Find(context.Host, command)?.Path;

        public async Task<IReadOnlyDictionary<string, bool>?> CheckRoleKeysAsync(CancellationToken cancellationToken) =>
            await MessageBus.Cli.Readiness.RoleKeyChecks.ReadAsync(context, cancellationToken).ConfigureAwait(false);

        public async Task<MachineInventory> BuildInventoryAsync(int slots, CancellationToken cancellationToken)
        {
            var keyProblem = await Readiness.WorkspaceKeyReadiness.CheckAsync(context, cancellationToken).ConfigureAwait(false);
            if (keyProblem.CouldNotCheck) await context.Host.Error.WriteLineAsync(keyProblem.Problem).ConfigureAwait(false);
            var inventory = await AgentScanner.BuildInventoryAsync(context.Host, MachineId, Location, cancellationToken).ConfigureAwait(false);
            inventory = Readiness.WorkspaceKeyReadiness.Apply(inventory with { Slots = slots }, keyProblem);
            return await MessageBus.Cli.Readiness.RoleKeyChecks.CheckAsync(context, inventory, cancellationToken).ConfigureAwait(false);
        }

        public string? GetEnvironmentVariable(string name) => context.Host.GetEnvironmentVariable(name);

        public IReadOnlyDictionary<string, string> AgentEnvironment() => context.Store.AgentEnvironment.Load();
    }

    /// <summary>The runner connection from the original issue: dials the service and serves the runner routes over the socket.</summary>
    /// <remarks>
    /// A dead service is noticed end to end (the original issue): each heartbeat (over HTTPS, every minute even when idle) says
    /// which connection it belongs to, and when the service answers that it doesn't hold that connection, the
    /// runner drops it and reconnects, even though a proxy or relay in between keeps its socket looking open.
    /// </remarks>
    public sealed class ReverseConnectionTransport(IRunnerHostContext host, TextWriter log, TimeProvider time, bool selfUpdate = false) : IRunnerTransport
    {
        /// <summary>
        /// A heartbeat sent this soon after connecting may have reached the service before the connection did,
        /// so its "not connected" answer doesn't count.
        /// </summary>
        public static readonly TimeSpan ConnectGrace = TimeSpan.FromSeconds(15);

        /// <summary>The wait before reconnect attempt n; Groundhog's 1–30 s schedule when null.</summary>
        public Func<int, TimeSpan>? ReconnectDelay { get; init; }

        /// <inheritdoc />
        public async Task RunAsync(RunnerRouteDispatcher dispatcher, CancellationToken cancellationToken)
        {
            var session = Guid.NewGuid().ToString("N");
            var engine = dispatcher.Engine;
            engine.SessionId = session;
            ReverseConnectionClient? client = null;
            long connectedAt = 0; // UTC ticks of the open connection; 0 while not connected
            void OnServiceLacksConnection(DateTimeOffset sentAt)
            {
                var opened = Interlocked.Read(ref connectedAt);
                if (opened != 0 && sentAt.UtcTicks - opened >= ConnectGrace.Ticks
                    && client?.DropConnection("The service doesn't have this runner's connection (it restarted, or a proxy in between kept a dead connection open).") == true)
                {
                    Interlocked.Exchange(ref connectedAt, 0);
                }
            }
            var options = RunnerTransport.ClientOptions(host.ServerUrl, app => app.Run(async http =>
            {
                string? body = null;
                if (http.Request.ContentLength is not 0)
                {
                    using var reader = new StreamReader(http.Request.Body);
                    body = await reader.ReadToEndAsync(http.RequestAborted).ConfigureAwait(false);
                }
                var answer = await dispatcher.HandleAsync(http.Request.Method, http.Request.Path.Value ?? "/", body, http.RequestAborted).ConfigureAwait(false);
                http.Response.StatusCode = answer.StatusCode;
                if (answer.Json is { } json)
                {
                    http.Response.ContentType = "application/json";
                    await http.Response.WriteAsync(json, http.RequestAborted).ConfigureAwait(false);
                }
            })) with
            {
                TimeProvider = time,
                ExecuteConnect = host.ConnectAsync,
                IsTerminalPreparationException = RunnerHandshake.IsTerminal,
                PrepareRequest = (request, _) =>
                {
                    request.SetHeader(MbusHeaders.Session, session);
                    // the original issue: the slots in use now (the dashboard may have changed them), not the starting count.
                    var status = dispatcher.Engine.GetStatus();
                    var free = status.Slots - status.ActiveDeliveryIds.Count;
                    request.SetHeader(MbusHeaders.Slots, Math.Max(0, free).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return ValueTask.CompletedTask;
                },
                OnStatus = status =>
                {
                    Interlocked.Exchange(ref connectedAt, status.State == ReverseConnectionState.Connected ? time.GetUtcNow().UtcTicks : 0);
                    if (status.State == ReverseConnectionState.Connected)
                    {
                        engine.ConfirmUpdateConnected();
                        // the original issue: the owner may have changed whose work it runs while it was away.
                        _ = engine.RefreshPolicyAsync(cancellationToken);
                    }
                    log.WriteLine($"Runner connection: {status.State.ToString().ToLowerInvariant()}{(status.Reason is { } r ? $" ({r})" : "")}");
                },
            };
            if (ReconnectDelay is { } delay)
            {
                options = options with { ReconnectDelay = delay };
            }
            client = new ReverseConnectionClient(options);
            engine.ServiceLacksConnection += OnServiceLacksConnection;
            ReverseConnectionStop stop;
            try
            {
                stop = await client.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                engine.ServiceLacksConnection -= OnServiceLacksConnection;
            }
            if (stop.Reason != ReverseConnectionStopReason.Cancelled)
            {
                await log.WriteLineAsync(stop.Message).ConfigureAwait(false);
            }
            if (selfUpdate && stop.CloseCode == Contracts.Runner.RunnerCloseCodes.UpdateRequired)
            {
                // the original issue: this mbus is older than the server accepts. Update like an update request: running
                // tasks finish first (their heartbeats and results go over HTTPS, not this connection).
                await Update.RunnerUpdateSetup.UpdateWhenIdleAsync(dispatcher.Engine, log, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

namespace MessageBus.Cli.Runner
{
    /// <summary>A task's git bundle, decrypted with this machine's keys by the local MCP server's code (the original issue).</summary>
    internal sealed class MachineBundleSource(MessageBus.Mcp.MachineBundles bundles) : IBundleSource
    {
        private static readonly HttpClient Links = MessageBus.Attachments.Client.AttachmentTransfers.CreateHttpClient();

        public static IBundleSource? Create(CommandContext context, HttpClient api) =>
            context.Settings.Stored.WorkspaceId is { } workspace
                ? new MachineBundleSource(new MessageBus.Mcp.MachineBundles(api, Links, workspace, context.Settings.Stored.MachineId,
                    context.Store.Directory, context.Host.Error, context.Host.Time, context.Store.LoadKeyPem,
                    // the original issue: asking a task's sender for earlier history goes through the service's MCP tools, as the run's agent.
                    context.Settings.Server is null ? null : Server.ServerConnection.CreateAuthenticatedClient(context.Host, context.Settings, context.Store, "mcp")))
                : null;

        public Task<string?> FetchAsync(TaskDelivery delivery, AttachmentRef bundle, string runDirectory, CancellationToken cancellationToken) =>
            bundles.FetchAsync(delivery, bundle, runDirectory, cancellationToken);

        public Task<string?> RequestHistoryAsync(TaskDelivery delivery, AttachmentRef bundle, string runDirectory,
            Func<ProgressRequest, CancellationToken, Task> ask, TimeSpan wait, CancellationToken cancellationToken) =>
            bundles.RequestHistoryAsync(delivery, bundle, runDirectory, ask, wait, cancellationToken);
    }
}
