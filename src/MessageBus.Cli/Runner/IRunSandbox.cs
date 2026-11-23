using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>
/// Wraps an agent's command line in a sandbox before it starts. Integration point for the original issue (run
/// isolation and per-run repository credentials, under <c>Runner/Sandbox/</c>): an OS sandbox or
/// container that limits the file system to the run directory, the network to the profile's hosts,
/// and swaps in a per-run repository token. The runner already applies the profile as the agent
/// CLI's own flags; the sandbox is the layer the agent can't switch off.
/// </summary>
/// <remarks>
/// A run calls <see cref="PrepareAsync"/> once, before the checkout; <see cref="Wrap"/> for each
/// process it starts (git checkout, setup command, agent); <see cref="Started"/> once each of those
/// is running; and <see cref="Ended"/> once when the run is over, whatever happened.
/// </remarks>
public interface IRunSandbox
{
    internal string? AgentRefusal(Contracts.Machines.AgentKind kind) => null;

    internal bool UsesPrivateHome => false;

    internal string? QuarantineProblem => null;

    // True only for this fresh launch when the external sandbox physically enforces
    // the supplied file policy. This must never be inferred from machine-wide readiness.
    internal bool EnforcesAgentFilePolicy(RunWorkspace workspace, PermissionProfile profile) => false;

    /// <summary>The launch to use instead of <paramref name="launch"/>.</summary>
    ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile);

    /// <summary>Sets up the run's own folders and repository credential, before anything starts in it.</summary>
    Task PrepareAsync(RunWorkspace workspace, TaskDelivery delivery, PermissionProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A process started from a wrapped launch is running.</summary>
    void Started(RunWorkspace workspace, int processId)
    {
    }

    /// <summary>The run is over: end whatever it left running and withdraw its credential. Never throws.</summary>
    void Ended(RunWorkspace workspace)
    {
    }

    /// <summary>Awaits owned sandbox cleanup before the run can settle and release its slot.</summary>
    Task EndedAsync(RunWorkspace workspace)
    {
        Ended(workspace);
        return Task.CompletedTask;
    }

    /// <summary>What runs get here, for the Runners page (the original issue); null when this sandbox doesn't say.</summary>
    Contracts.Machines.RunIsolationReport? Report => null;

    /// <summary>
    /// Whether this sandbox holds an agent that starts no sandbox of its own, keeping it out of personal files and other runs
    /// (the macOS sandbox). Then a run that edits files starts Codex without its own sandbox, so only one layer applies (the original issue).
    /// </summary>
    bool ConfinesAgents => false;

    /// <summary>Whether runner isolation is enabled, including through wrappers that add tool paths.</summary>
    bool IsolationEnabled => true;
}

/// <summary>No sandbox: the agent runs as this user, in its worktree, with the CLI's own permission flags.</summary>
public sealed class NoSandbox : IRunSandbox
{
    /// <inheritdoc />
    public bool IsolationEnabled => false;

    /// <inheritdoc />
    public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile) => launch;

    /// <inheritdoc />
    public Contracts.Machines.RunIsolationReport? Report { get; } = new()
    {
        Summary = "Off: runs inherit this machine's environment and git settings.",
        Credentials = "Whatever this machine's own git settings and credentials allow.",
        Limitations = ["Push: a role that can't push is stopped only by the agent's own command rules.", Sandbox.RunIsolation.NetworkLimit],
        Level = "off",
        Network = "open",
    };
}
