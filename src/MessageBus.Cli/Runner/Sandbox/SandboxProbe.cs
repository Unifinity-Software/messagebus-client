using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>An agent CLI whose <c>--version</c> the start-up check runs inside the sandbox.</summary>
/// <param name="Name">How the Run isolation line names it (<c>claude</c>).</param>
/// <param name="Executable">Full path of the command the runner starts for it.</param>
public sealed record SandboxProbeAgent(string Name, string Executable);

/// <summary>One command of the sandbox's start-up check.</summary>
/// <param name="Step">What it checks, as the Run isolation line names it.</param>
/// <param name="Launch">What to start: inside the sandbox when <paramref name="Sandboxed"/>.</param>
/// <param name="Unwrapped">The same command without the sandbox.</param>
/// <param name="Sandboxed">Whether <paramref name="Launch"/> starts inside the sandbox.</param>
internal sealed record ProbeCommand(string Step, ProcessLaunch Launch, ProcessLaunch Unwrapped, bool Sandboxed);

/// <summary>How a check command ended: its exit code and its output (stdout and stderr together).</summary>
internal sealed record ProbeOutcome(int ExitCode, string Output);

/// <summary>Runs the start-up check's commands (real processes; tests script the answers).</summary>
internal interface IProbeRunner
{
    ProbeOutcome Run(ProbeCommand command);
}

/// <summary>An OS sandbox the start-up check can run commands in: macOS's sandbox-exec or Linux's bubblewrap (the original issue).</summary>
internal interface IOsSandbox
{
    /// <summary>Writes whatever the run's sandbox reads from its run directory (the macOS profile).</summary>
    void WriteProfile(string runRoot, bool limitNetwork = false);

    /// <summary>Whether this sandbox can block connections that go around the run's proxy (the original issue), so the check tries one.</summary>
    bool ChecksNetwork { get; }

    /// <summary><paramref name="launch"/>, started inside the run's sandbox.</summary>
    ProcessLaunch Wrap(ProcessLaunch launch, string runRoot);

    /// <summary>A command that starts a sandbox of this kind and does nothing: whether one may start inside another.</summary>
    IReadOnlyList<string> NestingCheck { get; }
}

/// <summary>What the start-up check found.</summary>
/// <param name="Problem">Why the sandbox can't be used (names the step that failed); null when it can.</param>
/// <param name="NestingAllowed">Whether a run may start its own sandbox inside this one (Codex does).</param>
/// <param name="Notes">Steps skipped because they fail outside the sandbox too (a tool that's broken on this machine).</param>
/// <param name="NetworkEnforced">Whether the sandbox was seen to refuse a connection off this computer (the original issue); false when not checked.</param>
internal sealed record ProbeResult(string? Problem, bool NestingAllowed, IReadOnlyList<string> Notes, bool NetworkEnforced = false);

/// <summary>
/// The OS sandbox's start-up check (the original issue; Linux too, the original issue): before any task, the runner makes a run directory the
/// way a task gets one, writes its profile and git settings, and inside the sandbox checks the
/// sandbox's own rules (the run can write its folder and read its git settings; it can't read or
/// write another run's folder), then <c>git --version</c>, <c>git config --list --show-origin</c>
/// (it must see the run's git settings), a <c>git clone</c> of a tiny bundle into the run folder,
/// and <c>&lt;agent&gt; --version</c> for each agent. The first failure is the problem. A step that
/// fails outside the sandbox too is skipped: that's the tool, not the sandbox.
/// </summary>
internal static class SandboxProbe
{
    public const string RulesStep = "sandbox rules";
    public const string GitVersionStep = "git --version";
    public const string GitConfigStep = "git config --list --show-origin";
    public const string PrepareBundleStep = "prepare a test bundle";
    public const string GitCloneStep = "git clone of a test bundle";
    public const string NetworkStep = "network limit";

    /// <summary>An address off this computer the network check tries (Cloudflare's DNS service, HTTPS port).</summary>
    internal const string OffMachineAddress = "1.1.1.1";

    /// <summary>What <see cref="GitConfigStep"/> must print: a setting only the run's own git settings have.</summary>
    public const string ConfigMarker = "mbus.probe=run-gitconfig";

    private const string RulesScript = """
        echo ok > "$1/work/probe" || exit 10
        cat "$1/gitconfig" >/dev/null || exit 13
        cat "$2/secret" >/dev/null 2>&1 && exit 11
        (echo x > "$2/new") 2>/dev/null && exit 12
        shift 2
        "$@" 2>/dev/null || exit 20
        exit 0
        """;

    /// <summary>Runs the check for <paramref name="sandbox"/> in <paramref name="probeRoot"/> (made and filled here; the caller deletes it).</summary>
    public static ProbeResult Run(IOsSandbox sandbox, RunIsolationOptions options, IProbeRunner runner, string probeRoot)
    {
        var run = Directory.CreateDirectory(Path.Combine(probeRoot, "run")).FullName;
        Directory.CreateDirectory(Path.Combine(run, "work"));
        var temp = Directory.CreateDirectory(Path.Combine(run, "tmp")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(probeRoot, "other")).FullName;
        File.WriteAllText(Path.Combine(other, "secret"), "not for this run");

        // The same git settings and environment a task's run gets (push turned off: the stricter settings).
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in options.ToolVariables)
        {
            variables[name] = value;
        }
        foreach (var (name, value) in RunGitConfig.Write(run, new GitIdentity("mbus start-up check", "probe@messagebus.invalid"), null, null, canPush: false))
        {
            variables[name] = value;
        }
        File.AppendAllText(Path.Combine(run, RunGitConfig.ConfigFileName), "[mbus]\n\tprobe = run-gitconfig\n");
        variables["TMPDIR"] = temp;
        variables["TMP"] = temp;
        variables["TEMP"] = temp;
        var environment = RunEnvironmentScrubber.Build(Environment.GetEnvironmentVariables(), options.PassEnvironment, variables);
        sandbox.WriteProfile(run);

        ProcessLaunch Launch(string file, IReadOnlyList<string> args, string? cwd = null) =>
            new() { FileName = file, Arguments = args, WorkingDirectory = cwd ?? run, Environment = environment };

        var notes = new List<string>();

        // 1. The rules themselves.
        var rules = Launch("/bin/sh", ["-c", RulesScript.ReplaceLineEndings("\n"), "sh", run, other, .. sandbox.NestingCheck]);
        var rulesOutcome = runner.Run(new ProbeCommand(RulesStep, sandbox.Wrap(rules, run), rules, Sandboxed: true));
        var nesting = rulesOutcome.ExitCode == 0;
        switch (rulesOutcome.ExitCode)
        {
            case 0 or 20:
                break;
            case 11 or 12:
                return Failed($"its start-up check \"{RulesStep}\" found that a run could {(rulesOutcome.ExitCode == 11 ? "read" : "write")} another run's folder", nesting, notes);
            case 10 or 13:
                return Failed($"its start-up check \"{RulesStep}\" found that a run can't {(rulesOutcome.ExitCode == 10 ? "write in its own folder" : "read its own git settings")} ({Detail(rulesOutcome)})", nesting, notes);
            default:
                return Failed($"its start-up check \"{RulesStep}\" couldn't start the sandbox ({Detail(rulesOutcome)})", nesting, notes);
        }

        // 2-4. git, as a task's checkout uses it.
        if (options.Git is { } git)
        {
            var gitWorks = Step(GitVersionStep, Launch(git, ["--version"]), null, o => o.ExitCode == 0, out var problem);
            if (problem is not null)
            {
                return Failed(problem, nesting, notes);
            }
            if (gitWorks)
            {
                Step(GitConfigStep, Launch(git, ["config", "--list", "--show-origin"]), null,
                    o => o.ExitCode == 0 && o.Output.Contains(ConfigMarker, StringComparison.Ordinal), out problem);
                if (problem is not null)
                {
                    return Failed(problem, nesting, notes);
                }
                var bundle = Path.Combine(run, "probe.bundle");
                if (PrepareBundle(git, probeRoot, bundle, out var why))
                {
                    Step(GitCloneStep,
                        Launch(git, ["clone", "--quiet", bundle, Path.Combine(run, "work", "clone")]),
                        Launch(git, ["clone", "--quiet", bundle, Path.Combine(run, "work", "clone-outside")]),
                        o => o.ExitCode == 0, out problem);
                    if (problem is not null)
                    {
                        return Failed(problem, nesting, notes);
                    }
                }
                else
                {
                    notes.Add($"The start-up check skipped \"{GitCloneStep}\": {why}.");
                }
            }
        }

        // 5. Each agent CLI starts.
        foreach (var agent in options.ProbeAgents)
        {
            Step($"{agent.Name} --version", Launch(agent.Executable, ["--version"], Path.Combine(run, "work")), null, o => o.ExitCode == 0, out var problem);
            if (problem is not null)
            {
                return Failed(problem, nesting, notes);
            }
        }

        // 6. the original issue: with the network limit, a connection off this computer is refused by the sandbox itself.
        var networkEnforced = false;
        if (options.LimitNetwork && sandbox.ChecksNetwork)
        {
            sandbox.WriteProfile(run, limitNetwork: true);
            var connect = Launch("/usr/bin/nc", ["-z", "-G", "3", OffMachineAddress, "443"]);
            var outcome = runner.Run(new ProbeCommand(NetworkStep, sandbox.Wrap(connect, run), connect, Sandboxed: true));
            if (outcome.Output.Contains("not permitted", StringComparison.OrdinalIgnoreCase))
            {
                networkEnforced = true;
            }
            else if (outcome.ExitCode == 0)
            {
                notes.Add($"The start-up check \"{NetworkStep}\" reached {OffMachineAddress} from inside the sandbox, so this Mac doesn't enforce the network limit.");
            }
            else
            {
                notes.Add($"The start-up check \"{NetworkStep}\" couldn't tell whether this Mac enforces the network limit ({Detail(outcome)}); it is treated as not enforced.");
            }
        }
        return new ProbeResult(null, nesting, notes, networkEnforced);

        // True when the step passed inside the sandbox; false when it was skipped; problem set when the sandbox broke it.
        bool Step(string step, ProcessLaunch inner, ProcessLaunch? outsideLaunch, Func<ProbeOutcome, bool> ok, out string? problem)
        {
            problem = null;
            var inside = runner.Run(new ProbeCommand(step, sandbox.Wrap(inner, run), inner, Sandboxed: true));
            if (ok(inside))
            {
                return true;
            }
            var outsideCommand = outsideLaunch ?? inner;
            var outside = runner.Run(new ProbeCommand(step, outsideCommand, outsideCommand, Sandboxed: false));
            if (!ok(outside))
            {
                notes.Add($"The start-up check skipped \"{step}\": it fails outside the sandbox too ({Detail(outside)}).");
                return false;
            }
            problem = $"its start-up check \"{step}\" failed inside the sandbox ({Detail(inside)})";
            return false;
        }

        bool PrepareBundle(string gitPath, string root, string bundlePath, out string why)
        {
            var source = Path.Combine(root, "source");
            foreach (var args in new IReadOnlyList<string>[]
            {
                ["-c", "init.defaultBranch=main", "init", "--quiet", source],
                ["-C", source, "commit", "--quiet", "--allow-empty", "-m", "mbus start-up check"],
                ["-C", source, "bundle", "create", bundlePath, "--all"],
            })
            {
                var command = Launch(gitPath, args, root);
                var outcome = runner.Run(new ProbeCommand(PrepareBundleStep, command, command, Sandboxed: false));
                if (outcome.ExitCode != 0)
                {
                    why = $"git {args[2]} failed outside the sandbox ({Detail(outcome)})";
                    return false;
                }
            }
            why = "";
            return true;
        }
    }

    private static ProbeResult Failed(string problem, bool nesting, IReadOnlyList<string> notes) => new(problem, nesting, notes);

    /// <summary>The exit code and the last line of output, short.</summary>
    internal static string Detail(ProbeOutcome outcome)
    {
        var last = outcome.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (last is { Length: > 200 })
        {
            last = last[..200] + "…";
        }
        return last is null ? $"exit {outcome.ExitCode}" : $"exit {outcome.ExitCode}: {last}";
    }
}

/// <summary>Runs check commands as real processes, each with a time limit.</summary>
internal sealed class ProcessProbeRunner : IProbeRunner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public ProbeOutcome Run(ProbeCommand command)
    {
        var launch = command.Launch;
        var info = new ProcessStartInfo(launch.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = launch.WorkingDirectory,
        };
        foreach (var arg in launch.Arguments)
        {
            info.ArgumentList.Add(arg);
        }
        foreach (var (name, value) in launch.Environment)
        {
            if (value is null)
            {
                info.Environment.Remove(name);
            }
            else
            {
                info.Environment[name] = value;
            }
        }
        var output = new StringBuilder();
        void Collect(object _, DataReceivedEventArgs e)
        {
            if (e.Data is { } line)
            {
                lock (output)
                {
                    output.Append(line).Append('\n');
                }
            }
        }
        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception ex)
        {
            return new ProbeOutcome(-1, $"couldn't start {launch.FileName}: {ex.Message}");
        }
        if (process is null)
        {
            return new ProbeOutcome(-1, $"couldn't start {launch.FileName}");
        }
        using (process)
        {
            process.OutputDataReceived += Collect;
            process.ErrorDataReceived += Collect;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return new ProbeOutcome(-1, $"didn't finish in {Timeout.TotalSeconds:0} seconds");
            }
            process.WaitForExit();
            lock (output)
            {
                return new ProbeOutcome(process.ExitCode, output.ToString());
            }
        }
    }
}
