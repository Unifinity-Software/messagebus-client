using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>Why a run was stopped before its agent finished.</summary>
public enum StopReason
{
    /// <summary>Not stopped.</summary>
    None,
    /// <summary>A cancel request (from the sender, through the service).</summary>
    Cancelled,
    /// <summary>The role's time limit passed.</summary>
    TimedOut,
    /// <summary>The service says the lease is gone (another machine may have the task now).</summary>
    LeaseLost,
    /// <summary>The runner is draining and the drain deadline passed.</summary>
    Drained,
    /// <summary>The runner is stopping.</summary>
    Shutdown,
    /// <summary>Its estimated cost passed the role's most cost per run (the original issue).</summary>
    CostCapReached,
}

/// <summary>Everything a run needs from the engine.</summary>
internal sealed record RunServices(
    RunnerOptions Options,
    IRunnerHostContext Host,
    IRunnerServiceClient Service,
    TimeProvider Time,
    TextWriter Log,
    IRunSandbox Sandbox,
    IBundleSource? Bundles,
    CancellationToken Stopping = default,
    IRunTools? Tools = null,
    RunApprovalPrompt? RunApprovals = null,
    Func<RunWorkspace, TaskDelivery, CancellationToken, Task<bool>>? SendKeptAnswer = null,
    Func<RunWorkspace, TaskDelivery, string, string?, CancellationToken, Task<bool>>? ReturnCommits = null,
    NodeVerification? Verification = null);

/// <summary>
/// One delivery, from worktree to settle: check out, run the setup command, start the agent with its
/// prompt, stream progress, stop it on cancel or timeout (the whole process tree), work out the
/// result from the exit code and the agent's own <c>settle</c> call, settle, clean up.
/// </summary>
internal sealed class AgentRun
{
    private readonly RunServices _s;
    private IAgentAdapter _adapter;
    private readonly string _executable;
    private readonly PermissionProfile _profile;
    private readonly bool _restricted;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _timeout;
    private readonly object _parserGate = new();
    private DeclineReason? _declined;
    private bool _costUnknownNoted;
    private int _reason;
    private string? _stopDetail;
    private DateTimeOffset? _lastAgentProgress;
    private DateTimeOffset _lastReported;
    private string? _lastActivity;
    private Task _capacityChain = Task.CompletedTask;
    private CapacityLimitHit? _capacityHit;
    private bool _capacityErrorLogged;
    private Task _progressChain = Task.CompletedTask;
    private System.Diagnostics.Activity? _span;
    private RunToolsSession? _tools;
    private string? _lastRefusal;
    private readonly string? _alreadyHandled;
    private SettleInput? _verifiedAgentSettle;
    private VerificationResult? _verification;
    private int _resourceRoot;

    // Zero means no live process; -1 means the protected task root is outside the runner's view.
    internal int ResourceRoot => Volatile.Read(ref _resourceRoot);

    private async Task<int> WaitForOwnedProcessAsync(RunWorkspace workspace, AgentProcess process)
    {
        Volatile.Write(ref _resourceRoot, _s.Sandbox.UsesPrivateHome ? -1 : process.Id);
        try
        {
            _s.Sandbox.Started(workspace, process.Id);
            using var stop = _stop.Token.Register(process.Kill);
            return await process.WaitForExitAsync(ClearResourceRoot).ConfigureAwait(false);
        }
        finally
        {
            ClearResourceRoot();
        }
    }

    private void ClearResourceRoot() => Volatile.Write(ref _resourceRoot, 0);

    /// <param name="services">What the engine gives every run.</param>
    /// <param name="delivery">The task.</param>
    /// <param name="adapter">The agent CLI.</param>
    /// <param name="executable">Its command.</param>
    /// <param name="profile">What the run may do.</param>
    /// <param name="restricted">The orchestrator profile, enforced by the runner.</param>
    /// <param name="resume">the original issue: the closed session to resume in its own folder, instead of a new session in a worktree.</param>
    /// <param name="proposalAuthority">Trusted origin verified by the runner for this exact session.</param>
    /// <param name="alreadyHandled">the original issue: nothing to run (the session already read this reply); settles as succeeded with this summary.</param>
    public AgentRun(RunServices services, TaskDelivery delivery, IAgentAdapter adapter, string executable, PermissionProfile profile, bool restricted,
        MessageBus.Mcp.SessionRecord? resume = null, string? alreadyHandled = null, MessageBus.Mcp.ProposalAuthority proposalAuthority = MessageBus.Mcp.ProposalAuthority.Worker)
    {
        _s = services;
        Delivery = delivery;
        _adapter = adapter;
        _executable = executable;
        _profile = profile;
        _restricted = restricted;
        Resume = resume;
        ProposalAuthority = proposalAuthority;
        _alreadyHandled = alreadyHandled;
        LeaseExpiresAt = delivery.LeaseExpiresAt;
        // Started now, on the caller's thread, so the clock starts when the delivery is accepted; for a run that needs its
        // machine owner's approval (the original issue), once it is approved, so the wait doesn't use up the role's time limit.
        if (!delivery.RequireRunApproval)
        {
            StartClock();
        }
    }

    /// <summary>Starts the role's time limit.</summary>
    private void StartClock()
    {
        var limit = TimeSpan.FromSeconds(Math.Max(1, Delivery.RunEnvironment.TimeoutSeconds));
        _timeout = new CancellationTokenSource(limit, _s.Time);
        _timeout.Token.Register(() => Stop(StopReason.TimedOut, $"Stopped after {(int)limit.TotalSeconds} seconds, the role's time limit."));
    }

    private MessageBus.Mcp.ProposalAuthority ProposalAuthority { get; }

    public TaskDelivery Delivery { get; }

    /// <summary>The session this run resumes (the original issue), or null.</summary>
    public MessageBus.Mcp.SessionRecord? Resume { get; }

    public string DeliveryId => Delivery.DeliveryId;

    /// <summary>The coding agent running this task, for the runner's update waiting log.</summary>
    public AgentKind AgentKind => _adapter.Kind;

    public DateTimeOffset LeaseExpiresAt { get; set; }

    public Task<SettleResult> Completion { get; private set; } = null!;

    public StopReason Reason => (StopReason)Volatile.Read(ref _reason);

    public void Start() => Completion = Task.Run(RunAsync);

    /// <summary>Stops the run (first reason wins). Its process tree is killed and it settles accordingly.</summary>
    public void Stop(StopReason reason, string detail)
    {
        if (Interlocked.CompareExchange(ref _reason, (int)reason, (int)StopReason.None) == (int)StopReason.None)
        {
            _stopDetail = detail;
            try
            {
                _stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task<SettleResult> RunAsync()
    {
        var started = _s.Time.GetTimestamp();
        // The run's span (the original issue), continuing the delivery's trace; progress and settle calls are made under it.
        using var span = _span = Telemetry.RunnerTelemetry.StartRun(Delivery, _s.Host.MachineId, _s.Host.MachineName, _adapter.Kind);
        if (_alreadyHandled is { } handled)
        {
            var done = new SettleResult { Status = SettleStatus.Succeeded, Summary = handled };
            Telemetry.RunnerTelemetry.Finish(span, Delivery, _adapter.Kind, done, _s.Time.GetElapsedTime(started));
            await SettleAsync(done).ConfigureAwait(false);
            _timeout?.Dispose();
            return done;
        }
        var snapshot = Delivery.Verification;
        var verifier = snapshot is null ? null : _s.Verification;
        if (snapshot is not null)
            _verification = NodeVerification.Blocked(snapshot, "check-unavailable");

        RunWorkspace? workspace = null;
        RunWorkspace? verificationWorkspace = null;
        RunCommits? commits = null;
        AgentOutcome? outcome = null;
        SettleInput? agentSettle = null;
        IReadOnlyList<string> tail = [];
        int? exitCode = null;
        string? sandboxSettleNote = null;
        (SettleStatus Status, string Error)? failure = null;

        try
        {
            var kept = Delivery.RunEnvironment.KeepAgentTools ?? [];
            var names = string.Join(", ", kept);
            var cannotKeep = kept.Count == 0 ? null
                : _s.Options.AgentTools == "refuse"
                    ? $"This computer's owner doesn't let runs keep extra agent tools, and this role's runs keep {names}. Run this role on another computer, or ask the owner to allow it (MBUS_RUNNER_AGENT_TOOLS)."
                : _adapter.Kind switch
                {
                    AgentKind.Codex => (_adapter as CodexAdapter)?.CannotKeepTools(kept),
                    AgentKind.ClaudeCode => (_adapter as ClaudeCodeAdapter)?.CannotKeepTools(kept, Resume?.Folder), // the original issue
                    _ => $"This role's runs keep {names}, which works with Codex and Claude Code only for now. Make the role run on one of them.",
                };
            if (cannotKeep is not null)
            {
                failure = (SettleStatus.Failed, cannotKeep);
            }
            else if (Delivery.RequireRunApproval && await WaitForApprovalAsync().ConfigureAwait(false) is { } declined)
            {
                // the original issue: not run here; another machine may take it. Nothing was checked out.
                _declined = DeclineReason.NotApproved;
                failure = (SettleStatus.Abandoned, declined);
            }
            else
            {
                if (_adapter is OpenCodeAdapter openCode)
                {
                    var selected = await openCode.SelectAsync(Delivery.Model, _stop.Token).ConfigureAwait(false);
                    _adapter = selected.Adapter;
                    if (selected.Error is { } error)
                    {
                        _declined = DeclineReason.MachineNotReady;
                        failure = (SettleStatus.Abandoned, error);
                    }
                }
                if (failure is null) await PrepareAndRunAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Every run settles, whatever went wrong on this machine (missing executable, disk full).
            failure ??= (SettleStatus.Abandoned, verifier is null
                ? $"Couldn't run the task here: {ex.Message}"
                : "The required verification checkout or agent setup could not complete.");
        }
        finally
        {
            await EndToolsAndSandboxAsync(workspace).ConfigureAwait(false);
        }
        if (verifier is not null)
            agentSettle = Volatile.Read(ref _verifiedAgentSettle);

        async Task PrepareAndRunAsync()
        {
            // the original issue: a resumed session works in its own folder; the run's directory holds only the run's files.
            workspace = RunWorkspace.Create(_s.Options.WorkRoot, DeliveryId, Delivery.Attempt, Resume?.Folder);
            workspace.LocalModelEndpoint = (_adapter as OpenCodeAdapter)?.LocalEndpoint;
            await File.WriteAllTextAsync(workspace.DeliveryFile, MessageBusJson.Serialize(Delivery)).ConfigureAwait(false);
            var prompt = Resume is null ? TaskPrompt.Build(Delivery, _s.Host.MachineName, _restricted) : TaskPrompt.BuildResume(Delivery, _s.Host.MachineName);
            if (verifier is not null && !NodeVerification.IsReview(Delivery))
                prompt += $"\n\nFor required verification, commit your result on the current {NodeVerification.Branch} branch. "
                    + "Keep the approved package manifest, lockfile and Git attributes unchanged. Settle with your reply. The runner returns the checked commit.";
            await File.WriteAllTextAsync(workspace.PromptFile, prompt).ConfigureAwait(false);

            await _s.Sandbox.PrepareAsync(workspace, Delivery, _profile, _stop.Token).ConfigureAwait(false);
            var sandboxed = workspace;
            ProcessLaunch Sandboxed(ProcessLaunch launch) => _s.Sandbox.Wrap(launch, sandboxed, _profile);

            if (verifier is not null)
            {
                Note("Preparing the pinned checkout for isolated verification");
                verificationWorkspace = RunWorkspace.Create(_s.Options.WorkRoot, DeliveryId + "-verification", Delivery.Attempt);
                if (await verifier.PrepareAsync(workspace.WorkTree, Delivery, verificationWorkspace, _stop.Token)
                    .ConfigureAwait(false) is { } blocked)
                {
                    _verification = NodeVerification.Blocked(snapshot!, blocked);
                    failure = (SettleStatus.Failed, blocked);
                    return;
                }
            }
            else if (Resume is not null)
            {
                Note($"Resuming the session on {_s.Host.MachineName}");
            }
            else
            {
                Note($"Preparing the worktree on {_s.Host.MachineName}");
                // the original issue: a bundle that starts after commits this machine lacks: the run asks the sender's mbus for the rest, in its progress.
                var asked = false;
                Task AskSender(ProgressRequest request, CancellationToken ct)
                {
                    asked = true;
                    return Report("Waiting for the code from the sender: this machine has no copy of the repository's earlier history", request);
                }
                workspace.GitLog = message =>
                {
                    _s.Log.WriteLine($"{DeliveryId}: {message}");
                    Note(message);
                };
                if (await workspace.CheckOutAsync(Delivery, _s.Host.LocateCommand, _s.Bundles, _stop.Token, Sandboxed, AskSender, _s.Options.HistoryWait)
                        .ConfigureAwait(false) is { } checkoutError)
                {
                    // Often the network or a credential; another attempt (maybe elsewhere) may work. Earlier history that never
                    // came is the sender's to fix (its session closed): another attempt wouldn't get it either.
                    failure = (asked ? SettleStatus.Failed : SettleStatus.Abandoned, checkoutError);
                }
                else if (asked)
                {
                    Note("Received the code from the sender");
                }
            }

            if (verifier is null)
            {
                commits = new RunCommits(_s.Host.LocateCommand("git"), workspace);
                await commits.CaptureAsync().ConfigureAwait(false);
            }

            if (failure is null && _s.Tools is { } tools)
            {
                // the original issue: the agent's mbus tools are served from here, with a credential for this run only.
                _tools = await tools.StartAsync(new RunToolsRequest(workspace, Delivery, _profile, Sandboxed)
                {
                    Session = Resume,
                    ProposalAuthority = ProposalAuthority,
                    RecordVerifiedTaskSettle = verifier is null ? null : input => Volatile.Write(ref _verifiedAgentSettle, input),
                    Refused = (name, error) =>
                    {
                        if (name == McpToolNames.Settle) Volatile.Write(ref _lastRefusal, error.Message);
                    },
                }, _stop.Token).ConfigureAwait(false);
            }
            var environment = RunEnvironmentVariables(workspace);
            if (failure is null && verifier is null && Resume is null && Delivery.RunEnvironment.SetupCommand is { Length: > 0 } setup)
            {
                Note("Running the setup command");
                using var shell = AgentProcess.Start(Sandboxed(AgentProcess.Shell(setup, workspace.WorkTree, environment)), tailLines: 20);
                var setupExit = await WaitForOwnedProcessAsync(workspace, shell).ConfigureAwait(false);
                if (setupExit != 0 && !_stop.IsCancellationRequested)
                {
                    failure = (SettleStatus.Failed, $"The setup command failed (exit {setupExit}): {string.Join(" ", shell.Tail().TakeLast(5)).Trim()}");
                }
            }

            if (failure is null && !_stop.IsCancellationRequested)
            {
                // the original issue: a role that may run the repository's tests but no other commands gets those test commands.
                var profile = TestCommands.Apply(_profile, workspace.WorkTree);
                string? modeNote = null;
                string? cannotStart = null;
                var process = AgentProcessLaunch(_adapter, _s.Sandbox, _executable, workspace, _profile,
                    new AgentLaunchContext(prompt, workspace.PromptFile, profile, workspace.WorkTree, McpEnvironment(environment), Resume?.SessionId, Delivery.Model,
                        CodexOnWindows: Delivery.RunEnvironment.CodexOnWindows, KeepAgentTools: Delivery.RunEnvironment.KeepAgentTools ?? []),
                    AgentEnvironment(environment, _adapter, new SignInEnvironment(_s.Host.GetEnvironmentVariable, _s.Host.AgentEnvironment()),
                        windowsService: _s.Options.WindowsService), launch =>
                    {
                        cannotStart = launch.CannotStart;
                        sandboxSettleNote = launch.SettleNote;
                        modeNote = launch.Note;
                    });
                if (cannotStart is not null)
                {
                    failure = (SettleStatus.Failed, cannotStart);
                    return;
                }
                if (modeNote is { } note)
                {
                    Note(note);
                    await _s.Log.WriteLineAsync($"{DeliveryId}: {note}").ConfigureAwait(false);
                }
                if (process.StandardInput is { } launchPrompt && launchPrompt != prompt)
                    await File.WriteAllTextAsync(workspace.PromptFile, launchPrompt).ConfigureAwait(false);

                var parser = _adapter.Kind == AgentKind.ClaudeCode && Resume?.SessionId is { } sessionId
                    ? await ClaudeOutputParser.ForResumeAsync(MessageBus.Mcp.SessionUsage.ClaudeHome(name =>
                        process.Environment.TryGetValue(name, out var value) ? value : _s.Host.GetEnvironmentVariable(name)),
                        workspace.WorkTree, sessionId, _stop.Token).ConfigureAwait(false)
                    : _adapter.CreateParser(Delivery.Model);
                Note($"Started {AgentKinds.DisplayName(_adapter.Kind)}"
                    + (Delivery.Model is not { } model ? ""
                        : Delivery.EconomyModel ? $" with {model}, the role's cheaper model (its daily budget is used)"
                        : $" with {model}"));
                if (Delivery.CostCap is { } cap && !_adapter.ReportsUsageAsItGoes)
                {
                    // the original issue, said plainly: this CLI's usage arrives only at the end, so the cap can't stop it early.
                    Note($"{AgentKinds.DisplayName(_adapter.Kind)} reports what it used only when it finishes, so the role's "
                        + $"{Money(cap.MaxUsd)} most per run can't stop this run early");
                }
                // A long step (a build, a test run, a slow model turn) prints nothing for minutes: say it is still going (the original issue).
                var agentStarted = _s.Time.GetUtcNow();
                using var stillRunning = _s.Time.CreateTimer(_ => StillRunning(agentStarted), null, _s.Options.StillRunningInterval, _s.Options.StillRunningInterval);
                var filterOutput = _adapter.Kind == AgentKind.Codex
                    ? CodexAuthentication.RedactOutput(process.Environment.GetValueOrDefault(CodexAuthentication.CodexKey)) : null;
                using var agent = AgentProcess.Start(process, line => OnOutput(parser, line), _s.Options.LogTailLines, filterOutput);
                exitCode = await WaitForOwnedProcessAsync(workspace, agent).ConfigureAwait(false);
                lock (_parserGate)
                {
                    outcome = parser.Complete();
                    // A run stopped before the CLI's final report (a cost cap, a cancel) still counts what it used.
                    if (parser.UsageSoFar is { } soFar && outcome.Usage is not { TokensIn: > 0 } and not { TokensOut: > 0 } and not { EstimatedCostUsd: not null })
                    {
                        outcome = outcome with { Usage = soFar };
                    }
                }
                QueueCapacity(parser.Capacity);
                await _capacityChain.ConfigureAwait(false);
                tail = agent.Tail();
                if (_adapter.Kind == AgentKind.Codex && (exitCode != 0 || outcome.ReportedError)
                    && CodexAuthentication.Refusal(outcome, tail) is { } refusal)
                {
                    outcome = outcome with { ReportedError = true, ErrorText = refusal };
                }
                if (verifier is null)
                {
                    await SendKeptAnswerAsync(workspace).ConfigureAwait(false);
                    agentSettle = ReadAgentSettle(workspace.SettleFile);
                }
            }
        }

        // From here on nothing may stop the settle: every accepted delivery is settled (the original issue).
        SettleResult result;
        // the original issue: why the run's folder is the only copy of its work, so it is held for a while instead of deleted.
        string? keptBecause;
        try
        {
            var refusal = Volatile.Read(ref _lastRefusal);
            SettleResult CurrentResult() => Compose(Reason, _stopDetail, failure, exitCode, outcome, agentSettle, tail,
                _s.Time.GetElapsedTime(started), workspace?.WorkTree,
                // A message to a session inbox is an answer the session reads; there is nothing for it to settle.
                answerExpected: Delivery.Envelope.Target is not InboxTarget, runnerNote: sandboxSettleNote, refusal: refusal);
            result = CurrentResult();
            if (result.Status is SettleStatus.Failed or SettleStatus.Abandoned && workspace is not null
                && File.Exists(MessageBus.Mcp.RoleKeyReadiness.FileName(workspace.SettleFile)))
            {
                // The task can write this file; only a fresh check by the runner is evidence.
                try
                {
                    var readableRoles = await _s.Host.CheckRoleKeysAsync(_s.Stopping).ConfigureAwait(false);
                    if (Delivery.Role is { } role && readableRoles?.TryGetValue(role, out var readable) == true && !readable)
                        _unreadableRole = role;
                }
                catch (Exception ex)
                {
                    await _s.Log.WriteLineAsync($"Couldn't check role keys: {ex.Message}").ConfigureAwait(false);
                }
            }
            if (_unreadableRole is not null) result = result with { FailureCategory = "unreadable" };
            else if (_declined is not null) result = result with { FailureCategory = "refused" };
            // The agent finished but its answer (and any code it sent back) never reached the service: its
            // work exists only in the worktree.
            keptBecause = IsResultNotReceived(result) ? "the agent's answer didn't reach the runner" : null;
            if (verifier is not null && workspace is not null)
            {
                result = result with
                {
                    Refs = (result.Refs ?? []).Where(reference => reference.Kind != "git.commit" && reference.Kind != "git.branch").ToArray(),
                };
                if (verificationWorkspace is not null && result.Status == SettleStatus.Succeeded && Reason == StopReason.None)
                {
                    Note("Running required checks in a fresh isolated checkout");
                    var returned = await VerifyAndReturnAsync(verifier, workspace, verificationWorkspace).ConfigureAwait(false);
                    if (Reason != StopReason.None && returned is null)
                        result = CurrentResult() with { Refs = result.Refs };
                    if (returned is not null)
                        result = result with { Refs = [.. result.Refs, new Ref { Kind = "git.commit", Sha = returned }] };
                    else if (!NodeVerification.IsReview(Delivery))
                        keptBecause ??= "the checked commit was not returned";
                }
                else if (exitCode is not null && !NodeVerification.IsReview(Delivery))
                {
                    keptBecause ??= "the editing agent did not complete verification";
                }
            }

            commits?.InspectAfterExit();
            if (commits is not null && await commits.MustKeepAsync().ConfigureAwait(false))
            {
                var returned = false;
                if (result.Status == SettleStatus.Succeeded && _s.ReturnCommits is { } send && await commits.BranchAsync().ConfigureAwait(false) is { } branch)
                {
                    try
                    {
                        if (!await commits.HasOtherNewAsync(branch).ConfigureAwait(false))
                            returned = await send(workspace!, Delivery, branch, commits.BaseCommit, _s.Stopping).ConfigureAwait(false);
                        if (returned) await _s.Log.WriteLineAsync($"{DeliveryId}: returned the task branch's commits as a git bundle before cleanup.").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await _s.Log.WriteLineAsync($"{DeliveryId}: couldn't return commits: {ex.Message}").ConfigureAwait(false);
                    }
                }
                if (!returned) keptBecause ??= "commits may not have been returned";
            }
            Telemetry.RunnerTelemetry.Finish(span, Delivery, _adapter.Kind, result, _s.Time.GetElapsedTime(started));
            if (verifier is null && workspace is not null && Delivery.RunEnvironment.Logs != LogCapture.None)
            {
                // Upload (encrypted, to the run-logs container) needs the original issue; until then it is kept locally too.
                await WriteLogAsync(workspace, tail).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            result = new SettleResult { Status = SettleStatus.Abandoned, ExitCode = exitCode, Error = $"The runner failed while finishing the task: {ex.Message}" };
            keptBecause = "the runner failed while finishing the task";
        }

        try
        {
            await _progressChain.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _s.Log.WriteLineAsync($"Progress for {DeliveryId} wasn't delivered: {ex.Message}").ConfigureAwait(false);
        }
        var settled = false;
        try
        {
            settled = await SettleAsync(result).ConfigureAwait(false);
        }
        finally
        {
            if (verificationWorkspace is not null)
            {
                await EndWorkspaceAsync(verificationWorkspace, null).ConfigureAwait(false);
            }
            if (workspace is not null)
            {
                await EndWorkspaceAsync(workspace, settled ? keptBecause : keptBecause ?? "its result didn't reach the service").ConfigureAwait(false);
            }
        }
        _timeout?.Dispose();
        return result;
    }

    private async Task<string?> VerifyAndReturnAsync(NodeVerification verifier, RunWorkspace workspace, RunWorkspace fresh)
    {
        try
        {
            _verification = await verifier.VerifyAsync(Delivery, workspace, fresh, _stop.Token).ConfigureAwait(false);
            if (NodeVerification.IsReview(Delivery) || _verification.Revision is not { } revision) return null;

            fresh.VerifiedReturnRevision = revision;
            if (_s.ReturnCommits is { } send
                && await send(fresh, Delivery, NodeVerification.Branch, Delivery.Verification!.Revision, _stop.Token).ConfigureAwait(false))
                return revision;
            _verification = NodeVerification.Blocked(Delivery.Verification!, "revision-unverified");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _verification = NodeVerification.Blocked(Delivery.Verification!, _stop.IsCancellationRequested
                ? "cancelled" : "check-unavailable");
            await _s.Log.WriteLineAsync($"{DeliveryId}: required verification stopped ({ex.GetType().Name}).").ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// the original issue: the run's folder is deleted (its log stays when the role keeps local logs), succeeded or failed. When it is the
    /// only copy of work that never reached the service it is held for <see cref="RunWorkspace.HoldFor"/>, and housekeeping
    /// deletes it after that; a delete that fails now is retried by housekeeping too.
    /// </summary>
    private async Task EndWorkspaceAsync(RunWorkspace workspace, string? keptBecause)
    {
        try
        {
            if (_s.Options.KeepRunFolders)
            {
                workspace.Dispose();
            }
            else if (keptBecause is not null)
            {
                var until = _s.Time.GetUtcNow() + RunWorkspace.HoldFor;
                workspace.Hold(until);
                workspace.Dispose();
                await _s.Log.WriteLineAsync($"{DeliveryId}: {keptBecause}; the run's files are kept in {workspace.Root} until "
                    + $"{until.ToLocalTime():yyyy-MM-dd HH:mm}, then deleted.").ConfigureAwait(false);
            }
            else if (!workspace.CleanUp(keepLog: Delivery.RunEnvironment.Logs != LogCapture.None))
            {
                await _s.Log.WriteLineAsync($"{DeliveryId}: some of the run's files in {workspace.Root} couldn't be deleted yet; the runner's daily tidy-up tries again.").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            workspace.Dispose();
            await _s.Log.WriteLineAsync($"{DeliveryId}: couldn't finish with the run's files in {workspace.Root}: {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>The run's tools and their credential end with the agent; what it left running is ended before the settle.</summary>
    private async Task EndToolsAndSandboxAsync(RunWorkspace? workspace)
    {
        if (_tools is { } tools)
        {
            // The run's tools and their credential end with the agent.
            try
            {
                await tools.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _s.Log.WriteLineAsync($"{DeliveryId}: ending the run's mbus tools failed: {ex.Message}").ConfigureAwait(false);
            }
        }
        if (workspace is not null)
        {
            // Before the settle: what the run left running is ended and its credential withdrawn.
            try
            {
                _s.Sandbox.Ended(workspace);
            }
            catch (Exception ex)
            {
                await _s.Log.WriteLineAsync($"{DeliveryId}: ending the run's sandbox failed: {ex.Message}").ConfigureAwait(false);
            }
            // the original issue: Claude Code's run settings can hold a kept tool's token; gone with the agent, also from a held folder.
            if (!ClaudeCodeAdapter.DeleteMcpConfig(workspace.Root))
            {
                await _s.Log.WriteLineAsync($"{DeliveryId}: the run's agent tool settings in {workspace.Root} couldn't be deleted yet; they go with the run's folder.").ConfigureAwait(false);
            }
        }
    }

    /// <summary>How a settle starts when the agent ended without its own result reaching the runner (the original issue).</summary>
    internal const string ResultNotReceived = "Agent result not received";

    private static bool IsResultNotReceived(SettleResult result) =>
        result.Error?.StartsWith(ResultNotReceived, StringComparison.Ordinal) == true;

    /// <summary>
    /// The result: stop reasons first, then failures before the agent ran, then the exit code, then
    /// the agent's own <c>settle</c> call. What the agent wrote as text never decides the status: an
    /// agent that exits cleanly without its settle reaching the runner (the call failed, or it never
    /// made one) failed, with <see cref="ResultNotReceived"/> and where its worktree was kept.
    /// </summary>
    internal static SettleResult Compose(
        StopReason reason,
        string? stopDetail,
        (SettleStatus Status, string Error)? failure,
        int? exitCode,
        AgentOutcome? outcome,
        SettleInput? agentSettle,
        IReadOnlyList<string> tail,
        TimeSpan elapsed,
        string? workTree = null,
        bool answerExpected = true,
        string? runnerNote = null,
        string? refusal = null)
    {
        var usage = outcome?.Usage ?? agentSettle?.Result.Usage;
        // With more than one turn the runner's clock also counts waiting (an approval, a slow start), so it can read
        // longer than the agent's active time (the original issue).
        if (usage is not null && (usage.ActiveSeconds <= 0 || outcome?.SpansTurns == true))
        {
            usage = usage with { ActiveSeconds = Math.Round(elapsed.TotalSeconds, 3) };
        }
        var claimed = agentSettle?.Result;
        var summary = claimed?.Summary ?? AgentText.OneLine(outcome?.FinalText, 500);
        var refs = claimed?.Refs ?? [];

        SettleResult Result(SettleStatus status, string? error) => new()
        {
            Status = status,
            Summary = status == SettleStatus.Succeeded && runnerNote is not null
                ? string.IsNullOrWhiteSpace(summary) ? runnerNote : summary + "\n" + runnerNote
                : summary,
            FrictionKinds = (claimed?.FrictionKinds ?? []).Where(FrictionKinds.IsValid).Take(12).ToList(),
            Refs = refs,
            Usage = usage,
            ExitCode = exitCode,
            Error = error,
        };

        switch (reason)
        {
            case StopReason.Cancelled:
                return Result(SettleStatus.Cancelled, stopDetail);
            case StopReason.CostCapReached:
                // Final (the original issue): another attempt would cost as much again.
                return Result(SettleStatus.Failed, stopDetail);
            case StopReason.TimedOut or StopReason.LeaseLost or StopReason.Drained or StopReason.Shutdown:
                return Result(SettleStatus.Abandoned, stopDetail);
        }
        if (failure is { } f)
        {
            return Result(f.Status, f.Error) with { FailureCategory = "not-started" };
        }
        // Never raw output: the agent's own words when it settled (its error, else its summary for a failure), else one
        // readable line from the CLI (its error, else the agent's last message, else the last lines that aren't JSON events).
        // The agent's own words keep their lines and length (within reason); text taken from the CLI becomes one short line.
        var agentsOwn = AgentsOwn(claimed?.Error) ?? (claimed is { Status: not SettleStatus.Succeeded } ? AgentsOwn(claimed.Summary) : null);
        var lastMessage = Readable(outcome?.FinalText);
        var errorText = agentsOwn ?? Readable(outcome?.ErrorText)
            ?? (lastMessage is null ? null : $"Its last message: {lastMessage}")
            ?? LastLines(tail);
        if (exitCode is not 0)
        {
            // The CLI ended with an answer (or the agent settled) but a failing exit code: the work
            // failed. No answer at all: the agent crashed, and another attempt may do better.
            var answered = outcome is { ReportedError: true } || outcome?.FinalText is not null || claimed is not null;
            return Result(answered ? SettleStatus.Failed : SettleStatus.Abandoned,
                $"{(answered ? "The agent" : "The agent stopped unexpectedly and")} exited with code {exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}. {errorText}".Trim());
        }
        if (outcome is { ReportedError: true })
        {
            return Result(SettleStatus.Failed, errorText);
        }
        if (claimed is null && !answerExpected)
        {
            return Result(SettleStatus.Succeeded, null);
        }
        if (claimed is null)
        {
            // Its last words may well say "done", but nothing it made (reply, commits sent back) arrived.
            var kept = workTree is null ? "" : $" Its work is kept on this machine in {workTree} for 24 hours.";
            if (AgentsOwn(refusal) is { } refused)
                return Result(SettleStatus.Failed, $"{ResultNotReceived}: the agent's answer was refused: {refused}{kept}");
            return Result(SettleStatus.Failed,
                $"{ResultNotReceived}: the agent exited without its mbus settle call reaching the runner, so its answer and any code it sent back are missing.{kept}");
        }
        var status = claimed.Status;
        return Result(status, status == SettleStatus.Succeeded ? null : errorText);
    }

    /// <summary>How long a readable error line may be.</summary>
    internal const int ErrorLength = 300;

    /// <summary>How long the agent's own error (from its settle call) may be.</summary>
    internal const int AgentErrorLength = 4000;

    /// <summary>
    /// One line of at most <see cref="ErrorLength"/> characters, for text taken from the CLI. Text that is a JSON object
    /// (an API error the CLI passed on as it was) gives its <c>message</c> (or <c>error.message</c>), and none means
    /// nothing readable. Anything else, <c>[Errno 13] Permission denied</c> included, is plain text.
    /// </summary>
    internal static string? Readable(string? text) => AgentText.OneLine(Unwrapped(text), ErrorLength);

    /// <summary>The agent's own error or summary: JSON unwrapped, its lines kept, capped at <see cref="AgentErrorLength"/>.</summary>
    internal static string? AgentsOwn(string? text)
    {
        var unwrapped = Unwrapped(text);
        if (unwrapped is null)
        {
            return null;
        }
        return unwrapped.Length <= AgentErrorLength ? unwrapped : unwrapped[..(AgentErrorLength - 1)] + "…";
    }

    private static string? Unwrapped(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var trimmed = text.Trim();
        if (!TryParseObject(trimmed, out var root))
        {
            return trimmed;
        }
        var message = MessageIn(root);
        // Unwrapped once and still JSON: nothing readable is better than raw JSON.
        return string.IsNullOrWhiteSpace(message) || TryParseObject(message.Trim(), out _) ? null : message.Trim();
    }

    /// <summary>True when <paramref name="text"/> is a JSON object (not an array, not text that merely starts with a brace).</summary>
    private static bool TryParseObject(string text, out System.Text.Json.JsonElement root)
    {
        root = default;
        if (!text.StartsWith('{'))
        {
            return false;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return false;
            }
            root = doc.RootElement.Clone();
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string? MessageIn(System.Text.Json.JsonElement root)
    {
        if (Agents.ClaudeOutputParser.Str(root, "message") is { Length: > 0 } message) return message;
        if (root.TryGetProperty("error", out var error))
        {
            return error.ValueKind == System.Text.Json.JsonValueKind.String ? error.GetString()
                : error.ValueKind == System.Text.Json.JsonValueKind.Object ? Agents.ClaudeOutputParser.Str(error, "message") : null;
        }
        return null;
    }

    private Dictionary<string, string?> RunEnvironmentVariables(RunWorkspace workspace)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["MBUS_DELIVERY_ID"] = DeliveryId,
            ["MBUS_THREAD_ID"] = Delivery.Envelope.ThreadId,
            ["MBUS_MESSAGE_ID"] = Delivery.Envelope.Id,
            ["MBUS_RUN_DIR"] = workspace.Root,
            // For the local MCP server (the original issue): the delivery as received (body sealed), and where its
            // settle tool records the agent's result for this runner to read after the agent exits.
            ["MBUS_DELIVERY_FILE"] = workspace.DeliveryFile,
            ["MBUS_SETTLE_FILE"] = workspace.SettleFile,
            ["MBUS_PERMISSION_PROFILE"] = _profile.Name,
        };
        foreach (var (name, value) in _tools?.Variables ?? new Dictionary<string, string>())
        {
            env[name] = value;
        }
        if (Delivery.AgentId is { Length: > 0 } agent)
        {
            // Which of this machine's agents the local MCP server acts as (the original issue).
            env["MBUS_AGENT"] = agent;
        }
        if (Delivery.Role is { } role)
        {
            env["MBUS_ROLE"] = role;
        }
        if (_span?.Id is { } traceParent)
        {
            // So the agent's own telemetry, and what it sends through mbus, joins the task's trace under this run (the original issue).
            env["TRACEPARENT"] = traceParent;
            env["TRACESTATE"] = _span.TraceStateString;
        }
        return env;
    }

    /// <summary>
    /// What the local mbus MCP server needs from the run: the run's own MBUS_* variables, and either where the runner
    /// serves the run's tools (the original issue) or, without that (isolation off), the runner's MBUS_HOME (the machine's settings and
    /// key). For agents that don't pass their environment on.
    /// </summary>
    private Dictionary<string, string> McpEnvironment(Dictionary<string, string?> environment)
    {
        string[] names = ["MBUS_DELIVERY_FILE", "MBUS_SETTLE_FILE", "MBUS_DELIVERY_ID", "MBUS_THREAD_ID", "MBUS_MESSAGE_ID", "MBUS_RUN_DIR", "MBUS_ROLE", "MBUS_AGENT",
            RunToolsVariables.File];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (environment.TryGetValue(name, out var value) && value is { Length: > 0 })
            {
                result[name] = value;
            }
        }
        if (_tools is null && _s.Host.GetEnvironmentVariable(Hosting.EnvVars.Home) is { Length: > 0 } home)
        {
            result[Hosting.EnvVars.Home] = home;
        }
        return result;
    }

    /// <summary>
    /// The run's variables plus the agent sign-ins stored for the runner service (the original issue), for the agent process only (the
    /// setup command and checkout never get them). One the runner's own environment sets is left to it: what the person
    /// started this runner with wins over what an earlier install stored.
    /// </summary>
    internal static Dictionary<string, string?> AgentEnvironment(Dictionary<string, string?> environment, IAgentAdapter adapter,
        SignInEnvironment signIns, bool windowsService = false)
    {
        var merged = new Dictionary<string, string?>(environment, environment.Comparer);
        var allowed = windowsService ? Service.ServiceAgentTokens.For(adapter.Kind) : null;
        if (allowed is not null && !allowed.Any(signIns.Stored.ContainsKey))
            throw new CliException("This agent needs a service token. Use mbus runner env --save --service.");

        foreach (var (name, value) in signIns.Added)
        {
            if (!CodexAuthentication.IsKeyVariable(name) && (allowed is null || allowed.Contains(name)))
                merged.TryAdd(name, value);
        }
        if (adapter.Kind == AgentKind.Codex && CodexAuthentication.ReadKey(signIns.Get) is { } key)
        {
            merged[CodexAuthentication.CodexKey] = key;
        }
        else if (adapter is OpenCodeAdapter { LocalEndpoint: null } && signIns.Get(CodexAuthentication.OpenAiKey) is { } providerKey)
        {
            // OpenCode's cloud provider already accepts OPENAI_API_KEY. Local inference receives neither variable.
            merged[CodexAuthentication.OpenAiKey] = providerKey;
        }
        return merged;
    }

    /// <summary>
    /// The agent's process as the run starts it: the adapter's command line for <paramref name="context"/> (told whether the
    /// run's sandbox holds the agent, the original issue), with the run's variables and the adapter's own, wrapped by the run's sandbox.
    /// </summary>
    internal static ProcessLaunch AgentProcessLaunch(IAgentAdapter adapter, IRunSandbox sandbox, string executable, RunWorkspace workspace,
        PermissionProfile runProfile, AgentLaunchContext context, Dictionary<string, string?> environment, Action<AgentLaunch>? onLaunch = null)
    {
        var launch = adapter.BuildLaunch(context with
        {
            RunnerSandboxed = sandbox.ConfinesAgents, RunnerIsolationEnabled = sandbox.IsolationEnabled, RunDirectory = workspace.Root,
            UsesCodexEnvironmentKey = environment.ContainsKey(CodexAuthentication.CodexKey),
        });
        onLaunch?.Invoke(launch);
        return sandbox.Wrap(new ProcessLaunch
        {
            FileName = executable,
            Arguments = launch.Arguments,
            WorkingDirectory = context.WorkingDirectory,
            Environment = WithAgentVariables(environment, launch.Environment),
            StandardInput = launch.StandardInput,
        }, workspace, runProfile);
    }

    /// <summary>The run's variables with the adapter's own on top (the original issue).</summary>
    private static Dictionary<string, string?> WithAgentVariables(Dictionary<string, string?> environment, IReadOnlyDictionary<string, string> agent)
    {
        var merged = new Dictionary<string, string?>(environment, environment.Comparer);
        foreach (var (name, value) in agent)
        {
            merged[name] = value;
        }
        return merged;
    }

    private void QueueCapacity(AgentCapacityReport? report)
    {
        if (report is null) return;
        _capacityChain = ReportCapacityAsync(_capacityChain, report);
    }

    private async Task ReportCapacityAsync(Task previous, AgentCapacityReport report)
    {
        try
        {
            await previous.ConfigureAwait(false);
            var merged = _s.Host.CapacityStore.Merge(report);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), _s.Time);
            await _s.Service.CapacityAsync(merged, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_capacityErrorLogged)
            {
                _capacityErrorLogged = true;
                try { await _s.Log.WriteLineAsync($"{DeliveryId}: Could not save or report agent capacity ({ex.GetType().Name}).").ConfigureAwait(false); }
                catch (Exception) { /* Optional capacity reporting must never fail the run, including its diagnostic. */ }
            }
        }
    }

    private void OnOutput(IAgentOutputParser parser, string line)
    {
        string? note;
        lock (_parserGate)
        {
            note = parser.Observe(line);
            if (parser.Capacity is { LimitHit: { } hit } capacity && hit != _capacityHit)
            {
                _capacityHit = hit;
                QueueCapacity(capacity);
            }
        }
        if (Delivery.CostCap is { } cap && _adapter.ReportsUsageAsItGoes)
        {
            CheckCost(parser, cap);
        }
        if (note is null)
        {
            return;
        }
        var now = _s.Time.GetUtcNow();
        lock (_parserGate)
        {
            _lastActivity = note;
            if (_lastAgentProgress is { } last && now - last < _s.Options.ProgressInterval)
            {
                return;
            }
            _lastAgentProgress = now;
        }
        Note(note);
    }

    /// <summary>
    /// The role's most cost per run (the original issue): the usage the agent has reported so far, priced from the service's table (or
    /// the agent's own cost), against the cap. Past it, the agent is stopped and the run fails with the reason. When the
    /// model has no price the run can't be capped, and its progress says so once.
    /// </summary>
    private void CheckCost(IAgentOutputParser parser, RunCostCap cap)
    {
        Usage? usage;
        lock (_parserGate)
        {
            usage = parser.UsageSoFar;
        }
        if (usage is null || Reason != StopReason.None)
        {
            return;
        }
        if (RunCosts.Estimate(usage, RunCosts.PriceFor(cap.Prices, usage.Model)) is not { } cost)
        {
            if (!_costUnknownNoted)
            {
                _costUnknownNoted = true;
                Note($"No price is known for {usage.Model ?? "this run's model"}, so the role's {Money(cap.MaxUsd)} most per run can't be checked");
            }
            return;
        }
        if (cost > cap.MaxUsd)
        {
            Stop(StopReason.CostCapReached,
                $"Stopped at an estimated {Money(cost)}, over the role's most cost per run of {Money(cap.MaxUsd)}.");
        }
    }

    private static string Money(decimal usd) => "$" + usd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Approval before each run (the original issue): asks the machine's owner through the service and waits for the answer, asking
    /// again every <see cref="RunnerOptions.ApprovalPollInterval"/>. Null once approved (the role's time limit starts
    /// then); otherwise why the run goes back for another machine. A cancel or stop while waiting ends the wait.
    /// </summary>
    private async Task<string?> WaitForApprovalAsync()
    {
        var waitingNoted = false;
        // Withdraws the terminal's question (the original issue) once the wait is over, however it ended. Not disposed: the question may
        // still be setting up its wait on it.
        var stillWaiting = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            while (true)
            {
                var pollInterval = _s.Options.ApprovalPollInterval;
                RunApprovalState? state;
                try
                {
                    state = await _s.Service.RunApprovalAsync(new RunApprovalRequest { DeliveryId = DeliveryId }, _stop.Token).ConfigureAwait(false);
                    if (state is null)
                    {
                        return "The service couldn't ask this machine's owner to approve the run, so it was handed back.";
                    }
                }
                catch (MessageBus.Cli.Server.WorkspaceSuspendedException)
                {
                    pollInterval = _s.Options.DefaultHeartbeat;
                    state = null; // The heartbeat reports suspension once; keep this waiting approval quiet.
                }
                catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException && !_stop.IsCancellationRequested)
                {
                    // The service is unreachable for now: ask again on the next round.
                    await _s.Log.WriteLineAsync($"{DeliveryId}: couldn't ask for its run approval: {ex.Message}").ConfigureAwait(false);
                    state = null;
                }
                switch (state?.Status)
                {
                    case RunApprovalStatus.Approved:
                        Note(state.Detail ?? "Approved to run here");
                        StartClock();
                        return null;
                    case RunApprovalStatus.Declined:
                        return state.Detail ?? "This machine's owner declined to run it here.";
                }
                // The next ask's timer is set before the note goes out, so whoever reads the note can move the clock on.
                var next = Task.Delay(pollInterval, _s.Time, _stop.Token);
                if (state?.Status == RunApprovalStatus.Pending && !waitingNoted)
                {
                    waitingNoted = true;
                    _s.RunApprovals?.Waiting(Delivery, state, stillWaiting.Token);
                    Note($"Waiting for {state.ApproverName ?? "this machine's owner"} to approve the run");
                }
                await next.ConfigureAwait(false);
            }
        }
        finally
        {
            stillWaiting.Cancel();
        }
    }

    /// <summary>
    /// While the agent runs: when nothing was reported for a whole <see cref="RunnerOptions.StillRunningInterval"/>,
    /// reports that it is still going, with how long and the last thing it was seen doing (the command, when its
    /// output said).
    /// </summary>
    private void StillRunning(DateTimeOffset agentStarted)
    {
        var now = _s.Time.GetUtcNow();
        string? activity;
        lock (_parserGate)
        {
            if (now - _lastReported < _s.Options.StillRunningInterval || _stop.IsCancellationRequested)
            {
                return;
            }
            activity = _lastActivity;
        }
        var minutes = Math.Max(1, (int)Math.Round((now - agentStarted).TotalMinutes));
        Note(activity is null ? $"Still working ({minutes} min)" : $"Still running ({minutes} min): {activity}");
    }

    /// <summary>Queues a progress report; reports go out in order and before the settle.</summary>
    private void Note(string message) => _ = Report(message);

    /// <summary><see cref="Note"/>, with what the run waits for from the sender (the original issue).</summary>
    /// <returns>When this report has gone (or failed, logged).</returns>
    private Task Report(string message, ProgressRequest? request = null)
    {
        var progress = new RunnerProgress
        {
            DeliveryId = DeliveryId,
            Progress = new ProgressUpdate { Message = message, At = _s.Time.GetUtcNow(), Request = request },
        };
        lock (_parserGate)
        {
            _lastReported = progress.Progress.At;
            _progressChain = _progressChain.ContinueWith(async _ =>
            {
                try
                {
                    await _s.Service.ProgressAsync(progress, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Never fault the chain: the settle waits for it.
                    await _s.Log.WriteLineAsync($"Progress for {DeliveryId} wasn't delivered: {ex.Message}").ConfigureAwait(false);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return _progressChain;
        }
    }

    /// <summary>Waits before settle retries 2 to 9 (about six minutes in all): long enough to ride out a service restart.</summary>
    internal static readonly TimeSpan[] SettleRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
         TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)];

    private string? _stoppedForSuspension;

    internal void StopForSuspension(string reference)
    {
        _stoppedForSuspension = reference;
        Stop(StopReason.Cancelled, $"Stopped because the workspace was suspended ({reference}).");
    }

    private string? _unreadableRole;

    /// <summary>
    /// Posts the settle, retrying with backoff while the service can't be reached. The run stays in the
    /// runner's heartbeat meanwhile, so the service keeps its lease. True once the service has it; false
    /// when it refused it (the task is no longer this machine's) or every retry failed: the run then
    /// leaves the heartbeat and the service puts the task back in the queue (the original issue).
    /// </summary>
    private async Task<bool> SettleAsync(SettleResult result)
    {
        if (_stoppedForSuspension is { } reference)
        {
            await _s.Log.WriteLineAsync($"{DeliveryId}: Stopped because the workspace was suspended ({reference}).").ConfigureAwait(false);
            return true; // The server releases the held task; clean up its local work as usual.
        }
        var settle = new RunnerSettle
        {
            DeliveryId = DeliveryId, Attempt = Delivery.Attempt, Result = result, Declined = _declined, UnreadableRole = _unreadableRole,
            Verification = _verification,
            VerificationAttempt = Delivery.Verification is null ? null : Delivery.Attempt,
            VerificationDigest = Delivery.Verification?.Digest,
        };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _s.Service.SettleAsync(settle, CancellationToken.None).ConfigureAwait(false);
                await _s.Log.WriteLineAsync($"{DeliveryId}: {result.Status.ToString().ToLowerInvariant()}{(result.Error is { } e ? $" ({e})" : "")}").ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                var refused = ex is HttpRequestException { StatusCode: { } status } && (int)status is >= 400 and < 500 and not 408 and not 429;
                if (refused || attempt >= SettleRetryDelays.Length || _s.Stopping.IsCancellationRequested)
                {
                    await _s.Log.WriteLineAsync($"Couldn't settle {DeliveryId}: {ex.Message}").ConfigureAwait(false);
                    return false;
                }
                try
                {
                    await Task.Delay(SettleRetryDelays[attempt], _s.Time, _s.Stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The runner is stopping: one last try now.
                }
            }
        }
    }

    /// <summary>
    /// The agent gave up after a settle refused for an attachment: its kept answer is sent now (the original issue). Not for a run that was
    /// stopped (cancelled, its cost cap, its time limit, a lost lease, the runner stopping): its result says why instead, and
    /// a task handed back mustn't have an answer already.
    /// </summary>
    private async Task SendKeptAnswerAsync(RunWorkspace workspace)
    {
        if (_s.SendKeptAnswer is not { } send || File.Exists(workspace.SettleFile) || Reason != StopReason.None)
        {
            return;
        }
        try
        {
            if (await send(workspace, Delivery, _s.Stopping).ConfigureAwait(false))
            {
                Note("Sent the answer the agent tried to settle with, and what it couldn't attach");
            }
        }
        catch (Exception ex)
        {
            await _s.Log.WriteLineAsync($"{DeliveryId}: the agent's kept answer wasn't sent: {ex.Message}").ConfigureAwait(false);
        }
    }

    private SettleInput? ReadAgentSettle(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }
            var input = MessageBusJson.Deserialize<SettleInput>(File.ReadAllText(file));
            return input is null || (input.DeliveryId is { } id && id != DeliveryId) ? null : input;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task WriteLogAsync(RunWorkspace workspace, IReadOnlyList<string> tail)
    {
        try
        {
            await File.WriteAllLinesAsync(workspace.LogFile, tail).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The last few output lines that aren't JSON events (what a CLI printed to stderr as it crashed), as one line.</summary>
    private static string? LastLines(IReadOnlyList<string> tail)
    {
        var plain = tail.Select(l => l.Trim()).Where(l => l.Length > 0 && !TryParseObject(l, out _)).TakeLast(5).ToList();
        return plain.Count == 0 ? null : AgentText.OneLine(string.Join(" ", plain), ErrorLength);
    }
}
