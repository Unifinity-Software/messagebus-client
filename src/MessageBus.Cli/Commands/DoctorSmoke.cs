using System.Globalization;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Machines;
using Spectre.Console;

namespace MessageBus.Cli.Commands;

/// <summary>
/// <c>mbus doctor --smoke</c> (the original issue): each agent a runner here would start (or the one asked for) does a tiny developer
/// task in a real run (<see cref="SmokeTest"/>), and the report says, step by step, whether it could. Everything happens
/// on this machine; nothing goes to the server.
/// </summary>
internal static class DoctorSmoke
{
    /// <summary>Runs the check; the exit code is 0 when every agent passed.</summary>
    public static async Task<int> RunAsync(CommandContext context, string? agentName, bool keep, CancellationToken ct)
    {
        var host = context.Host;
        var console = context.Console;
        AgentKind? only = null;
        if (agentName is { Length: > 0 })
        {
            if (!TryParseAgent(agentName, out var kind))
            {
                console.MarkupLineInterpolated($"[red]Unknown agent[/] {agentName}. Use one of: {string.Join(", ", AgentKinds.Launchable.Select(AgentKinds.Name))}.");
                return 1;
            }
            only = kind;
        }

        var scan = await AgentScanner.ScanMachineAsync(host, ct).ConfigureAwait(false);
        if (!scan.Git.Works)
        {
            console.MarkupLineInterpolated($"[red]not ready[/]  git: {scan.Git.Problem ?? "git doesn't work."}");
            return 1;
        }
        var git = await MacGitSetup.InstalledGitPathAsync(scan.Git.Path, ct).ConfigureAwait(false) ?? scan.Git.Path!;
        RunnerOptions options;
        Runner.Sandbox.RunIsolationOptions isolation;
        try
        {
            options = RunnerOptions.FromEnvironment(host.GetEnvironmentVariable, host.HomeDirectory, null);
            isolation = Runner.Sandbox.RunIsolationOptions.FromEnvironment(host.GetEnvironmentVariable, options.WorkRoot, host.HomeDirectory, git, context.Settings.MachineName);
        }
        catch (FormatException ex)
        {
            console.MarkupLineInterpolated($"[red]not ready[/]  run isolation: {ex.Message}");
            return 1;
        }

        // the original issue: one network proxy as a runner here has, on the port the runner keeps; while a runner holds that port,
        // one of its own on any free port (the original issue), so the check works with the runner running.
        using var sharedNetwork = Runner.Sandbox.RunNetworkProxy.UsedFor(isolation)
            ? Runner.Sandbox.RunNetworkProxy.StartStable(host.ConfigDirectory, TextWriter.Null, isolation.Upstream, moveIfTaken: false)
                ?? Runner.Sandbox.RunNetworkProxy.Start(0, TextWriter.Null, upstream: isolation.Upstream)
            : null;
        isolation = isolation with { SharedNetwork = sharedNetwork };
        var agents = Agents(scan, options, host, only);
        if (agents.Count == 0)
        {
            console.MarkupLine("[red]not ready[/]  agents: no coding agent is installed. Fix: install Claude Code, Codex, GitHub Copilot CLI or OpenCode, then mbus connect.");
            return 1;
        }

        // The run isolation a runner here starts with: its start-up check, then the first line it prints.
        var toolPaths = Readiness.ToolPaths.RunVariables(host.HomeDirectory, host.GetEnvironmentVariable);
        var said = new StringWriter();
        var sandbox = Runner.Sandbox.RunIsolation.Create(isolation with
        {
            ProbeAgents = [.. agents.Where(a => a.Executable is not null).Select(a => new Runner.Sandbox.SandboxProbeAgent(AgentKinds.Command(a.Kind), a.Executable!))],
            ToolVariables = toolPaths,
            // As with the runner's own mbus tools: runs can't read mbus's settings folder (macOS), so not the machine key.
            RunTools = true,
            SettingsDirectory = host.GetEnvironmentVariable(Hosting.EnvVars.Home) is { Length: > 0 } mbusHome ? Path.GetFullPath(mbusHome) : Path.Combine(host.HomeDirectory, ".mbus"),
            NetworkHosts = Runner.Sandbox.RunNetwork.BaseHosts(host.GetEnvironmentVariable, context.Settings.Server),
        }, said);
        if (said.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } isolationLine)
        {
            console.MarkupLineInterpolated($"[grey]{isolationLine}[/]");
        }

        var dotnet = scan.Tools.FirstOrDefault(t => t.Name == "dotnet" && t.Works)?.Path;
        var smoke = new SmokeTest(new SmokeTestOptions
        {
            WorkRoot = options.WorkRoot,
            Git = git,
            Dotnet = dotnet,
            Sandbox = new Readiness.ToolPathSandbox(sandbox, toolPaths),
            AgentEnvironment = context.Store.AgentEnvironment.Load,
            GetEnvironmentVariable = host.GetEnvironmentVariable,
            Keep = keep,
            Time = host.Time,
        });
        console.MarkupLineInterpolated($"[grey]Each agent changes one line, builds, tests and commits in a {(dotnet is null ? "small shell-script" : "small .NET")} project in a run folder under {options.WorkRoot}.[/]");

        // the original issue: advice names this mbus by its full path when the mbus on PATH is another one.
        var mbus = Hosting.MbusOnPath.For(host);
        var results = new List<SmokeResult>();
        foreach (var installed in agents)
        {
            var agent = sandbox.AgentRefusal(installed.Kind) is { } refusal ? installed with { NotReady = refusal } : installed;
            if (agent.NotReady is null)
            {
                console.MarkupLineInterpolated($"[grey]Starting {AgentKinds.DisplayName(agent.Kind)}…[/]");
            }
            var result = await smoke.RunAsync(agent, ct).ConfigureAwait(false);
            results.Add(result);
            Write(console, result with { NotReady = mbus.AdviceOrNull(result.NotReady) });
        }
        return Summary(console, results) ? 0 : 1;
    }

    private static string CodexHome(Hosting.CliHost host) =>
        host.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } c ? c : Path.Combine(host.HomeDirectory, ".codex");

    /// <summary>The agents a runner here would start: each one found, with why it wouldn't start one that isn't ready.</summary>
    internal static List<SmokeAgent> Agents(MachineScan scan, RunnerOptions options, Hosting.CliHost host, AgentKind? only)
    {
        var codexHome = CodexHome(host);
        var list = new List<SmokeAgent>();
        foreach (var kind in AgentKinds.Launchable.Where(k => only is null || k == only))
        {
            var found = scan.Agents.Select(a => a.Agent).FirstOrDefault(a => a.Kind == kind && a.Found);
            if (found is null)
            {
                if (only is not null)
                {
                    list.Add(new SmokeAgent(kind, null, null, "it isn't installed on this machine."));
                }
                continue;
            }
            if (found.NotReady is { } notReady)
            {
                list.Add(new SmokeAgent(kind, null, null, notReady));
                continue;
            }
            if (!found.SignedIn)
            {
                list.Add(new SmokeAgent(kind, null, null, "it doesn't look signed in. Fix: sign in to it on this machine, then mbus connect --rescan."));
                continue;
            }
            var executable = options.AgentCommands.TryGetValue(kind, out var configured)
                ? (Path.IsPathFullyQualified(configured) ? (File.Exists(configured) ? configured : null) : ExecutableLocator.Find(host, configured)?.Path)
                : found.ExecutablePath ?? ExecutableLocator.Find(host, AgentKinds.Command(kind))?.Path;
            if (executable is null)
            {
                list.Add(new SmokeAgent(kind, null, null, "its command wasn't found."));
                continue;
            }
            IAgentAdapter adapter = kind switch
            {
                AgentKind.Codex => new CodexAdapter(CodexAdapter.ReadConfiguredModel(codexHome), Connect.CodexTomlConfig.HasMbus(Path.Combine(codexHome, "config.toml")), codexHome: codexHome, getEnvironmentVariable: host.GetEnvironmentVariable),
                AgentKind.Copilot => new CopilotAdapter(copilotHome: host.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } h ? h : Path.Combine(host.HomeDirectory, ".copilot")),
                AgentKind.OpenCode => OpenCodeAdapter.ForRunner(found.Version, host.GetEnvironmentVariable, host.HomeDirectory, host.CreateLocalModelHttpHandler,
                    () => Runner.Service.AgentEnvironmentStore.ReadFrom(host.ConfigDirectory)),
                _ => new ClaudeCodeAdapter(),
            };
            list.Add(new SmokeAgent(kind, adapter, executable));
        }
        return list;
    }

    /// <summary>One agent's lines: a heading, then one line per step (or why it wasn't started), then what it used.</summary>
    internal static void Write(IAnsiConsole console, SmokeResult result)
    {
        var name = AgentKinds.DisplayName(result.Kind);
        if (result.NotReady is { } why)
        {
            console.MarkupLineInterpolated($"[red]not run[/]  {name}: {why}");
            return;
        }
        console.MarkupLineInterpolated($"{name}:");
        foreach (var step in result.Steps)
        {
            if (step.Ok)
            {
                console.MarkupLineInterpolated($"  [green]ok[/]      {step.Name,-7} {step.Detail}");
            }
            else
            {
                console.MarkupLineInterpolated($"  [red]failed[/]  {step.Name,-7} {step.Detail}");
            }
        }
        // A step that wasn't reached (the run stopped before the checks) still gets its line.
        foreach (var missing in SmokeTest.StepNames.Skip(result.Steps.Count))
        {
            console.MarkupLineInterpolated($"  [grey]skipped[/] {missing,-7} not checked");
        }
        if (Usage(result.Usage) is { } used)
        {
            console.MarkupLineInterpolated($"  [grey]used    {used}[/]");
        }
        if (result.KeptFolder is { } folder)
        {
            console.MarkupLineInterpolated($"  [grey]kept    {folder} (for a day)[/]");
        }
    }

    /// <summary>The closing line; true when every agent passed.</summary>
    internal static bool Summary(IAnsiConsole console, IReadOnlyList<SmokeResult> results)
    {
        var passed = results.Count(r => r.Passed);
        console.WriteLine();
        if (passed == results.Count)
        {
            console.MarkupLineInterpolated($"[green]{(results.Count == 1 ? "The agent" : $"All {results.Count} agents")} can edit, build, test and commit here.[/]");
            return true;
        }
        var failed = results.Where(r => !r.Passed).Select(r => AgentKinds.DisplayName(r.Kind));
        console.MarkupLineInterpolated($"[red]{passed} of {results.Count} agents passed.[/] Not ready for developer work here: {string.Join(", ", failed)}. Fix what failed, then run mbus doctor --smoke again (--keep keeps the run folders to look at).");
        return false;
    }

    /// <summary>What the run cost, as far as the agent said: the estimated cost, else the tokens.</summary>
    internal static string? Usage(Contracts.Messaging.Usage? usage)
    {
        if (usage is null)
        {
            return null;
        }
        var tokens = usage.TokensIn > 0 || usage.TokensOut > 0
            ? $"{usage.TokensIn.ToString("N0", CultureInfo.InvariantCulture)} tokens in, {usage.TokensOut.ToString("N0", CultureInfo.InvariantCulture)} out"
            : null;
        return usage.EstimatedCostUsd is { } cost
            ? $"about ${cost.ToString("0.00", CultureInfo.InvariantCulture)}" + (tokens is null ? "" : $" ({tokens})")
            : tokens;
    }

    private static bool TryParseAgent(string text, out AgentKind kind)
    {
        if (AgentKinds.TryParse(text, out kind) && AgentKinds.Launchable.Contains(kind))
        {
            return true;
        }
        // The command names too: claude, codex, copilot, opencode.
        foreach (var candidate in AgentKinds.Launchable)
        {
            if (string.Equals(AgentKinds.Command(candidate), text, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }
        return false;
    }
}
