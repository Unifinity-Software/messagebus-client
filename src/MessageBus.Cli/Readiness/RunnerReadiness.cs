using System.Collections.Concurrent;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Readiness;

/// <summary>
/// What this machine can take, as the runner knows it (the original issue, the original issue). At start-up and on each rescan it
/// learns the scan (<see cref="Scanned"/>): an agent the scan marked not ready (git doesn't work, it
/// can't use its sign-in) isn't offered work by the service, and a delivery for it anyway is declined
/// as <see cref="DeclineReason.MachineNotReady"/> (<see cref="Admit"/>). A run that fails because of
/// the machine rather than the task (macOS's git placeholder, "Not logged in") is handed back the
/// same way (<see cref="Watch"/>): its settle carries <see cref="RunnerSettle.Declined"/>, the agent
/// is marked not ready and the service is told at once, until the next rescan clears the mark.
/// A delivery for a not-ready agent means the service thinks otherwise (someone ran
/// <c>mbus connect --rescan</c> after a fix): it is declined, and the runner scans again (at most
/// once a minute) so it picks up the fix, or tells the service again that it isn't fixed.
/// </summary>
/// <param name="log">Status lines.</param>
/// <param name="time">Clock for how often a decline may start a scan.</param>
/// <param name="advice">
/// the original issue: rewrites the mbus commands in the lines printed here so they start this mbus (<see cref="Hosting.MbusOnPath.Advice"/>).
/// Only for this machine's own output: reasons sent to the service stay as they are, because the full path names the user's folders.
/// </param>
public sealed class RunnerReadiness(TextWriter log, TimeProvider? time = null, Func<string, string>? advice = null)
{
    /// <summary>Shortest time between scans started by declines.</summary>
    public static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly TextWriter _log = TextWriter.Synchronized(log);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private Func<CancellationToken, Task>? _rescan;
    private DateTimeOffset? _lastRescan;
    private readonly ConcurrentDictionary<string, AgentKind> _deliveries = new(StringComparer.Ordinal);
    private readonly Dictionary<AgentKind, string> _scanned = [];
    private readonly Dictionary<AgentKind, string> _marked = [];
    private MachineInventory? _inventory;

    /// <summary>
    /// Learns a fresh scan before it is sent: what it found not ready, and nothing marked since the last
    /// one (a rescan is how a fixed machine says so). Prints what is ready and what to fix.
    /// </summary>
    /// <returns>The inventory to send.</returns>
    public MachineInventory Scanned(MachineInventory inventory, Func<string, string?> env)
    {
        lock (_gate)
        {
            _inventory = inventory;
            _scanned.Clear();
            _marked.Clear();
            foreach (var agent in inventory.Agents.Where(a => a is { Found: true, NotReady: not null }))
            {
                _scanned[agent.Kind] = agent.NotReady!;
            }
        }
        var found = inventory.Agents.Where(a => a.Found).ToList();
        var ready = found.Where(a => a is { Kind: not AgentKind.Codex, NotReady: null }).Select(a => $"{AgentKinds.DisplayName(a.Kind)}{(a.Version is { } v ? " " + v : "")}").ToList();
        var codexConfigured = found.Any(a => a is { Kind: AgentKind.Codex, SignedIn: true, NotReady: null });
        var tools = inventory.Capabilities.Where(c => c.StartsWith(CapabilityNames.ToolPrefix + ":", StringComparison.Ordinal))
            .Select(c => c[(CapabilityNames.ToolPrefix.Length + 1)..]).ToList();
        if (ready.Count > 0) _log.WriteLine($"Ready to run: {string.Join(", ", ready)}.");
        if (codexConfigured) _log.WriteLine($"Codex credentials configured. {Local(CodexAuthentication.VerifyAccess)}");
        if (ready.Count == 0 && !codexConfigured) _log.WriteLine("No agent on this machine can take work yet.");
        foreach (var problem in found.Where(a => a.NotReady is not null).GroupBy(a => a.NotReady!))
        {
            _log.WriteLine($"Not ready: {string.Join(", ", problem.Select(a => AgentKinds.DisplayName(a.Kind)))}. {Local(problem.Key)}");
        }
        if (found.Any(a => a is { Kind: AgentKind.ClaudeCode, NotReady: null }) && SignInChecks.SshKeychainWarning(env) is { } warning)
        {
            _log.WriteLine(warning);
        }
        _log.WriteLine(tools.Count > 0 ? $"Toolchains: {string.Join(", ", tools)}." : "No toolchains found (git, dotnet, node, …).");
        return inventory;
    }

    /// <summary>A line for this machine's own output, with its mbus commands starting this mbus.</summary>
    private string Local(string text) => advice?.Invoke(text) ?? text;

    /// <summary>Why a delivery for <paramref name="kind"/> can't run here now, or null; remembers the delivery's agent for its settle.</summary>
    public string? Admit(TaskDelivery delivery, AgentKind kind)
    {
        lock (_gate)
        {
            if (_marked.TryGetValue(kind, out var reason) || _scanned.TryGetValue(kind, out reason))
            {
                RescanSoon();
                return Tagged(reason);
            }
        }
        _deliveries[delivery.DeliveryId] = kind;
        return null;
    }

    /// <summary>
    /// The reason as the thread shows it (its last error, and the reason of its status change):
    /// <c>machine-not-ready on {host}: …</c>. Orchestrators (the mbus-orchestrate skill) look for the literal
    /// <c>machine-not-ready</c> to tell a machine problem from a task failure.
    /// </summary>
    private string Tagged(string reason)
    {
        var host = _inventory?.HostName is { Length: > 0 } name ? " on " + name : "";
        return $"machine-not-ready{host}: {reason}";
    }

    /// <summary>How to scan again and send the result (the engine's rescan), for declines that may be out of date.</summary>
    public void RescanWith(Func<CancellationToken, Task> rescan) => _rescan = rescan;

    private void RescanSoon()
    {
        var now = _time.GetUtcNow();
        if (_rescan is not { } rescan || (_lastRescan is { } last && now - last < RescanInterval))
        {
            return;
        }
        _lastRescan = now;
        _ = Task.Run(async () =>
        {
            try
            {
                await rescan(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                await _log.WriteLineAsync($"Couldn't scan this machine again: {ex.Message}").ConfigureAwait(false);
            }
        });
    }

    /// <summary>The service client the engine uses: settles of runs that failed because of the machine are handed back.</summary>
    public IRunnerServiceClient Watch(IRunnerServiceClient service) => new Watched(this, service);

    /// <summary>
    /// Whether a run's failure is the machine's, not the task's: git is macOS's placeholder or missing,
    /// or the agent can't use its sign-in (it answered without using the model). Null when it is the task's.
    /// </summary>
    internal static (bool WholeMachine, string Reason)? Classify(AgentKind kind, SettleResult result)
    {
        if (result.Status is not (SettleStatus.Failed or SettleStatus.Abandoned) || result.Error is not { Length: > 0 } error)
        {
            return null;
        }
        if (MachineTools.IsDeveloperToolsPlaceholder(error) || error.Contains("git isn't installed on this machine", StringComparison.Ordinal))
        {
            return (true, (MachineTools.IsDeveloperToolsPlaceholder(error)
                ? "git on this machine is only macOS's placeholder: Apple's command line developer tools aren't installed. Fix: run xcode-select --install on the Mac"
                : "git isn't installed on this machine. Fix: install git") + ", then mbus connect --rescan or restart mbus runner.");
        }
        var usedModel = result.Usage is { } usage && usage.TokensIn + usage.TokensOut > 0;
        if (!usedModel && kind == AgentKind.Codex && CodexAuthentication.Refusal(error) is { } refusal)
        {
            return (false, refusal);
        }
        if (!usedModel && (error.Contains("Not logged in", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Please run /login", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase)))
        {
            return (false, $"{AgentKinds.DisplayName(kind)} couldn't use its sign-in when it ran here. Fix: {(kind == AgentKind.Codex ? "run codex login" : "run claude setup-token and set " + SignInChecks.ClaudeTokenVariable + " where mbus runner starts (for the runner service, run mbus runner install again from a shell where it is set), or start the runner from the machine's own terminal")}, then mbus connect --rescan or restart mbus runner.");
        }
        return null;
    }

    private RunnerSettle Review(RunnerSettle settle, out MachineInventory? changed)
    {
        changed = null;
        if (settle.Declined is not null)
        {
            return settle; // already handed back for its own reason (a declined run approval, the original issue)
        }
        if (!_deliveries.TryGetValue(settle.DeliveryId, out var kind) || Classify(kind, settle.Result) is not { } verdict)
        {
            return settle;
        }
        lock (_gate)
        {
            foreach (var agent in verdict.WholeMachine ? (_inventory?.Agents.Where(a => a.Found).Select(a => a.Kind) ?? []).Append(kind) : [kind])
            {
                _marked[agent] = verdict.Reason;
            }
            changed = Annotated();
        }
        _log.WriteLine($"{settle.DeliveryId}: handed back, this machine isn't ready: {Local(verdict.Reason)}");
        return settle with
        {
            Declined = DeclineReason.MachineNotReady,
            Result = settle.Result with { Status = SettleStatus.Abandoned, Error = kind == AgentKind.Codex
                ? Tagged(verdict.Reason) : $"{Tagged(verdict.Reason)} ({settle.Result.Error})" },
        };
    }

    /// <summary>The last scan with what was marked since: those agents not ready, without their <c>agent:</c> capability.</summary>
    private MachineInventory? Annotated()
    {
        if (_inventory is not { } inventory)
        {
            return null;
        }
        var agents = inventory.Agents.Select(a => a.Found && a.NotReady is null && _marked.TryGetValue(a.Kind, out var reason) ? a with { NotReady = reason } : a).ToList();
        var notReady = agents.Where(a => a.NotReady is not null).Select(a => CapabilityNames.Format(CapabilityNames.AgentPrefix, AgentKinds.Name(a.Kind))).ToHashSet(StringComparer.Ordinal);
        return inventory with { RoleKeysReadable = null, Agents = agents, Capabilities = [.. inventory.Capabilities.Where(c => !notReady.Contains(c))] };
    }

    private sealed class Watched(RunnerReadiness readiness, IRunnerServiceClient inner) : IRunnerServiceClient
    {
        public Task<HeartbeatAck> HeartbeatAsync(RunnerHeartbeat heartbeat, CancellationToken cancellationToken) => inner.HeartbeatAsync(heartbeat, cancellationToken);

        public Task ProgressAsync(RunnerProgress progress, CancellationToken cancellationToken) => inner.ProgressAsync(progress, cancellationToken);

        public Task<IReadOnlyList<AgentInfo>> InventoryAsync(MachineInventory inventory, CancellationToken cancellationToken) => inner.InventoryAsync(inventory, cancellationToken);

        public Task<bool> SupportsWorkflowsAsync(CancellationToken cancellationToken) => inner.SupportsWorkflowsAsync(cancellationToken);

        public Task<RunnerWorkPolicy?> PolicyAsync(CancellationToken cancellationToken) => inner.PolicyAsync(cancellationToken);

        public Task<RunApprovalState?> RunApprovalAsync(RunApprovalRequest request, CancellationToken cancellationToken) =>
            inner.RunApprovalAsync(request, cancellationToken);

        public Task CapacityAsync(MessageBus.Contracts.Runner.AgentCapacityReport report, CancellationToken cancellationToken) => inner.CapacityAsync(report, cancellationToken);

        public Task UpdateProgressAsync(MessageBus.Contracts.Releases.RunnerUpdateProgress progress, CancellationToken cancellationToken) =>
            inner.UpdateProgressAsync(progress, cancellationToken);

        public async Task SettleAsync(RunnerSettle settle, CancellationToken cancellationToken)
        {
            var reviewed = readiness.Review(settle, out var changed);
            // A settle the runner retries is reviewed again, so the delivery is forgotten only once it got through.
            await inner.SettleAsync(reviewed, cancellationToken).ConfigureAwait(false);
            readiness._deliveries.TryRemove(settle.DeliveryId, out _);
            if (changed is null)
            {
                return;
            }
            try
            {
                // So the service stops offering the agent now, not at the next rescan.
                await inner.InventoryAsync(changed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await readiness._log.WriteLineAsync($"Couldn't tell the service this machine isn't ready: {ex.Message}").ConfigureAwait(false);
            }
        }
    }
}
