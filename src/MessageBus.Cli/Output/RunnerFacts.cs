using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Contracts.Machines;

namespace MessageBus.Cli.Output;

/// <summary>
/// The rows <c>mbus runner</c>'s start-up summary and <c>mbus doctor</c> share (the original issue), each short enough for one line of
/// an 80-column terminal where it can be. The full wording stays where it was: the runner's plain log lines and the
/// Runners page.
/// </summary>
public static class RunnerFacts
{
    /// <summary>Version, server, machine (and location) and workspace.</summary>
    public static IEnumerable<SummaryRow> Identity(string version, Uri? server, string machineName, string? location, string? workspace)
    {
        yield return new("Version", version);
        if (Hosting.CliHost.SourceRevision is { } revision)
            yield return new("Source revision", revision);
        yield return server is null
            ? new("Server", "not signed in", SummaryTone.Bad)
            : new("Server", server.AbsoluteUri.TrimEnd('/'));
        yield return new("Machine", location is { Length: > 0 } ? $"{machineName} ({location})" : machineName);
        if (workspace is { Length: > 0 })
        {
            yield return new("Workspace", workspace);
        }
    }

    /// <summary>Tasks at once now, and the most the dashboard can set.</summary>
    public static SummaryRow Slots(int current, int ceiling) =>
        new("Tasks at once", $"{current} now; the dashboard can set up to {ceiling}");

    /// <summary>What run isolation the runner got (<paramref name="sandbox"/>) and why it isn't stronger.</summary>
    public static SummaryRow Isolation(IRunSandbox sandbox, RunIsolationOptions options) => sandbox switch
    {
        RunIsolation { AciProblem: { } why } => new("Run isolation", why, SummaryTone.Bad, "ACI tasks blocked:"),
        RunIsolation { Level: IsolationLevel.OsSandbox } os => new("Run isolation", os.SandboxName ?? "the OS sandbox", SummaryTone.Good, "OS sandbox:"),
        RunIsolation { SandboxProblem: { } why } basic => new("Run isolation", $"the {basic.SandboxName} isn't used: {why}", SummaryTone.Note, "basic:"),
        RunIsolation => new("Run isolation", WhyBasic(options.Requested), SummaryTone.Note, "basic:"),
        _ => new("Run isolation", $"runs use this machine's environment and git settings ({SandboxEnvVars.Isolation}=off)", SummaryTone.Bad, "off:"),
    };

    /// <summary>The run isolation the owner asked for, before the runner's start-up check (<c>mbus doctor</c>).</summary>
    public static SummaryRow PlannedIsolation(RunIsolationOptions options) => options.RequireAciSandbox
        ? new("Run isolation", "required; tasks are refused if the protected launch check fails", SummaryTone.Plain, "ACI sandbox:")
        : options.Requested switch
    {
        IsolationLevel.Off => new("Run isolation", $"runs use this machine's environment and git settings ({SandboxEnvVars.Isolation}=off)", SummaryTone.Bad, "off:"),
        IsolationLevel.OsSandbox when !OperatingSystem.IsWindows() =>
            new("Run isolation", "the OS sandbox, if it passes the runner's start-up check (mbus runner shows which)", SummaryTone.Plain, "OS sandbox:"),
        _ => new("Run isolation", WhyBasic(options.Requested), SummaryTone.Note, "basic:"),
    };

    private static string WhyBasic(IsolationLevel requested) =>
        requested == IsolationLevel.Basic ? $"set by {SandboxEnvVars.Isolation}=basic"
        : OperatingSystem.IsWindows() ? "Windows has no sandbox mbus can use without admin rights"
        : "this OS has no sandbox mbus can use";

    /// <summary>How far each run's network is limited.</summary>
    public static SummaryRow Network(IRunSandbox? sandbox, RunIsolationOptions options)
    {
        if (options.RequireAciSandbox)
        {
            return sandbox switch
            {
                null => new("Network", "private task namespace and an approved public IPv4 proxy, if the launch check passes", SummaryTone.Plain),
                RunIsolation { AciProblem: not null } => new("Network", "protected ACI launch unavailable", SummaryTone.Bad, "tasks blocked:"),
                _ => new("Network", "private task namespace; proxy only to approved public IPv4 destinations", SummaryTone.Good, "limited:"),
            };
        }
        if (options.Requested == IsolationLevel.Off || sandbox is NoSandbox)
        {
            return new("Network", "run isolation is off", SummaryTone.Bad, "not limited:");
        }
        if (!options.LimitNetwork)
        {
            return new("Network", $"{SandboxEnvVars.Network}=open", SummaryTone.Note, "not limited:");
        }
        var through = options.Upstream is { } upstream ? $", then through {upstream.Describe()}" : "";
        if (sandbox is RunIsolation { NetworkEnforced: true })
        {
            return new("Network", $"each run reaches only its allow-list (enforced by the macOS sandbox){through}", SummaryTone.Good, "limited:");
        }
        var os = OperatingSystem.IsWindows() ? "Windows can't force programs through it without admin rights"
            : OperatingSystem.IsMacOS() ? "without the macOS sandbox nothing forces programs through it"
            : "this OS can't force programs through it without root";
        return new("Network", $"each run gets an allow-list proxy{through}; {os}", SummaryTone.Note, "limited:");
    }

    /// <summary>
    /// Whether the runner updates itself: <paramref name="lines"/> are what the update setup said (only when it is off,
    /// or goes through dotnet), <paramref name="channel"/> the workspace's release channel when known.
    /// </summary>
    public static SummaryRow Updates(bool on, IReadOnlyList<string> lines, Contracts.Releases.RunnerReleasePolicy? channel)
    {
        var said = string.Join(" ", lines.Select(l => l.Trim()).Select(l => l.StartsWith("Updates:", StringComparison.Ordinal) ? l["Updates:".Length..].Trim() : l));
        if (!on)
        {
            return new("Updates", said.StartsWith("off.", StringComparison.Ordinal) ? said[4..].Trim() : said, SummaryTone.Note, "off:");
        }
        var where = channel is null ? "" : channel.PinnedVersion is { Length: > 0 } pinned ? $"pinned to {pinned}" : $"{channel.Channel} channel";
        var text = string.Join("; ", new[] { where, said }.Where(s => s.Length > 0));
        return new("Updates", text, SummaryTone.Good, text.Length > 0 ? "on:" : "on");
    }

    /// <summary>
    /// One row per installed agent: ready or not (with the reason and its fix), and its roles from the registry
    /// (<paramref name="registry"/>; null when the server wasn't asked or didn't answer).
    /// </summary>
    public static IEnumerable<SummaryRow> Agents(IEnumerable<DetectedAgent> agents, IReadOnlyList<AgentInfo>? registry)
    {
        var any = false;
        foreach (var agent in agents.Where(a => a.Found))
        {
            any = true;
            var name = AgentScanner.DisplayName(agent.Kind) + (agent.Version is { Length: > 0 } v ? " " + v : "");
            var roles = Roles(agent.Kind, registry) is { } held ? "· " + held : "";
            yield return agent.NotReady is { } problem
                ? new(name, roles.Length > 0 ? $"{roles}\n{problem}" : problem, SummaryTone.Bad, roles.Length > 0 ? "not ready" : "not ready:")
                : agent.Kind == AgentKind.Codex
                    ? new(name, roles, SummaryTone.Note, agent.SignedIn ? "model access unverified" : "credentials missing")
                    : new(name, roles, SummaryTone.Good, "ready");
        }
        if (!any)
        {
            yield return new("Agents", "none installed. Install Claude Code, Codex, GitHub Copilot CLI or OpenCode, then mbus connect.", SummaryTone.Bad);
        }
    }

    private static string? Roles(AgentKind kind, IReadOnlyList<AgentInfo>? registry)
    {
        if (registry is null)
        {
            return null;
        }
        var roles = registry.Where(a => a.Kind == kind).SelectMany(a => a.Roles).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return roles.Count == 0 ? "no roles yet" : $"roles: {string.Join(", ", roles)}";
    }

    /// <summary>The toolchains a scan found, as roles require them (<c>tool:&lt;name&gt;</c>).</summary>
    public static SummaryRow Toolchains(IEnumerable<string> capabilities)
    {
        var tools = capabilities.Where(c => c.StartsWith(CapabilityNames.ToolPrefix + ":", StringComparison.Ordinal))
            .Select(c => c[(CapabilityNames.ToolPrefix.Length + 1)..]).ToList();
        return tools.Count > 0 ? new("Toolchains", string.Join(", ", tools)) : new("Toolchains", "none found (git, dotnet, node, ...)", SummaryTone.Note);
    }

    /// <summary>The folders the runner adds to each task's PATH, or null when it adds none.</summary>
    public static SummaryRow? TaskPath(IReadOnlyDictionary<string, string> toolPaths, Func<string, string?> env)
    {
        if (!toolPaths.TryGetValue("PATH", out var runPath))
        {
            return null;
        }
        var added = runPath.Split(Path.PathSeparator).Except((env("PATH") ?? "").Split(Path.PathSeparator)).ToList();
        return added.Count == 0 ? null : new("Task PATH adds", string.Join(", ", added) + (toolPaths.ContainsKey("DOTNET_ROOT") ? " (and DOTNET_ROOT)" : ""));
    }
}
