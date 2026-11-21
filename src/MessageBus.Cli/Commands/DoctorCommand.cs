using System.CommandLine;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Output;
using MessageBus.Cli.Readiness;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Machines;
using Spectre.Console;

namespace MessageBus.Cli.Commands;

/// <summary>
/// <c>mbus doctor</c>: every check a runner makes before it takes work, in one place, with what to do
/// about each problem (the original issue, the original issue, the original issue): git works (not macOS's placeholder), each agent can use its
/// sign-in from this session, this machine is signed in to messagebus.ai, and which toolchains tasks
/// can use. It runs version and status commands, never a model. Anonymous discovery may persist a
/// restrictive authority pin before any credential is sent; it never enrolls or changes key trust.
/// It starts with what a runner here would start with (the original issue: version, server, workspace, run isolation, network,
/// updates) and the server's view of this machine (<see cref="DoctorServerView"/>), so a machine the server thinks
/// is offline can tell.
/// Exit code 0 when the machine can take tasks, 1 when something needs fixing first.
/// </summary>
internal static class DoctorCommand
{
    public static Command Create(Hosting.CliHost host, CommonOptions options)
    {
        var command = new Command("doctor", "Check that this machine can take tasks, and say how to fix what it can't.");
        var smoke = new Option<bool>("--smoke")
        {
            Description = "Also have each agent change a line, build, test and commit in a small project here, as a task would. "
                + "This runs each agent once, so it uses some of its tokens.",
        };
        var agent = new Option<string?>("--agent") { Description = "With --smoke: test only this agent (claude-code, codex, copilot or opencode)." };
        var keep = new Option<bool>("--keep") { Description = "With --smoke: keep each test's run folder for a day and print where it is." };
        command.Options.Add(smoke);
        command.Options.Add(agent);
        command.Options.Add(keep);
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.Load(host, parse, options);
            if (parse.GetValue(smoke))
            {
                // the original issue: on this machine only, without asking the server anything.
                return await DoctorSmoke.RunAsync(context, parse.GetValue(agent), parse.GetValue(keep), ct);
            }
            var asking = DoctorServerView.AskAsync(context, ct);
            var keyCheck = WorkspaceKeyReadiness.CheckAsync(context, ct);
            var scan = await AgentScanner.ScanMachineAsync(host, ct);
            var sleep = await host.SleepSettings(ct);
            return Report(context, scan, await asking, sleep, await keyCheck) ? 0 : 1;
        });
        return command;
    }

    /// <summary>The rows a runner here would start with, and the server's view of this machine.</summary>
    private static List<SummaryRow> Summary(CommandContext context, DoctorServerView server)
    {
        var host = context.Host;
        var settings = context.Settings;
        var rows = RunnerFacts.Identity(Hosting.CliHost.Version, settings.Server, settings.MachineName, settings.Location,
            server.WorkspaceName ?? settings.Stored.WorkspaceName ?? settings.Stored.WorkspaceId).ToList();
        if (settings.Server is { } selected)
        {
            rows.Add(new("Selected API endpoint", ServerEndpoint.Parse(selected.AbsoluteUri).Value));
            rows.Add(new("Transport origin", ServerEndpoint.Parse(selected.AbsoluteUri).Origin));
            if (settings.Stored.ServerBinding is { } saved)
                rows.Add(new("Saved credential authority", $"{saved.Binding.Issuer} (stamp {saved.Binding.StampId}); endpoint {saved.SelectedEndpoint}"));
            if (server.PersistedBinding is { } pin)
                rows.Add(new("Persisted origin pin", $"{pin.Issuer} (stamp {pin.StampId})"));
            if (host.GetEnvironmentVariable(ServerBindingExpectation.EnvironmentVariable) is { } expectedJson)
            {
                try
                {
                    var expected = ServerBindingExpectation.Parse(expectedJson);
                    rows.Add(new("Expected authority", $"{expected.Binding.Issuer} (stamp {expected.Binding.StampId}); transport {expected.TransportOrigin}"));
                }
                catch (ServerBindingException) { rows.Add(new("Expected authority", "Invalid local binding configuration.", SummaryTone.Bad)); }
            }
            if (server.PreparedBinding is { } prepared)
                rows.Add(new("Validated authority", $"{prepared.Binding.Issuer} (stamp {prepared.Binding.StampId}); signature v2"));
            rows.Add(new("Reported hosting", server.Discovery?.Binding is { } binding
                ? $"{binding.Issuer} (stamp {binding.StampId}); geography {server.Discovery.Info.Stamp?.Geography}; Azure region {server.Discovery.Info.Stamp?.AzureRegion} (server-reported)"
                : "Unknown/custom: no validated isolated authority. A tunnel or server name does not establish a region."));
            rows.Add(new("Execution location", settings.Location is { } location ? $"{location} (runner label)" : "No runner location label."));
        }
        rows.Add(new SummaryRow("Machine key", context.Store.KeyVault.Description + (context.Store.KeyStoreName == "file" ? ". Programs running as you can read it without Keychain protection." : "")));
        rows.AddRange(server.Rows());
        try
        {
            var options = RunnerOptions.FromEnvironment(host.GetEnvironmentVariable, host.HomeDirectory);
            var isolation = RunIsolationOptions.FromEnvironment(host.GetEnvironmentVariable, options.WorkRoot, host.HomeDirectory, null, settings.MachineName);
            rows.Add(RunnerFacts.PlannedIsolation(isolation));
            rows.Add(RunnerFacts.Network(null, isolation));
        }
        catch (FormatException ex)
        {
            // A setting the runner would refuse to start with.
            rows.Add(new("Run isolation", ex.Message, SummaryTone.Bad, "the runner won't start:"));
        }
        var said = new StringWriter();
        var updater = Update.RunnerUpdateSetup.TryCreate(host, settings, said);
        rows.Add(RunnerFacts.Updates(updater is not null, said.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), server.Releases));
        return rows;
    }

    private static bool Report(CommandContext context, MachineScan scan, DoctorServerView view, string? sleep, WorkspaceKeyReadiness.CheckResult keyCheck)
    {
        var console = context.Console;
        var env = context.Host.GetEnvironmentVariable;
        var ready = true;
        // the original issue: advice names this mbus by its full path when the mbus on PATH is another one.
        var mbus = Hosting.MbusOnPath.For(context.Host);
        void Good(string what, string detail) => console.MarkupLineInterpolated($"[green]ready[/]      {what}: {mbus.Advice(detail)}");
        void Bad(string what, string problem)
        {
            ready = false;
            console.MarkupLineInterpolated($"[red]not ready[/]  {what}: {mbus.Advice(problem)}");
        }
        void Note(string what, string detail) => console.MarkupLineInterpolated($"[yellow]note[/]       {what}: {mbus.Advice(detail)}");

        SummaryView.Write(console, "mbus doctor", Summary(context, view));
        if (mbus.Warning() is { } other)
        {
            Note("mbus", other);
        }

        if (sleep is not null) Note("Mac sleep", sleep);
        if ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) &&
            !string.Equals(env(SandboxEnvVars.Isolation)?.Trim(), "off", StringComparison.OrdinalIgnoreCase))
            Note(".NET builds", "each run starts its own build processes, so runs on this computer don't share them.");
        if (keyCheck.Problem is { } keyProblem)
        {
            if (keyCheck.CouldNotCheck) Note("Workspace key", keyProblem);
            else Bad("Workspace key", keyProblem);
        }

        foreach (var capacity in context.Store.AgentCapacity.Describe(context.Host.Time.GetUtcNow()))
            console.MarkupLineInterpolated($"{capacity}");

        // This machine and messagebus.ai.
        if ((view.BindingRefused || view.Suspended) && view.Problem is { } serverProblem)
        {
            Bad("mbus server", serverProblem);
        }
        else if (context.Settings.Server is { } server && context.Settings.CanAuthenticate(context.Store))
        {
            Good("mbus", $"signed in to {CommandContext.ServerText(server)}{(context.Settings.Stored.MachineId is { } id ? $" as {id}" : "")}");
        }
        else
        {
            Bad("mbus", "this machine isn't signed in. Fix: mbus login --server <address>.");
        }

        // git: every task with a repository needs it.
        if (scan.Git.Works)
        {
            Good("git", $"{scan.Git.Version} ({scan.Git.Path})");
        }
        else
        {
            Bad("git", scan.Git.Problem ?? "git doesn't work.");
        }

        // Agents: installed, signed in from this session, with the mbus tools.
        var found = scan.Agents.Where(a => a.Agent.Found).ToList();
        if (found.Count == 0)
        {
            Bad("agents", "no coding agent is installed. Fix: install Claude Code, Codex, GitHub Copilot CLI or OpenCode, then mbus connect.");
        }
        foreach (var (agent, warning) in found)
        {
            var name = AgentScanner.DisplayName(agent.Kind);
            if (OperatingSystem.IsWindows() && agent.Kind == AgentKind.Codex)
            {
                try { Note(name, CodexAdapter.WindowsModeDescription(RunnerOptions.ParseCodexSandbox(env(CodexAdapter.SandboxOverrideVariable)))); }
                catch (FormatException ex) { Bad(name, ex.Message); }
            }
            // git's problem is on every agent (it is said once above); this is the agent's own.
            var own = agent.NotReady is { } p && (scan.Git.Works || p != scan.Git.Problem) ? p : null;
            if (own is not null)
            {
                Bad(name, own);
            }
            else if (!agent.SignedIn)
            {
                Bad(name, "it doesn't look signed in. Fix: sign in to it on this machine, then mbus connect --rescan.");
            }
            else
            {
                Good(name, $"{agent.Version ?? "version unknown"}, {(agent.Kind == AgentKind.OpenCode ? "provider available" : "signed in")}");
            }
            if (warning is not null)
            {
                Note(name, warning);
            }
            if (!agent.McpConfigured && agent.Kind == AgentKind.ClaudeCode)
            {
                Bad(name, "it doesn't have the mbus tools, so its tasks can't report back. Fix: mbus connect claude.");
            }
            else if (!agent.McpConfigured && agent.Kind == AgentKind.Codex)
            {
                Note(name, "its own settings don't have the mbus tools (the runner gives them to its tasks). For your own Codex sessions: mbus connect codex.");
            }
        }
        if (found.Count > 0 && found.All(a => a.Agent.NotReady is not null || !a.Agent.SignedIn))
        {
            ready = false;
        }

        // the original issue: the agent sign-ins stored for the runner service (names only, never values).
        var stored = context.Store.AgentEnvironment.Load().Keys.ToList();
        if (stored.Count > 0)
        {
            Good("runner service", $"keeps {string.Join(", ", stored)} for its agents (change them with mbus runner install or mbus runner env)");
        }
        else if (Runner.Service.RunnerServiceDefinitions.IsInstalled(context.Host.HomeDirectory, context.Store.Directory))
        {
            Note("runner service", "no agent sign-ins are stored for it, so its agents use only their own saved sign-ins. "
                + $"If they can't sign in there, set {SignInChecks.ClaudeTokenVariable} (from claude setup-token) or the agent's API key, then mbus runner install again.");
        }

        // Toolchains: reported as tool:<name>, which roles can require.
        console.WriteLine();
        console.MarkupLine("Toolchains tasks can use (roles can require them as tool:<name>):");
        foreach (var tool in scan.Tools.Where(t => t.Name != MachineTools.Git))
        {
            if (tool.Works)
            {
                var versions = tool.Details.Count > 1 ? string.Join(", ", tool.Details) : tool.Version;
                console.MarkupLineInterpolated($"  [green]{tool.Name,-7}[/] {tool.DisplayName} {versions}");
            }
            else if (tool.Problem is { } problem)
            {
                console.MarkupLineInterpolated($"  [yellow]{tool.Name,-7}[/] {problem}");
            }
        }
        var missing = scan.Tools.Where(t => !t.Found).Select(t => t.Name).ToList();
        if (missing.Count > 0)
        {
            console.MarkupLineInterpolated($"  [grey]not found: {string.Join(", ", missing)}[/]");
        }

        var runPath = ToolPaths.RunVariables(context.Host.HomeDirectory, env);
        if (runPath.TryGetValue("PATH", out var path))
        {
            var added = path.Split(Path.PathSeparator).Except((env("PATH") ?? "").Split(Path.PathSeparator));
            console.MarkupLineInterpolated($"  [grey]mbus runner adds these folders to each task's PATH: {string.Join(", ", added)}{(runPath.ContainsKey("DOTNET_ROOT") ? " (and sets DOTNET_ROOT)" : "")}[/]");
        }

        console.WriteLine();
        console.MarkupLine(ready
            ? $"[green]This machine can take tasks.[/] Start it with {Markup.Escape(mbus.Advice("mbus runner"))}."
            : $"[red]Fix the problems above[/], then run {Markup.Escape(mbus.Advice("mbus doctor"))} again. A runner that is already running picks up fixes when it restarts or rescans ({Markup.Escape(mbus.Advice("mbus connect"))} --rescan).");
        return ready;
    }
}
