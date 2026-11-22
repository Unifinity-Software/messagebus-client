using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;

namespace MessageBus.Cli.Runner.Agents;

/// <summary>Names and commands of the agent kinds the runner can start.</summary>
public static class AgentKinds
{
    /// <summary>Kinds the runner has an adapter for.</summary>
    public static IReadOnlyList<AgentKind> Launchable { get; } = [AgentKind.ClaudeCode, AgentKind.Codex, AgentKind.Copilot, AgentKind.OpenCode, AgentKind.Antigravity];

    /// <summary>The contract name (<c>claude-code</c>).</summary>
    public static string Name(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => "claude-code",
        AgentKind.Codex => "codex",
        AgentKind.Copilot => "copilot",
        AgentKind.OpenCode => "opencode",
        AgentKind.Antigravity => "antigravity",
        _ => "custom",
    };

    /// <summary>The command the kind is started with.</summary>
    public static string Command(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => "claude",
        AgentKind.Codex => "codex",
        AgentKind.Copilot => "copilot",
        AgentKind.OpenCode => "opencode",
        AgentKind.Antigravity => "agy",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No command for custom agents."),
    };

    /// <summary>How people know it.</summary>
    public static string DisplayName(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => "Claude Code",
        AgentKind.Codex => "Codex",
        AgentKind.Copilot => "GitHub Copilot CLI",
        AgentKind.OpenCode => "OpenCode",
        AgentKind.Antigravity => "Antigravity CLI",
        _ => "Custom agent",
    };

    /// <summary>Parses a contract name (<c>claude-code</c>).</summary>
    public static bool TryParse(string text, out AgentKind kind)
    {
        foreach (var candidate in Enum.GetValues<AgentKind>())
        {
            if (string.Equals(Name(candidate), text, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }
        kind = default;
        return false;
    }

    /// <summary>The environment variable that overrides a kind's command (<c>MBUS_RUNNER_AGENT_CLAUDE_CODE</c>).</summary>
    public static string OverrideVariable(AgentKind kind) =>
        RunnerEnvVars.AgentCommandPrefix + Name(kind).Replace('-', '_').ToUpperInvariant();
}

/// <summary>What the adapter needs to build one agent command line.</summary>
/// <param name="Prompt">The full task prompt.</param>
/// <param name="PromptFile">The same prompt, saved in the run directory (for CLIs that take no stdin).</param>
/// <param name="Profile">What the run may do.</param>
/// <param name="WorkingDirectory">The run's worktree.</param>
/// <param name="McpEnvironment">
/// The run's variables the local mbus MCP server (the original issue) needs (MBUS_DELIVERY_FILE, MBUS_SETTLE_FILE, …), for CLIs
/// that don't pass their own environment on to the MCP servers they start.
/// </param>
/// <param name="ResumeSessionId">
/// Continue this earlier session of the agent instead of starting a new one (the original issue: a reply to a closed orchestrator
/// session); only for adapters whose <see cref="IAgentAdapter.CanResume"/> is true.
/// </param>
/// <param name="Model">The model to run with instead of the agent's own default (the task's or role's, the original issue, or a role's cheaper model, the original issue); null for its default.</param>
/// <param name="RunnerSandboxed">
/// The runner's own OS sandbox will hold the agent and everything it starts (<see cref="IRunSandbox.ConfinesAgents"/>), so an agent
/// that would start its own sandbox needn't (the original issue).
/// </param>
/// <param name="RunDirectory">The run's folder (the worktree is in it), for run-local caches; null when unknown.</param>
/// <param name="CodexOnWindows">The role's Codex sandbox setting on Windows.</param>
/// <param name="KeepAgentTools">Named tools to keep from the agent's own settings; null or empty turns this off.</param>
/// <param name="RunnerIsolationEnabled">The runner's isolation is enabled, even if it relies on the agent's own sandbox.</param>
public sealed record AgentLaunchContext(string Prompt, string PromptFile, PermissionProfile Profile, string WorkingDirectory,
    IReadOnlyDictionary<string, string>? McpEnvironment = null, string? ResumeSessionId = null, string? Model = null,
    bool RunnerSandboxed = false, string? RunDirectory = null, CodexWindowsSandbox CodexOnWindows = CodexWindowsSandbox.Sandbox,
    bool RunnerIsolationEnabled = true, IReadOnlyList<string>? KeepAgentTools = null)
{
    /// <summary>Named tools to keep from the agent's own settings; empty turns this off.</summary>
    public IReadOnlyList<string> KeepAgentTools { get; init; } = KeepAgentTools ?? [];

    internal bool UsesCodexEnvironmentKey { get; init; }

    internal bool ExternalFilePolicyEnforced { get; init; }
}

/// <summary>An agent command line.</summary>
/// <param name="Arguments">Arguments after the executable.</param>
/// <param name="StandardInput">Text written to the agent's stdin (then closed); null to close it at once.</param>
public sealed record AgentLaunch(IReadOnlyList<string> Arguments, string? StandardInput)
{
    /// <summary>Why this run must fail before starting the agent.</summary>
    public string? CannotStart { get; init; }

    /// <summary>A mode decision to report once in the run's progress and log.</summary>
    public string? Note { get; init; }

    /// <summary>A runner limitation appended to the final summary.</summary>
    public string? SettleNote { get; init; }

    /// <summary>Variables the agent gets on top of the run's (OpenCode's per-run settings, the original issue).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

/// <summary>What the agent CLI's output said about the run.</summary>
public sealed record AgentOutcome
{
    /// <summary>Model, tokens, cost and time, as far as the CLI reported them.</summary>
    public Usage? Usage { get; init; }

    /// <summary>The agent's last message (never trusted as the result; used as a summary when the agent didn't settle).</summary>
    public string? FinalText { get; init; }

    /// <summary>The CLI itself reported an error (Claude's <c>is_error</c>, Codex's <c>turn.failed</c>).</summary>
    public bool ReportedError { get; init; }

    /// <summary>The CLI's error message, if any.</summary>
    public string? ErrorText { get; init; }

    /// <summary>
    /// The CLI ended a turn and later resumed (Claude Code after background work, the original issue). Its time covers only its
    /// turns, not the waits between them, so the run's own clock is the run's time.
    /// </summary>
    public bool SpansTurns { get; init; }
}

/// <summary>Reads one run's output, line by line.</summary>
public interface IAgentOutputParser
{
    /// <summary>Takes one stdout line; returns a one-line progress note worth reporting, or null.</summary>
    string? Observe(string line);

    /// <summary>What the output said, after the process ended.</summary>
    AgentOutcome Complete();

    /// <summary>
    /// Usage so far, while the run goes on (the original issue), for a role's most cost per run; null until the CLI has reported any.
    /// Claude Code, Copilot and OpenCode report each model call as it ends; Codex only when its turn (the whole run)
    /// ends, so a run can't be stopped early on what it costs.
    /// </summary>
    Usage? UsageSoFar => null;

    /// <summary>Allowance windows reported by this run, when available.</summary>
    MessageBus.Contracts.Runner.AgentCapacityReport? Capacity => null;
}

/// <summary>How to start one kind of agent CLI headless, and how to read what it prints.</summary>
public interface IAgentAdapter
{
    /// <summary>The kind.</summary>
    AgentKind Kind { get; }

    /// <summary>The command line for one run, with the role's permission profile applied as the CLI's own flags.</summary>
    AgentLaunch BuildLaunch(AgentLaunchContext context);

    /// <summary>A parser for one run's output.</summary>
    IAgentOutputParser CreateParser();

    /// <summary>
    /// Whether the CLI reports usage as each model call ends (<see cref="IAgentOutputParser.UsageSoFar"/>), so a role's
    /// most cost per run can stop it early (the original issue). Codex reports only when its turn, the whole run, ends.
    /// </summary>
    bool ReportsUsageAsItGoes => false;

    /// <summary>A parser for a run started with <paramref name="model"/> (the original issue), for CLIs whose output doesn't name the model.</summary>
    IAgentOutputParser CreateParser(string? model) => CreateParser();

    /// <summary>Whether the CLI can continue an earlier session headless (<see cref="AgentLaunchContext.ResumeSessionId"/>).</summary>
    bool CanResume => false;
}

/// <summary>Output parser for CLIs that print plain text: the last non-empty line is the final message.</summary>
public sealed class PlainTextOutputParser(AgentKind kind, string? vendor) : IAgentOutputParser
{
    private string? _last;

    /// <inheritdoc />
    public string? Observe(string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            _last = line.Trim();
        }
        return null;
    }

    /// <inheritdoc />
    public AgentOutcome Complete() => new() { FinalText = _last, Usage = new Usage { AgentKind = kind, Vendor = vendor } };
}

/// <summary>Small helpers the adapters share.</summary>
internal static class AgentText
{
    /// <summary>One line, at most <paramref name="max"/> characters.</summary>
    public static string? OneLine(string? text, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }
}
