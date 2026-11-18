using MessageBus.Cli.Output;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Contracts.Machines;
using Spectre.Console;

namespace MessageBus.Cli.Runner;

/// <summary>
/// <c>mbus runner</c>'s start-up summary on a terminal (the original issue): one block with this machine's version, server, workspace,
/// tasks at once, run isolation, network, updates and each agent (ready or not, why, and its roles), instead of the
/// lines the parts print as they start. Until <see cref="Show"/>, the runner's lines are held (<see cref="Log"/>); the
/// lines the block says again are dropped (<see cref="Covered"/>, <see cref="Updates"/>). Not on a terminal (a service, a
/// redirect) there is no view and the runner prints its plain lines as before, so logs stay greppable.
/// </summary>
public sealed class RunnerStartupView
{
    private readonly IAnsiConsole _console;
    private readonly StartupGate _log;
    private readonly StartupGate _covered;
    private readonly StartupGate _updates;
    private readonly string? _workspace;

    /// <param name="console">The terminal the block is drawn on.</param>
    /// <param name="log">Where the runner's lines go (already timestamped).</param>
    /// <param name="workspace">The workspace's name as stored, when known; the service's current one replaces it (the original issue).</param>
    public RunnerStartupView(IAnsiConsole console, TextWriter log, string? workspace)
    {
        _console = console;
        _log = new StartupGate(log);
        _covered = new StartupGate(log);
        _updates = new StartupGate(log);
        _workspace = workspace;
    }

    /// <summary>
    /// The decision (the original issue): a block on a terminal; plain lines when the runner writes to a log file (a service) or its
    /// output is redirected.
    /// </summary>
    public static bool Wanted(bool errorIsTerminal, bool logFile) => errorIsTerminal && !logFile;

    /// <summary>The runner's lines: held until the block is shown, then passed on in order.</summary>
    public TextWriter Log => _log;

    /// <summary>For the parts whose start-up lines the block says again (run isolation, the agent scan): dropped until it is shown.</summary>
    public TextWriter Covered => _covered;

    /// <summary>For the update setup: what it says at start-up becomes the block's Updates row.</summary>
    public TextWriter Updates => _updates;

    /// <summary>Whether this runner updates itself (it has an updater).</summary>
    public bool UpdatesOn { get; set; }

    /// <summary>the original issue: rewrites the mbus commands in agents' reasons so they start this mbus (only here, on this machine); null leaves them.</summary>
    public Func<string, string>? Advice { get; set; }

    /// <summary>What the runner knows once it has started (its engine has scanned and read its policy).</summary>
    public sealed record Facts
    {
        public required IRunnerHostContext Host { get; init; }
        public required RunnerOptions Options { get; init; }
        public required RunnerEngine Engine { get; init; }
        public required IRunSandbox Sandbox { get; init; }
        public required RunIsolationOptions Isolation { get; init; }
        public required IReadOnlyDictionary<string, string> ToolPaths { get; init; }
    }

    /// <summary>Draws the block, then lets the held lines through. Only the first call draws.</summary>
    public void Show(Facts facts)
    {
        if (Shown)
        {
            return;
        }
        Shown = true;
        var host = facts.Host;
        var status = facts.Engine.GetStatus();
        var rows = new List<SummaryRow>();
        rows.AddRange(RunnerFacts.Identity(RunnerEnvironment.Version, host.ServerUrl, host.MachineName, host.Location, facts.Engine.WorkspaceName ?? _workspace));
        rows.Add(RunnerFacts.Slots(status.Slots, status.SlotCeiling ?? status.Slots));
        rows.Add(new("Runs in", facts.Options.WorkRoot));
        rows.Add(RunnerFacts.Isolation(facts.Sandbox, facts.Isolation));
        rows.Add(RunnerFacts.Network(facts.Sandbox, facts.Isolation));
        rows.Add(RunnerFacts.Updates(UpdatesOn, _updates.HeldLines, facts.Engine.Releases));
        if (RunnerFacts.TaskPath(facts.ToolPaths, host.GetEnvironmentVariable) is { } path)
        {
            rows.Add(path);
        }
        if (facts.Engine.LastInventory is { } inventory)
        {
            // The registry's answer carries the roles; without it (the server wasn't reached) they are left out.
            var registry = facts.Engine.RegistryAgents.Count > 0
                ? facts.Engine.RegistryAgents.Where(a => a.MachineId == inventory.MachineId || string.IsNullOrEmpty(inventory.MachineId)).ToList()
                : null;
            rows.AddRange(RunnerFacts.Agents(Advice is { } advice
                ? inventory.Agents.Select(a => a.NotReady is { } why ? a with { NotReady = advice(why) } : a)
                : inventory.Agents, registry));
            if (inventory.Agents.Any(a => a is { Kind: AgentKind.ClaudeCode, Found: true, NotReady: null })
                && MessageBus.Cli.Agents.SignInChecks.SshKeychainWarning(host.GetEnvironmentVariable) is { } warning)
            {
                rows.Add(new("", warning, SummaryTone.Note));
            }
            rows.Add(RunnerFacts.Toolchains(inventory.Capabilities));
        }
        try
        {
            SummaryView.Write(_console, "mbus runner", rows);
        }
        finally
        {
            Release(shown: true);
        }
    }

    /// <summary>Whether the block was drawn.</summary>
    public bool Shown { get; private set; }

    /// <summary>
    /// Lets every line through from now on. Without the block (the runner stopped before it started), the held lines
    /// are all written, so nothing it said is lost.
    /// </summary>
    public void Release(bool shown = false)
    {
        _updates.Open(replay: !shown);
        _covered.Open(replay: !shown);
        _log.Open(replay: true);
    }
}
