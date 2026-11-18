using MessageBus.Cli.Readiness;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

/// <summary>
/// the original issue: the Codex command line a run actually starts, through the sandbox stack the runner builds (per-user tool folders
/// around run isolation, <see cref="RunnerCommand"/>): only the runner's own macOS sandbox may take Codex's away, and then
/// it holds the launch itself. No sandbox-exec runs here; the start-up check's answers are scripted.
/// </summary>
public sealed class CodexRunnerStackTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mbus-codex-stack-").FullName;

    public void Dispose() => RunWorkspace.TryDelete(_root);

    private static readonly PermissionProfile Developer = new() { Name = PermissionProfiles.Worker, CanEditFiles = true, Network = NetworkAccess.Open };

    public enum Machine { Mac, MacCheckFailed, Linux, Basic, Off }

    private IRunSandbox Stack(Machine machine)
    {
        var options = new RunIsolationOptions
        {
            WorkRoot = Path.Combine(_root, "runs"),
            HomeDirectory = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName,
            MachineName = "test-machine",
            Requested = machine switch { Machine.Basic => IsolationLevel.Basic, Machine.Off => IsolationLevel.Off, _ => IsolationLevel.OsSandbox },
        };
        // Exit 20: the sandbox works but can't start one inside itself (a real Mac); exit 1: it couldn't start.
        var rules = new Scripted(machine == Machine.MacCheckFailed ? 1 : 20);
        var isolation = machine == Machine.Linux
            ? RunIsolation.Create(options, TextWriter.Null, macProbe: null, linuxProbe: new Scripted(0), bubblewrap: "/usr/bin/bwrap")
            : RunIsolation.Create(options, TextWriter.Null, macProbe: machine is Machine.Mac or Machine.MacCheckFailed ? rules : null, linuxProbe: null);
        return new ToolPathSandbox(isolation, new Dictionary<string, string> { ["PATH"] = "/usr/bin" });
    }

    private ProcessLaunch Launch(Machine machine, PermissionProfile profile, string? resume = null,
        CodexWindowsSandbox codexOnWindows = CodexWindowsSandbox.Sandbox)
    {
        using var workspace = RunWorkspace.Create(Path.Combine(_root, "runs"), "dlv_codex", 1);
        Directory.CreateDirectory(Path.Combine(workspace.WorkTree, ".git"));
        var context = new AgentLaunchContext("prompt", workspace.PromptFile, profile, workspace.WorkTree, ResumeSessionId: resume,
            CodexOnWindows: codexOnWindows);
        return AgentRun.AgentProcessLaunch(new CodexAdapter("gpt-5", codexHome: Path.Combine(_root, ".codex")), Stack(machine), "/usr/local/bin/codex",
            workspace, profile, context, new Dictionary<string, string?>());
    }

    private static string Mode(ProcessLaunch launch)
    {
        var args = launch.Arguments.ToList();
        return args.IndexOf("--sandbox") is var i and >= 0 ? args[i + 1] : args.Single(a => a.StartsWith("sandbox_mode=", StringComparison.Ordinal));
    }

    [Fact]
    public void On_a_mac_with_its_sandbox_an_editing_codex_runs_without_its_own_inside_the_runners()
    {
        var launch = Launch(Machine.Mac, Developer);

        Assert.Equal(MacSandbox.SandboxExec, launch.FileName);
        Assert.Equal("danger-full-access", Mode(launch));
        Assert.Contains("/usr/local/bin/codex", launch.Arguments);
    }

    [Theory]
    [InlineData(Machine.MacCheckFailed)]
    [InlineData(Machine.Linux)]
    [InlineData(Machine.Basic)]
    [InlineData(Machine.Off)]
    public void Anywhere_else_an_editing_codex_keeps_its_own_sandbox(Machine machine)
    {
        var launch = Launch(machine, Developer);

        Assert.Equal("workspace-write", Mode(launch));
        Assert.NotEqual(MacSandbox.SandboxExec, launch.FileName);
    }

    [Fact]
    public void Windows_role_opt_in_requires_runner_isolation_even_through_the_tool_path_wrapper()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The role setting only applies on Windows.");
        Assert.Equal("workspace-write", Mode(Launch(Machine.Off, Developer, codexOnWindows: CodexWindowsSandbox.SameAsOtherAgents)));
        Assert.Equal("danger-full-access", Mode(Launch(Machine.Basic, Developer, codexOnWindows: CodexWindowsSandbox.SameAsOtherAgents)));
    }

    [Fact]
    public void On_a_mac_a_resumed_session_and_a_role_that_only_reads_keep_codexs_own_sandbox()
    {
        Assert.Equal("sandbox_mode=\"workspace-write\"", Mode(Launch(Machine.Mac, Developer, resume: "01a1d693-49de-7192-acc6-dc991d600218")));
        Assert.Equal("read-only", Mode(Launch(Machine.Mac, Developer with { CanEditFiles = false })));
    }

    private sealed class Scripted(int rulesExit) : IProbeRunner
    {
        public ProbeOutcome Run(ProbeCommand command) => new(command.Step == SandboxProbe.RulesStep ? rulesExit : 0, "");
    }
}
