using MessageBus.Cli.Update;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>Optional parts of the engine that other issues provide.</summary>
public sealed record RunnerExtensions
{
    /// <summary>Gets decrypted git bundles (the original issue, the original issue).</summary>
    public IBundleSource? Bundles { get; init; }

    internal NodeVerification? Verification { get; init; }
    internal INodeVerificationReadiness? VerificationReadiness { get; init; }

    /// <summary>Wraps agent commands in a sandbox (the original issue).</summary>
    public IRunSandbox Sandbox { get; init; } = new NoSandbox();

    /// <summary>Serves each run's mbus tools from the runner, with a credential of the run's own (the original issue); null: the agent's mbus mcp signs in as this machine.</summary>
    public IRunTools? Tools { get; init; }

    /// <summary>
    /// Once a run's agent has ended without its settle going through: sends the answer kept from a settle that was refused
    /// for an attachment (the original issue; <see cref="MessageBus.Mcp.LocalServer.SendKeptAnswerAsync"/>), recording its result in the run's
    /// settle file. Null: such an answer isn't sent, and the run ends as "Agent result not received".
    /// </summary>
    public Func<RunWorkspace, TaskDelivery, CancellationToken, Task<bool>>? SendKeptAnswer { get; init; }

    /// <summary>Returns a successful run's missing branch bundle; false keeps the worktree.</summary>
    public Func<RunWorkspace, TaskDelivery, string, string?, CancellationToken, Task<bool>>? ReturnCommits { get; init; }

    /// <summary>Checks role instructions (signature check from the original issue).</summary>
    public IInstructionVerifier Instructions { get; init; } = new InstructionVerifier();

    /// <summary>Adapters by kind; default: one per <see cref="AgentKinds.Launchable"/> kind.</summary>
    public Func<AgentKind, IAgentAdapter?>? Adapters { get; init; }

    /// <summary>Declines work for agents that aren't ready here and hands back runs that failed because of the machine (the original issue, the original issue).</summary>
    public Readiness.RunnerReadiness? Readiness { get; init; }

    /// <summary>
    /// Rewrites an mbus command the owner is told to run so it starts this mbus (the original issue: its full path when the mbus on PATH
    /// is another one; <see cref="Hosting.MbusOnPath.Advice"/>), in what this machine prints (the runner's log, its start-up
    /// summary). Never in what goes to the service: the path names the user's folders. Null: printed as they are.
    /// </summary>
    public Func<string, string>? Advice { get; init; }

    /// <summary>Sets up an agent to use mbus after the person at the machine says yes (the original issue); null: this runner can't ask anyone.</summary>
    public Func<ConnectAgentRequest, CancellationToken, Task<ConnectAgentResult>>? Connect { get; init; }

    /// <summary>
    /// The sessions this machine's agents opened inboxes for (the original issue, written by the local mbus tools); with them a reply to a
    /// closed session resumes that session. Null: every message to a session inbox starts a new session.
    /// </summary>
    public MessageBus.Mcp.SessionRecords? Sessions { get; init; }

    /// <summary>How this runner's last update ended (the original issue), sent with every inventory; null: nothing to report.</summary>
    public Func<Contracts.Releases.RunnerUpdateReport?>? LastUpdate { get; init; }

    /// <summary>
    /// Whether this mbus can update itself, and why not (the original issue), sent with every inventory. When it can't, an update
    /// request is answered "not installed" with that reason and the runner keeps taking work. Null: not said (the
    /// runner then stops for whoever restarts it to install the update).
    /// </summary>
    public Contracts.Releases.RunnerUpdateAbility? Updates { get; init; }

    /// <summary>Keeps the result of an update that was refused without being tried (the original issue), for later inventories.</summary>
    public Action<Contracts.Releases.RunnerUpdateReport>? KeepUpdateResult { get; init; }

    /// <summary>Asks in the runner's terminal while a run waits for its owner's approval (the original issue); null: the run just waits.</summary>
    public RunApprovalPrompt? RunApprovals { get; init; }

    /// <summary>Owner-approved installation of prerequisites on this machine, opt-in locally (the original issue).</summary>
    public IToolSetupRequests? ToolSetup { get; init; }

    /// <summary>Keeps the workspace's current name when the service's policy says it (the original issue); null: nothing keeps it.</summary>
    public Action<string>? WorkspaceNamed { get; init; }

    /// <summary>Local dotnet-tool holders and the persisted wait across restarts.</summary>
    public ISessionRunnerUpdater? SessionUpdater { get; init; }

    /// <summary>Called under the engine lock when the running task count changes.</summary>
    internal Action<bool>? WorkActivityChanged { get; init; }
}

/// <summary>
/// The runner: takes deliveries up to its slot count, runs each in its own worktree, keeps leases
/// alive with heartbeats, and answers the service's cancel, drain, rescan, update and status
/// requests. Transport-agnostic: <see cref="RunnerRouteDispatcher"/> maps <see cref="RunnerRoutes"/>
/// onto it for whichever connection hosts them (the original issue).
/// </summary>
public sealed class RunnerEngine : IAsyncDisposable
{
    private readonly RunnerOptions _options;
    private readonly IRunnerHostContext _host;
    private readonly IRunnerServiceClient _service;
    private readonly TimeProvider _time;
    private readonly TextWriter _log;
    private readonly RunnerExtensions _ext;
    private readonly WorkAcceptance _acceptance;
    private readonly RunServices _services;
    private readonly Dictionary<string, AgentRun> _runs = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, AgentKind> _registryAgentIds = new Dictionary<string, AgentKind>();
    private IReadOnlyDictionary<AgentKind, string?> _agentVersions = new Dictionary<AgentKind, string?>();
    private readonly object _gate = new();
    private readonly object _updateReportGate = new();
    private Task _updateReports = Task.CompletedTask;
    private TaskCompletionSource<UpdateRequest> _updateReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();
    private ITimer? _heartbeat;
    private TimeSpan _heartbeatPeriod;
    private ITimer? _drainTimer;
    private int _beating;
    private bool _draining;
    private string? _managedDrainId;
    private bool IsDraining => _draining || _managedDrainId is not null;
    private bool _userDraining;
    private bool _offlineForUpdate;
    private bool _waitingForSessions;
    private int _holderCount = -1;
    private bool _releasePolicyLoaded;
    private UpdateRequest? _savedUpdate;
    private ITimer? _sessionTimer;
    private bool _settingUp;
    private UpdateRequest? _update;
    // the original issue: the workspace's release settings (channel, pinned version, mirror), for updates the runner starts itself.
    private Contracts.Releases.RunnerReleasePolicy? _releases;
    private string? _workspaceName;
    // the original issue: tasks at once now, as set from the dashboard; never above _ceiling.
    private int _slots;
    // the original issue, the original issue: the most the dashboard may set: the most for this machine's size.
    private readonly int _ceiling;
    private readonly int _machineMax;

    /// <summary>Creates the engine.</summary>
    public RunnerEngine(RunnerOptions options, IRunnerHostContext host, IRunnerServiceClient service, TimeProvider time, TextWriter log, RunnerExtensions? extensions = null)
    {
        options = options with { AgentTools = RunnerOptions.ParseAgentTools(options.AgentTools ?? host.GetEnvironmentVariable(RunnerOptions.AgentToolsVariable)) };
        _options = options;
        _machineMax = SlotAdvice.MostForMachine(Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        _ceiling = Math.Max(options.Slots, _machineMax);
        _slots = Math.Min(options.Slots, _ceiling);
        _host = host;
        _time = time;
        _log = TextWriter.Synchronized(log);
        _ext = extensions ?? new RunnerExtensions();
        _service = _ext.Readiness?.Watch(service) ?? service;
        _acceptance = new WorkAcceptance(host.Policy);
        _services = new RunServices(options, host, _service, time, _log, _ext.Sandbox, _ext.Bundles, _stopping.Token, _ext.Tools, _ext.RunApprovals, _ext.SendKeptAnswer, _ext.ReturnCommits, _ext.Verification);
        Directory.CreateDirectory(options.WorkRoot);
        // Heartbeats start now, not with the first task: an idle runner's beats keep its agents online and
        // its free slots current on the service (a delivery can only shorten the interval).
        lock (_gate)
        {
            EnsureHeartbeat(options.DefaultHeartbeat);
        }
    }

    /// <summary>Completes once an update was requested and every run has finished: time to exit and update.</summary>
    public Task<UpdateRequest> UpdateReady
    {
        get
        {
            lock (_gate)
            {
                return _updateReady.Task;
            }
        }
    }

    /// <summary>The runner connection's session (<c>X-Mbus-Session</c>), sent with heartbeats so the service can say whether it holds that connection.</summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// A heartbeat's answer said the service doesn't hold this runner's connection (the original issue); the value is when that
    /// heartbeat was sent. The connection's owner reconnects if it was already open then.
    /// </summary>
    public event Action<DateTimeOffset>? ServiceLacksConnection;

    /// <summary>Answers a delivery at once: accepted (the run starts) or declined (it goes back to the queue).</summary>
    public DeliveryDecision Deliver(TaskDelivery delivery)
    {
        if (delivery.Verification is not null || delivery.Envelope.Verification is not null)
        {
            if (_ext.Verification is not { } verifier)
                return Decline(DeclineReason.VerificationUnavailable, "This runner has no approved isolated verification runtime.");
            if (verifier.Refusal(delivery) is { } unsupported)
                return Decline(DeclineReason.VerificationUnavailable, unsupported);
        }
        else if (_ext.Verification is not null)
        {
            return Decline(DeclineReason.MachineNotReady, "This dedicated runner accepts only required Node verification deliveries.");
        }
        AgentRun run;
        if (delivery.Envelope.Target is InboxTarget && delivery.Envelope.Type == ProgressRequestKinds.GitHistoryMessageType)
        {
            // the original issue: code a run of this machine asked its sender for, sent to the inbox that run opened: the run takes it
            // from there itself. Never an agent run of its own.
            return Decline(DeclineReason.SessionOpen, "The task that asked for this code reads it itself.");
        }
        lock (_gate)
        {
            if (_runs.ContainsKey(delivery.DeliveryId))
            {
                return new DeliveryDecision { Accepted = true, Detail = "Already running." };
            }
            if (IsDraining || _settingUp || _ext.ToolSetup?.BlocksWork == true)
            {
                return Decline(DeclineReason.Draining, "This machine is taking no new work.");
            }
            if (_runs.Count >= _slots)
            {
                return Decline(DeclineReason.NoFreeSlot, $"All {_slots} slots are busy.");
            }
            if (_acceptance.Check(delivery, _time.GetUtcNow()) is { } refusal)
            {
                return Decline(refusal.Reason, refusal.Detail);
            }
            if (delivery.Instructions is { } instructions && _ext.Instructions.Verify(instructions) is { } bad)
            {
                return Decline(DeclineReason.BadInstructions, bad);
            }
            if (ResolveAgent(delivery.AgentId) is not { } agent)
            {
                return Decline(DeclineReason.AgentUnavailable, $"No agent '{delivery.AgentId}' on this machine.");
            }
            if (_ext.Readiness?.Admit(delivery, agent.Adapter.Kind) is { } notReady)
            {
                return Decline(DeclineReason.MachineNotReady, notReady);
            }
            MessageBus.Mcp.SessionRecord? resume = null;
            string? handled = null;
            if (delivery.Envelope.Target is InboxTarget inbox)
            {
                var session = PlanSession(delivery, inbox.InboxId, agent.Adapter);
                if (session.Decline is { } declined)
                {
                    return declined;
                }
                (resume, handled) = (session.Resume, session.Handled);
                if (resume is not null)
                {
                    _log.WriteLine($"{delivery.DeliveryId}: resuming the {resume.AgentKind} session {resume.SessionId} in {resume.Folder} for a message to its inbox.");
                }
            }

            var (profile, restricted) = EffectiveProfile(delivery);
            run = new AgentRun(_services, delivery, agent.Adapter, agent.Executable, profile, restricted, resume, handled,
                resume is { OriginAuthority: MessageBus.Mcp.ProposalAuthority.Interactive, CoordinatorRecordId.Length: > 0 }
                    && resume.MachineId is not null && resume.MachineId == _host.MachineId
                    && resume.AgentId == delivery.AgentId && resume.InboxId == (delivery.Envelope.Target as InboxTarget)?.InboxId
                    ? MessageBus.Mcp.ProposalAuthority.ResumedCoordinator : MessageBus.Mcp.ProposalAuthority.Worker);
            _runs[delivery.DeliveryId] = run;
            _ext.WorkActivityChanged?.Invoke(true);
            EnsureHeartbeat(TimeSpan.FromSeconds(Math.Max(1, delivery.HeartbeatSeconds)));
            run.Start();
            // the original issue: a line when a task starts, as there is when it finishes.
            _log.WriteLine($"{delivery.DeliveryId}: started{(delivery.Role is { Length: > 0 } role ? $" for {role}" : "")} with {AgentKinds.DisplayName(agent.Adapter.Kind)}"
                + (delivery.Attempt > 1 ? $" (attempt {delivery.Attempt})." : "."));
        }
        _ = run.Completion.ContinueWith(t => Finished(run, t), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return new DeliveryDecision { Accepted = true };
    }

    /// <summary>Stops a run and settles it as cancelled. False when it isn't running here.</summary>
    public bool Cancel(CancelDelivery cancel)
    {
        AgentRun? run;
        lock (_gate)
        {
            _runs.TryGetValue(cancel.DeliveryId, out run);
        }
        const string suspendedPrefix = "Workspace suspended (";
        if (run is not null && cancel.Reason is { } reason && reason.StartsWith(suspendedPrefix, StringComparison.Ordinal)
            && reason.IndexOf(')', suspendedPrefix.Length) is var end && end > suspendedPrefix.Length)
            run.StopForSuspension(reason[suspendedPrefix.Length..end]);
        else run?.Stop(StopReason.Cancelled, cancel.Reason ?? "Cancelled.");
        return run is not null;
    }

    /// <summary>Takes no new work; at <see cref="DrainRequest.Deadline"/> stops what is still running (it returns to the queue).</summary>
    public void Drain(DrainRequest request)
    {
        lock (_gate)
        {
            if (request.ManagedDrainId is { } operation)
            {
                if (string.IsNullOrWhiteSpace(operation)) return;
                if (request.Resume)
                {
                    if (_managedDrainId == operation) _managedDrainId = null;
                }
                else if (_managedDrainId is null || _managedDrainId == operation)
                {
                    _managedDrainId = operation;
                }
                // Managed drains never cancel active work, even if the request contains a deadline.
                return;
            }
            if (request.Resume) return;
            _userDraining = true;
            _draining = true;
            if (request.Deadline is { } deadline)
            {
                var due = deadline - _time.GetUtcNow();
                _drainTimer?.Dispose();
                if (due <= TimeSpan.Zero)
                {
                    StopAll(StopReason.Drained, "The machine stopped taking work.");
                }
                else
                {
                    _drainTimer = _time.CreateTimer(_ => StopAll(StopReason.Drained, "The machine stopped taking work."), null, due, Timeout.InfiniteTimeSpan);
                }
            }
            CheckUpdateReady();
        }
    }

    /// <summary>
    /// Drains, then completes <see cref="UpdateReady"/> once idle. Installing the release is the original issue. What the request
    /// leaves out (a 4426 close asks for "latest" only) comes from the workspace's release settings (the original issue).
    /// </summary>
    public void RequestUpdate(UpdateRequest request, bool offline = false)
    {
        if (Volatile.Read(ref _releases) is { } releases)
        {
            request = request with
            {
                Channel = request.Channel ?? releases.Channel,
                Mirror = request.Mirror ?? releases.Mirror,
                Pinned = request.Pinned || (request.Version == RunnerUpdates.Latest && releases.PinnedVersion is not null),
                Version = request.Version == Contracts.Releases.ReleaseFeed.LatestVersion && releases.PinnedVersion is { } pinned ? pinned : request.Version,
            };
        }
        if (_ext.Updates is { CanUpdate: false } off)
        {
            // the original issue: stopping work for an update that can't be installed would only take this machine away. Say why and carry on.
            var reason = off.Reason ?? "This mbus can't update itself.";
            _log.WriteLine($"The server asked for mbus {request.Version}. Not installed: {reason}");
            var report = new Contracts.Releases.RunnerUpdateReport
            {
                Outcome = Contracts.Releases.RunnerUpdateOutcome.NotInstalled,
                FromVersion = RunnerEnvironment.Version,
                ToVersion = request.Version == Contracts.Releases.ReleaseFeed.LatestVersion ? null : request.Version,
                Reason = reason,
                At = _time.GetUtcNow(),
            };
            _ext.KeepUpdateResult?.Invoke(report);
            ReportUpdate(Contracts.Releases.RunnerUpdateStage.Ended, null, report);
            return;
        }
        string[] tasks;
        lock (_gate)
        {
            _offlineForUpdate |= offline;
            _sessionTimer?.Dispose();
            _sessionTimer = null;
            _waitingForSessions = false;
            _holderCount = -1;
            _draining = _userDraining || _offlineForUpdate;
            if (_updateReady.Task.IsCompleted) _updateReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _update = request;
            if (_ext.SessionUpdater is { } tool)
            {
                var comparison = Contracts.Releases.ReleaseVersion.Compare(tool.CurrentVersion, request.Version);
                if (request.Version != RunnerUpdates.Latest && (!Contracts.Releases.ReleaseFeed.IsPinnableVersion(request.Version)
                    || comparison == 0 || (!request.Pinned && comparison > 0)))
                {
                    tool.ClearWaiting();
                    _update = null;
                    var report = new Contracts.Releases.RunnerUpdateReport { Outcome = Contracts.Releases.RunnerUpdateOutcome.NotInstalled,
                        FromVersion = tool.CurrentVersion, ToVersion = request.Version, At = _time.GetUtcNow(),
                        Reason = comparison == 0 ? "This version is already installed." : "The requested version is invalid or is an unpinned downgrade." };
                    _ext.KeepUpdateResult?.Invoke(report);
                    ReportUpdate(Contracts.Releases.RunnerUpdateStage.Ended, null, report);
                    return;
                }
                tool.KeepWaiting(request);
                if (ScanSessions()) return;
            }
            _draining = true;
            tasks = _runs.Values.Select(run =>
                $"{(run.Delivery.Envelope.Title ?? run.Delivery.Envelope.Type).ReplaceLineEndings(" ")} ({AgentKinds.DisplayName(run.AgentKind)})").ToArray();
        }
        _log.WriteLine(tasks.Length == 0
            ? $"Update to {request.Version} requested; no running tasks to wait for."
            : $"Update to {request.Version} requested; waiting for {tasks.Length} running task{(tasks.Length == 1 ? "" : "s")} to finish: {string.Join("; ", tasks)}.");
        if (tasks.Length > 0)
        {
            ReportUpdate(Contracts.Releases.RunnerUpdateStage.Waiting, request.Version);
        }
        lock (_gate) { _draining = true; CheckUpdateReady(); }
    }

    /// <summary>
    /// The update that was due ended with nothing installed (the original issue): the runner takes work again, tells the service how
    /// it ended, and can be asked to update again.
    /// </summary>
    /// <param name="report">How it ended; null when there was nothing to install.</param>
    public void ResumeAfterUpdate(Contracts.Releases.RunnerUpdateReport? report)
    {
        lock (_gate)
        {
            _update = null;
            _ext.SessionUpdater?.ClearWaiting();
            _draining = _userDraining || _offlineForUpdate;
            if (!_userDraining)
            {
                _drainTimer?.Dispose();
                _drainTimer = null;
            }
            _updateReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        ReportUpdate(Contracts.Releases.RunnerUpdateStage.Ended, null, report);
    }

    /// <summary>Tells the service where the update stands (the original issue). Best effort: the update itself never waits for or fails on it.</summary>
    public Task ReportUpdate(Contracts.Releases.RunnerUpdateStage stage, string? version, Contracts.Releases.RunnerUpdateReport? report = null, int? blockedBy = null)
    {
        var progress = new Contracts.Releases.RunnerUpdateProgress
        {
            BlockedBy = blockedBy is { } count ? new() { Count = count } : null,
            Stage = stage,
            Version = version is { Length: > 0 } && version != Contracts.Releases.ReleaseFeed.LatestVersion ? version : null,
            Report = report,
        };
        lock (_updateReportGate)
        {
            var previous = _updateReports;
            // A zero-holder scan can complete the drain immediately. Keep its Waiting report ahead
            // of Restarting even when both sends are scheduled before either reaches the service.
            return _updateReports = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _service.UpdateProgressAsync(progress, limit.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
                {
                }
            });
        }
    }

    /// <summary>
    /// Sets how many tasks run at once (the original issue, from the dashboard), up to the most for this machine's size (the original issue, the original issue);
    /// null goes back to the number it started with. Raising lets new
    /// tasks in at once; lowering stops nothing that is running, it only holds back new tasks until fewer are running
    /// than the new number.
    /// </summary>
    public RunnerStatus SetSlots(SlotsRequest request)
    {
        lock (_gate)
        {
            _slots = Math.Clamp(request.Slots ?? _options.Slots, 1, _ceiling);
        }
        return GetStatus();
    }

    /// <summary>Scans for agents and sends the inventory.</summary>
    public async Task RescanAsync(CancellationToken cancellationToken)
    {
        var slots = Volatile.Read(ref _slots);
        var inventory = await _host.BuildInventoryAsync(slots, cancellationToken).ConfigureAwait(false);
        // the original issue: with the stored agent sign-ins, as the agents see them.
        inventory = _ext.Readiness?.Scanned(inventory, new MessageBus.Cli.Agents.SignInEnvironment(_host.GetEnvironmentVariable, _host.AgentEnvironment()).Get) ?? inventory;
        // the original issue: the ceiling and this machine's size, for the dashboard's slot setting and its suggestion.
        inventory = inventory with
        {
            Capabilities = _ext.Verification is { IsReady: true }
                ? [.. inventory.Capabilities.Where(c => c != CapabilityNames.NodeVerificationV1), CapabilityNames.NodeVerificationV1]
                : [.. inventory.Capabilities.Where(c => c != CapabilityNames.NodeVerificationV1)],
            Slots = slots,
            SlotCeiling = _ceiling,
            SlotMachineMax = _machineMax,
            CpuCount = inventory.CpuCount ?? Environment.ProcessorCount,
            MemoryBytes = inventory.MemoryBytes ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        };
        // the original issue: how runs are kept apart here, for the Runners page.
        inventory = inventory with { Isolation = inventory.Isolation ?? _ext.Sandbox.Report };
        // the original issue: the last update's result, so the Runners page shows it once this runner is back.
        inventory = inventory with { LastUpdate = inventory.LastUpdate ?? _ext.LastUpdate?.Invoke() };
        // the original issue: whether this mbus can update itself, so the page offers Update only where it works.
        inventory = inventory with { Updates = inventory.Updates ?? _ext.Updates };
        // the original issue: which OpenCode is installed decides its command line.
        _agentVersions = inventory.Agents.Where(a => a is { Found: true, Version: { Length: > 0 } })
            .GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.First().Version);
        LastInventory = inventory;
        IReadOnlyList<AgentInfo> agents;
        try { agents = await _service.InventoryAsync(inventory, cancellationToken).ConfigureAwait(false); }
        catch (MessageBus.Cli.Server.WorkspaceSuspendedException) { _suspensionRecoveryNeeded = true; throw; }
        RegistryAgents = agents;
        // Deliveries name the registry's agent id (agt_…); the service's answer says which kind each is.
        _registryAgentIds = agents.Where(a => a.MachineId == inventory.MachineId || string.IsNullOrEmpty(inventory.MachineId))
            .GroupBy(a => a.AgentId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);
    }

    /// <summary>The last scan this runner sent (the original issue: its start-up summary); null before the first.</summary>
    public MachineInventory? LastInventory { get; private set; }

    /// <summary>The registry's agents (with their roles) from the service's answer to the last scan; empty until it answered.</summary>
    public IReadOnlyList<AgentInfo> RegistryAgents { get; private set; } = [];

    /// <summary>The workspace's release settings from the last work policy (the original issue); null until the service said.</summary>
    public Contracts.Releases.RunnerReleasePolicy? Releases => Volatile.Read(ref _releases);

    /// <summary>The workspace's name as the service last said it (the original issue); null until it does.</summary>
    public string? WorkspaceName => Volatile.Read(ref _workspaceName);

    /// <summary>Whose work this machine runs now: the service's settings for it (the original issue) with the owner's environment variables on top.</summary>
    public RunnerPolicy Policy => _acceptance.Policy;

    /// <summary>
    /// Reads this machine's work policy from the service (the original issue) and applies it; done when the runner starts and each
    /// time its connection opens. False when the service didn't say (it is older, or couldn't be reached): the
    /// policy in force stays.
    /// </summary>
    public async Task<bool> RefreshPolicyAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await _service.PolicyAsync(cancellationToken).ConfigureAwait(false) is not { } policy)
            {
                return false;
            }
            ApplyPolicy(policy);
            return true;
        }
        catch (MessageBus.Cli.Server.WorkspaceSuspendedException)
        {
            _suspensionRecoveryNeeded = true;
            return false; // The heartbeat reports suspension and reinstatement once.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
        {
            await _log.WriteLineAsync($"Couldn't read this machine's work policy from the service: {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Applies the machine's work policy from the service (the original issue); each <see cref="RunnerEnvVars"/> setting that is set still wins.</summary>
    public void ApplyPolicy(RunnerWorkPolicy policy)
    {
        var firstReleasePolicy = !_releasePolicyLoaded;
        _releasePolicyLoaded = true;
        var previousReleases = Volatile.Read(ref _releases);
        Volatile.Write(ref _releases, policy.Releases);
        if ((firstReleasePolicy || previousReleases != policy.Releases) && _ext.SessionUpdater is not null)
        {
            UpdateRequest? waiting;
            lock (_gate) { waiting = _savedUpdate ?? _update; _savedUpdate = null; }
            if (waiting is not null) ResumeSavedUpdate(waiting);
        }
        if (policy.WorkspaceName is { Length: > 0 } workspaceName)
        {
            Volatile.Write(ref _workspaceName, workspaceName);
            _ext.WorkspaceNamed?.Invoke(workspaceName);
        }
        var fromService = new RunnerPolicy
        {
            OwnerSubjectId = string.IsNullOrEmpty(policy.OwnerSubjectId) ? null : policy.OwnerSubjectId,
            AcceptsWorkFrom = policy.AcceptsWorkFrom ?? new AcceptsWorkFrom { Mode = AcceptsWorkFromMode.Workspace },
            GroupMemberSubjectIds = new HashSet<string>(policy.GroupMemberSubjectIds ?? [], StringComparer.Ordinal),
            DailySpendCeilingUsd = policy.DailySpendCeilingUsd,
            RepoCredentialMode = policy.RepoCredentialMode,
        };
        RunnerPolicy effective;
        try
        {
            effective = RunnerEnvironment.WithOverrides(fromService, _host.GetEnvironmentVariable);
        }
        catch (FormatException)
        {
            // The runner checked these variables when it started; a bad one now is ignored rather than stopping it.
            effective = fromService;
        }
        var before = _acceptance.Policy;
        _acceptance.Policy = effective;
        if (before.AcceptsWorkFrom.Mode != effective.AcceptsWorkFrom.Mode || before.DailySpendCeilingUsd != effective.DailySpendCeilingUsd
            || before.OwnerSubjectId != effective.OwnerSubjectId || !before.GroupMemberSubjectIds.SetEquals(effective.GroupMemberSubjectIds)
            || before.RepoCredentialMode != effective.RepoCredentialMode)
        {
            var overrides = RunnerEnvironment.PolicyOverrides(_host.GetEnvironmentVariable);
            _log.WriteLine($"Runs work for {WorkAcceptance.Describe(effective.AcceptsWorkFrom.Mode)}"
                + (effective.DailySpendCeilingUsd is { } usd ? $", up to ${usd.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} a day for other people's" : "")
                + (effective.RepoCredentialMode == RepoCredentialMode.OwnerSession ? "; its owner's own projects use the owner's git sign-in" : "")
                + (overrides.Count > 0 ? $" (set on this machine by {string.Join(", ", overrides)})." : "."));
        }
    }

    /// <summary>Reconciles a saved wait with the release policy loaded for this connection.</summary>
    public void ResumeSavedUpdate(UpdateRequest waiting)
    {
        var releases = Volatile.Read(ref _releases);
        lock (_gate)
            if (!_releasePolicyLoaded) { _savedUpdate = waiting; return; }
        RequestUpdate(waiting with { Version = releases?.PinnedVersion ?? RunnerUpdates.Latest,
            Pinned = releases?.PinnedVersion is not null, Channel = releases?.Channel ?? "stable", Mirror = releases?.Mirror });
    }

    /// <summary>The dashboard's "Connect" for one agent (the original issue); asks on this machine first, never changes anything silently.</summary>
    public Task<ConnectAgentResult> ConnectAgentAsync(ConnectAgentRequest request, CancellationToken cancellationToken) =>
        _ext.Connect?.Invoke(request, cancellationToken)
        ?? Task.FromResult(new ConnectAgentResult
        {
            Status = ConnectAgentStatus.NeedsTerminal,
            Message = "This runner can't ask anyone on its machine. Run mbus connect there.",
        });

    /// <summary>The runner's opt-in Git setup state.</summary>
    public ToolSetupReport ToolSetupStatus => _ext.ToolSetup?.Status
        ?? new() { State = "disabled", Detail = "This runner doesn't support owner-approved Git setup. Update mbus and opt in on the machine." };

    /// <summary>The setup state the dashboard and terminal show: says Git is installed when it already works.</summary>
    public Task<ToolSetupReport> ToolSetupCurrentAsync(CancellationToken cancellationToken) =>
        _ext.ToolSetup?.CurrentAsync(cancellationToken) ?? Task.FromResult(ToolSetupStatus);

    /// <summary>Starts the same local setup flow as the terminal command; a refusal on a machine that has Git says so instead.</summary>
    public async Task<ToolSetupReport> StartToolSetupAsync(CancellationToken cancellationToken) =>
        _ext.ToolSetup?.Start() is { } started
            ? started.State == "disabled" ? await ToolSetupCurrentAsync(cancellationToken).ConfigureAwait(false) : started
            : ToolSetupStatus;

    /// <summary>Atomically reserves an idle runner so no task can start during installation.</summary>
    internal IDisposable? TryPauseForToolSetup()
    {
        lock (_gate)
        {
            if (_runs.Count != 0 || IsDraining || _settingUp || _ext.ToolSetup?.BlocksWork == true || _stopping.IsCancellationRequested) return null;
            _settingUp = true;
            return new SetupPause(this);
        }
    }

    private sealed class SetupPause(RunnerEngine engine) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (engine._gate) { engine._settingUp = false; engine.CheckUpdateReady(); }
        }
    }

    /// <summary>What the runner is doing.</summary>
    public RunnerStatus GetStatus()
    {
        lock (_gate)
        {
            return new RunnerStatus
            {
                Version = RunnerEnvironment.Version,
                Slots = _slots,
                ActiveDeliveryIds = [.. _runs.Keys],
                Draining = IsDraining || _settingUp || _ext.ToolSetup?.BlocksWork == true,
                SupportsManagedDrain = true,
                ManagedDrainId = _managedDrainId,
                SlotCeiling = _ceiling,
                SlotMachineMax = _machineMax,
            };
        }
    }

    /// <summary>Stops every run (they return to the queue) and waits for them to settle.</summary>
    public async Task StopAsync()
    {
        Task[] running;
        lock (_gate)
        {
            _draining = true;
            _sessionTimer?.Dispose();
            _sessionTimer = null;
            StopAll(StopReason.Shutdown, "The runner on this machine stopped.");
            running = [.. _runs.Values.Select(r => (Task)r.Completion)];
        }
        // Settles still being retried get one last try instead of waiting out their backoff.
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_ext.ToolSetup is { } setup) await setup.StopAsync().ConfigureAwait(false);
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _heartbeat?.Dispose();
        _drainTimer?.Dispose();
        _sessionTimer?.Dispose();
    }

    private (IAgentAdapter Adapter, string Executable)? ResolveAgent(string agentId)
    {
        if (!_host.AgentIds.TryGetValue(agentId, out var kind) && !_registryAgentIds.TryGetValue(agentId, out kind)
            && !AgentKinds.TryParse(agentId, out kind))
        {
            return null;
        }
        var adapter = _ext.Adapters is { } custom ? custom(kind) : DefaultAdapter(kind);
        if (adapter is null)
        {
            return null;
        }
        var command = _options.AgentCommands.TryGetValue(kind, out var configured) ? configured : AgentKinds.Command(kind);
        return _host.LocateCommand(command) is { } path ? (adapter, path) : null;
    }

    private IAgentAdapter? DefaultAdapter(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => new ClaudeCodeAdapter(settingsFile: ClaudeCodeAdapter.SettingsFile(_host.GetEnvironmentVariable, _host.HomeDirectory)),
        AgentKind.Codex => new CodexAdapter(CodexAdapter.ReadConfiguredModel(CodexHome), Connect.CodexTomlConfig.HasMbus(Path.Combine(CodexHome, "config.toml")), codexHome: CodexHome, getEnvironmentVariable: _host.GetEnvironmentVariable, sandboxOverride: _options.CodexSandbox),
        AgentKind.Copilot => new CopilotAdapter(copilotHome: EnvOr("COPILOT_HOME", Path.Combine(_host.HomeDirectory, ".copilot"))),
        AgentKind.OpenCode => OpenCodeAdapter.ForRunner(_agentVersions.GetValueOrDefault(AgentKind.OpenCode), _host.GetEnvironmentVariable, _host.HomeDirectory,
            storedEnvironment: _host.AgentEnvironment),
        _ => null,
    };

    private string EnvOr(string name, string fallback) => _host.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    private string CodexHome =>
        _host.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codexHome ? codexHome : Path.Combine(_host.HomeDirectory, ".codex");

    private string ClaudeHome =>
        _host.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } claudeHome ? claudeHome : Path.Combine(_host.HomeDirectory, ".claude");

    /// <summary>
    /// What a message to a session inbox (the original issue) does here (called under the gate): resume that session when this machine
    /// recorded it, the session is closed, it belongs to this machine's owner and the same kind of agent can pick it up from
    /// its log in its folder; settle at once when the session already read it; wait while the session is open or already
    /// being resumed (never two runs of one session); refuse another person's; otherwise a new session, as for any message.
    /// </summary>
    private (DeliveryDecision? Decline, MessageBus.Mcp.SessionRecord? Resume, string? Handled) PlanSession(TaskDelivery delivery, string inboxId, IAgentAdapter adapter)
    {
        var owner = _acceptance.Policy.OwnerSubjectId;
        if (delivery.InboxOwner is { } inboxOwner && owner is not null && inboxOwner != owner)
        {
            return (Decline(DeclineReason.NotAccepted, "This message is for another person's session; this machine resumes only its owner's."), null, null);
        }
        if (_ext.Sessions?.Find(inboxId) is not { } record || record.WorkspaceId != delivery.Envelope.WorkspaceId
            || (record.AgentId is not null && record.AgentId != delivery.AgentId))
        {
            return (null, null, null);
        }
        if (record.Seen.Contains(delivery.Envelope.Id, StringComparer.Ordinal))
        {
            return (null, null, "The session had already read this message.");
        }
        if (record.ProcessRunning() || (record.RunDeliveryId is { } opener && _runs.ContainsKey(opener))
            || _runs.Values.Any(r => r.Resume is { } other && (other.InboxId == inboxId || (record.SessionId is not null && other.SessionId == record.SessionId))))
        {
            return (Decline(DeclineReason.SessionOpen, "Its session is still open on this machine (or already resumed for another message): it waits until that ends."), null, null);
        }
        var resumable = delivery.InboxOwner is not null && owner is not null && record.SessionId is { } session && adapter.CanResume
            && record.AgentKind == AgentKinds.Name(adapter.Kind) && Directory.Exists(record.Folder)
            && MessageBus.Mcp.AgentSessions.Exists(record.AgentKind, record.Folder, session, ClaudeHome, CodexHome, _time.GetUtcNow());
        return (null, resumable ? record : null, null);
    }

    /// <summary>
    /// The profile the runner enforces. A message to a session inbox wakes a closed orchestrator
    /// session: it always gets the orchestrator profile (mbus tools and reading only), whatever the
    /// delivery says (design S5).
    /// </summary>
    private static (PermissionProfile Profile, bool Restricted) EffectiveProfile(TaskDelivery delivery)
    {
        var profile = delivery.RunEnvironment.PermissionProfile;
        if (delivery.Envelope.Target is InboxTarget || profile.Name == PermissionProfiles.Orchestrator)
        {
            return (new PermissionProfile
            {
                Name = PermissionProfiles.Orchestrator,
                CanEditFiles = false,
                CanRunCommands = false,
                CanPush = false,
                Network = NetworkAccess.None,
            }, true);
        }
        return (profile, false);
    }

    private void EnsureHeartbeat(TimeSpan period)
    {
        if (_heartbeat is null)
        {
            _heartbeatPeriod = period;
            _heartbeat = _time.CreateTimer(_ => Heartbeat(), null, period, period);
        }
        else if (period < _heartbeatPeriod)
        {
            _heartbeatPeriod = period;
            _heartbeat.Change(period, period);
        }
    }

    private string? _suspensionReference;
    private volatile bool _suspensionRecoveryNeeded;
    private int _recoveringSuspension;

    private void RecoverAfterSuspension()
    {
        if (Interlocked.Exchange(ref _recoveringSuspension, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await RescanAsync(CancellationToken.None).ConfigureAwait(false);
                if (await RefreshPolicyAsync(CancellationToken.None).ConfigureAwait(false))
                    _suspensionRecoveryNeeded = false;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                await _log.WriteLineAsync($"Couldn't refresh this machine after reinstatement; retrying on the next heartbeat: {ex.Message}").ConfigureAwait(false);
            }
            finally { Volatile.Write(ref _recoveringSuspension, 0); }
        });
    }

    private void Heartbeat() => _ = Task.Run(HeartbeatAsync);

    internal async Task HeartbeatAsync()
    {
        if (Interlocked.Exchange(ref _beating, 1) == 1)
        {
            return;
        }
        try
        {
            RunnerHeartbeat beat;
            lock (_gate)
            {
                // A local acknowledgement may have cleared a persistent setup pause while idle.
                CheckUpdateReady();
                // Sent when idle too (no deliveries, no busy slots).
                beat = new RunnerHeartbeat
                {
                    ActiveDeliveryIds = [.. _runs.Keys],
                    Slots = _slots,
                    SlotsBusy = _runs.Count,
                    Draining = IsDraining || _settingUp || _ext.ToolSetup?.BlocksWork == true,
                    SessionId = SessionId,
                };
            }
            var sentAt = _time.GetUtcNow();
            var ack = await _service.HeartbeatAsync(beat, CancellationToken.None).ConfigureAwait(false);
            if (ack.Suspension?.Reference != _suspensionReference)
            {
                await _log.WriteLineAsync(ack.Suspension is { } suspended
                    ? MessageBus.Cli.Server.SuspensionRefusal.Format(suspended.Message, suspended.Reference, suspended.SupportEmail)
                    : $"Workspace reinstated ({_suspensionReference}); waiting for tasks.").ConfigureAwait(false);
                _suspensionRecoveryNeeded = true;
                _suspensionReference = ack.Suspension?.Reference;
            }
            if (ack.Suspension is null && _suspensionRecoveryNeeded) RecoverAfterSuspension();
            if (ack.Suspension is { StopRunningTasks: true } stopStatus)
            {
                AgentRun[] stoppedRuns;
                lock (_gate) stoppedRuns = [.. _runs.Values];
                foreach (var stoppedRun in stoppedRuns) stoppedRun.StopForSuspension(stopStatus.Reference);
            }
            if (ack.RunnerConnected == false && beat.SessionId is not null)
            {
                try
                {
                    ServiceLacksConnection?.Invoke(sentAt);
                }
                catch (Exception ex)
                {
                    await _log.WriteLineAsync($"Reconnecting after a heartbeat failed: {ex.Message}").ConfigureAwait(false);
                }
            }
            foreach (var lease in ack.Leases)
            {
                AgentRun? run;
                lock (_gate)
                {
                    _runs.TryGetValue(lease.DeliveryId, out run);
                }
                if (run is null)
                {
                    continue;
                }
                if (ack.Suspension is { StopRunningTasks: true } stopped)
                {
                    run.StopForSuspension(stopped.Reference);
                }
                else if (lease.CancelRequested)
                {
                    run.Stop(StopReason.Cancelled, "Cancelled.");
                }
                else if (lease.LeaseExpiresAt is { } until)
                {
                    if (ack.Suspension is { } capped && until <= _time.GetUtcNow()) run.StopForSuspension(capped.Reference);
                    else run.LeaseExpiresAt = until;
                }
                else
                {
                    if (ack.Suspension is { } capped) run.StopForSuspension(capped.Reference);
                    else run.Stop(StopReason.LeaseLost, "The service no longer holds this task for this machine.");
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            await _log.WriteLineAsync($"Heartbeat failed: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _beating, 0);
        }
    }

    private void StopAll(StopReason reason, string detail)
    {
        lock (_gate)
        {
            foreach (var run in _runs.Values)
            {
                run.Stop(reason, detail);
            }
        }
    }

    private void Finished(AgentRun run, Task<SettleResult> completion)
    {
        if (completion.IsCompletedSuccessfully)
        {
            _acceptance.Record(run.Delivery, completion.Result.Usage?.EstimatedCostUsd, _time.GetUtcNow());
        }
        else if (completion.Exception is { } ex)
        {
            _log.WriteLine($"{run.DeliveryId}: the run failed inside the runner: {ex.GetBaseException().Message}");
        }
        lock (_gate)
        {
            _runs.Remove(run.DeliveryId);
            _ext.WorkActivityChanged?.Invoke(_runs.Count > 0);
            CheckUpdateReady();
        }
    }

    /// <summary>Rejects a completed drain superseded by another request.</summary>
    public bool IsCurrentUpdate(UpdateRequest request) { lock (_gate) return _update == request; }

    /// <summary>The transport confirmed this version connected.</summary>
    public void ConfirmUpdateConnected() => _ext.SessionUpdater?.ConfirmConnected();

    /// <summary>A holder appeared during the drain; enable work and repeat the final check.</summary>
    public void ResumeSessionWait(UpdateRequest request)
    {
        lock (_gate)
        {
            if (_update != request) return;
            _updateReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _holderCount = 0;
            _draining = _userDraining || _offlineForUpdate;
            // The final check and the ordinary scan can disagree. An empty scan waits for the timer.
            var holders = _ext.SessionUpdater?.Holders();
            if (holders is { Count: > 0 }) ApplySessionScan(request, holders);
            _waitingForSessions = true;
            _sessionTimer ??= _time.CreateTimer(_ => ScanSessionTimer(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
    }

    private bool ScanSessions()
    {
        if (_update is not { } request || _ext.SessionUpdater is not { } tool || _stopping.IsCancellationRequested) return false;
        return ApplySessionScan(request, tool.Holders());
    }

    private void ScanSessionTimer()
    {
        try
        {
            UpdateRequest? request;
            lock (_gate) request = _update;
            if (request is null || _ext.SessionUpdater is not { } tool) return;
            var holders = tool.Holders(); // Read the process table outside the engine gate.
            lock (_gate)
                if (_update == request && !_stopping.IsCancellationRequested) ApplySessionScan(request, holders);
        }
        catch (Exception ex) { _log.WriteLine("Could not check install holders: " + ex.Message); }
    }

    private bool ApplySessionScan(UpdateRequest request, IReadOnlyList<ToolInstallHolder> holders)
    {
        if (holders.Count == 0)
        {
            _waitingForSessions = false;
            _sessionTimer?.Dispose();
            _sessionTimer = null;
            if (_holderCount >= 0)
            {
                _draining = true;
                ReportUpdate(Contracts.Releases.RunnerUpdateStage.Waiting, request.Version);
                CheckUpdateReady();
            }
            return false;
        }
        _waitingForSessions = true;
        if (holders.Count != _holderCount)
        {
            _holderCount = holders.Count;
            _log.WriteLine($"Waiting for {holders.Count} install holders: " + string.Join(", ", holders.Select(h => $"{h.Pid} {h.Name}")));
            ReportUpdate(Contracts.Releases.RunnerUpdateStage.WaitingForSessions, request.Version, blockedBy: holders.Count);
        }
        // Only the local process table is rescanned: there is no process-created event across platforms.
        // No API reads, and the timer stops as soon as this wait ends or the runner stops.
        _sessionTimer ??= _time.CreateTimer(_ => ScanSessionTimer(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        return true;
    }

    private void CheckUpdateReady()
    {
        if (!_waitingForSessions && _update is { } update && _runs.Count == 0 && !_settingUp && _ext.ToolSetup?.BlocksWork != true)
        {
            _updateReady.TrySetResult(update);
        }
    }

    private static DeliveryDecision Decline(DeclineReason reason, string detail) =>
        new() { Accepted = false, Reason = reason, Detail = detail };
}
