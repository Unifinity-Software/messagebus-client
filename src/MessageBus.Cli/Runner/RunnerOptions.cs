using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Machines;

namespace MessageBus.Cli.Runner;

/// <summary>How the runner on this machine behaves (set by its owner: flags or <see cref="RunnerEnvVars"/>).</summary>
public sealed record RunnerOptions
{
    /// <summary>The selected machine key backend.</summary>
    public string? KeyStore { get; init; }

    /// <summary>
    /// How many tasks run at once when it starts, until the dashboard's setting arrives (the original issue). Only the dashboard
    /// changes it, up to <see cref="SlotAdvice.MostForMachine"/> for this machine (the original issue).
    /// </summary>
    public int Slots { get; init; } = 1;

    /// <summary>macOS idle sleep protection (default: while the runner runs).</summary>
    public RunnerKeepAwake KeepAwake { get; init; } = RunnerKeepAwake.Always;

    /// <summary>Machine owner's Codex sandbox override: always or unelevated; null follows the role.</summary>
    public string? CodexSandbox { get; init; }

    /// <summary>Machine owner's add-on tool policy; null uses the environment, then allow.</summary>
    public string? AgentTools { get; init; }

    /// <summary>The machine owner's add-on tool policy variable.</summary>
    public const string AgentToolsVariable = "MBUS_RUNNER_AGENT_TOOLS";

    /// <summary>Folder holding one directory per run (its worktree, prompt and log).</summary>
    public required string WorkRoot { get; init; }

    /// <summary>Tests only: every run's folder is left as it ended, to look at. A runner always deletes them (the original issue).</summary>
    public bool KeepRunFolders { get; init; }

    /// <summary>Commands to start instead of the agents' usual ones (a full path, or a name looked up on PATH).</summary>
    public IReadOnlyDictionary<AgentKind, string> AgentCommands { get; init; } = new Dictionary<AgentKind, string>();

    /// <summary>The agent's own progress is reported at most this often (the runner's own notes always go).</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>While an agent runs, a "still running" report goes out when nothing was reported for this long (the original issue).</summary>
    public TimeSpan StillRunningInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Lines of output kept in the run's log when the role keeps local logs.</summary>
    public int LogTailLines { get; init; } = 200;

    /// <summary>Heartbeat interval when no role asks for a shorter one.</summary>
    public TimeSpan DefaultHeartbeat { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// the original issue: how long a run whose code starts after commits this machine lacks waits for the sender's mbus to send the
    /// repository's earlier history, before it fails saying so.
    /// </summary>
    public TimeSpan HistoryWait { get; init; } = RunWorkspace.DefaultHistoryWait;

    /// <summary>How often a run waiting for its machine owner's approval asks whether it has been answered (the original issue).</summary>
    public TimeSpan ApprovalPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Options from <see cref="RunnerEnvVars"/>, with <paramref name="workRoot"/> (a command-line flag) on top.</summary>
    /// <param name="env">Environment lookup.</param>
    /// <param name="homeDirectory">The user's home directory (default work root <c>~/.mbus/runs</c>).</param>
    /// <param name="workRoot"><c>--work-dir</c>, if given.</param>
    /// <param name="keepAwake"><c>--keep-awake</c>, if given.</param>
    /// <param name="codexSandbox"><c>--codex-sandbox</c>, if given.</param>
    /// <param name="agentTools"><c>--agent-tools</c>, if given.</param>
    public static RunnerOptions FromEnvironment(Func<string, string?> env, string homeDirectory, string? workRoot = null, string? keepAwake = null, string? codexSandbox = null, string? agentTools = null)
    {
        var commands = new Dictionary<AgentKind, string>();
        foreach (var kind in AgentKinds.Launchable)
        {
            if (RunnerEnvironment.NonEmpty(env(AgentKinds.OverrideVariable(kind))) is { } command)
            {
                commands[kind] = command;
            }
        }
        return new RunnerOptions
        {
            WorkRoot = Path.GetFullPath(workRoot ?? RunnerEnvironment.NonEmpty(env(RunnerEnvVars.WorkDirectory)) ?? Path.Combine(homeDirectory, ".mbus", "runs")),
            AgentCommands = commands,
            KeepAwake = ParseKeepAwake(keepAwake ?? RunnerEnvironment.NonEmpty(env(RunnerEnvVars.KeepAwake))),
            AgentTools = ParseAgentTools(agentTools ?? env(AgentToolsVariable)),
            CodexSandbox = ParseCodexSandbox(codexSandbox ?? env(CodexAdapter.SandboxOverrideVariable)),
        };
    }

    /// <summary>Parses the command-line or environment sleep policy.</summary>
    public static RunnerKeepAwake ParseKeepAwake(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "always" => RunnerKeepAwake.Always,
        "tasks" => RunnerKeepAwake.Tasks,
        "off" => RunnerKeepAwake.Off,
        _ => throw new FormatException("--keep-awake / MBUS_RUNNER_KEEP_AWAKE must be always, tasks or off."),
    };

    internal static string ParseAgentTools(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "allow" => "allow",
        "refuse" => "refuse",
        _ => throw new FormatException("--agent-tools / MBUS_RUNNER_AGENT_TOOLS must be allow or refuse."),
    };

    internal static string? ParseCodexSandbox(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "always" => "always",
        "unelevated" => "unelevated",
        _ => throw new FormatException("Codex sandbox must be always or unelevated (--codex-sandbox / MBUS_RUNNER_CODEX_SANDBOX)."),
    };
}
