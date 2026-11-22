using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

/// <summary>
/// the original issue: Codex as a developer inside a run. One sandbox layer: inside the runner's macOS sandbox Codex runs without its own;
/// in Codex's own sandbox the run's .git and the run's own package caches are writable.
/// </summary>
public sealed class CodexRunSandboxTests : IDisposable
{
    private readonly string _run = Directory.CreateTempSubdirectory("mbus-codex-run-").FullName;

    public void Dispose() => Directory.Delete(_run, recursive: true);

    private static readonly PermissionProfile Developer = new() { Name = PermissionProfiles.Worker, CanEditFiles = true, CanRunCommands = true, Network = NetworkAccess.Open };

    private AgentLaunchContext Context(PermissionProfile profile, bool runnerSandboxed)
    {
        var work = Directory.CreateDirectory(Path.Combine(_run, "work")).FullName;
        Directory.CreateDirectory(Path.Combine(work, ".git"));
        return new AgentLaunchContext("prompt", Path.Combine(_run, "prompt.md"), profile, work, RunnerSandboxed: runnerSandboxed, RunDirectory: _run);
    }

    private AgentLaunch Launch(PermissionProfile profile, bool runnerSandboxed) =>
        new CodexAdapter("gpt-5", codexHome: Path.Combine(_run, ".codex")).BuildLaunch(Context(profile, runnerSandboxed));

    private static string After(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_a_verified_external_file_profile_replaces_the_fresh_codex_sandbox(bool writable)
    {
        var context = Context(Developer with { CanEditFiles = writable }, runnerSandboxed: false);
        var adapter = new CodexAdapter("gpt-5", codexHome: Path.Combine(_run, ".codex"), getEnvironmentVariable: _ => null);
        Assert.Equal(writable ? "workspace-write" : "read-only", After(adapter.BuildLaunch(context, windows: false).Arguments, "--sandbox"));
        Assert.Equal("danger-full-access", After(adapter.BuildLaunch(context with { ExternalFilePolicyEnforced = true }, windows: false).Arguments, "--sandbox"));
        var resumed = adapter.BuildLaunch(context with { ExternalFilePolicyEnforced = true, ResumeSessionId = "fixture" }, windows: false);
        Assert.Contains($"sandbox_mode=\"{(writable ? "workspace-write" : "read-only")}\"", resumed.Arguments);
    }

    [Fact]
    public void Machine_options_accept_both_overrides_and_command_line_wins()
    {
        Assert.Equal("always", RunnerOptions.FromEnvironment(k => k == CodexAdapter.SandboxOverrideVariable ? "always" : null, _run).CodexSandbox);
        Assert.Equal("unelevated", RunnerOptions.FromEnvironment(k => k == CodexAdapter.SandboxOverrideVariable ? "unelevated" : null, _run).CodexSandbox);
        Assert.Equal("always", RunnerOptions.FromEnvironment(k => k == CodexAdapter.SandboxOverrideVariable ? "unelevated" : null, _run, codexSandbox: "always").CodexSandbox);
        Assert.Null(RunnerOptions.FromEnvironment(_ => null, _run).CodexSandbox);
        Assert.Equal("allow", RunnerOptions.FromEnvironment(_ => null, _run).AgentTools);
        Assert.Equal("refuse", RunnerOptions.FromEnvironment(k => k == RunnerOptions.AgentToolsVariable ? "refuse" : null, _run).AgentTools);
        Assert.Equal("allow", RunnerOptions.FromEnvironment(k => k == RunnerOptions.AgentToolsVariable ? "refuse" : null, _run, agentTools: "allow").AgentTools);
        Assert.Equal("--agent-tools / MBUS_RUNNER_AGENT_TOOLS must be allow or refuse.",
            Assert.Throws<FormatException>(() => RunnerOptions.FromEnvironment(k => k == RunnerOptions.AgentToolsVariable ? "off" : null, _run)).Message);
        Assert.Throws<FormatException>(() => RunnerOptions.FromEnvironment(k => k == CodexAdapter.SandboxOverrideVariable ? "off" : null, _run));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Kept_tools_come_only_from_enabled_unambiguous_user_tables_and_work_when_resuming(bool resume)
    {
        var context = Context(Developer, false) with { RunDirectory = null, ResumeSessionId = resume ? "session" : null };
        var home = Directory.CreateDirectory(Path.Combine(_run, ".codex")).FullName;
        var config = Path.Combine(home, "config.toml");
        const string servers = "[mcp_servers.a]\ncommand = 'a'\n[mcp_servers.playwright]\ncommand = 'playwright'\n";
        File.WriteAllText(config, servers);
        var adapter = new CodexAdapter(null, codexHome: home, getEnvironmentVariable: _ => null);
        string[] prefix = resume
            ? ["exec", "resume", "session", "--json", "--skip-git-repo-check", "-c", "sandbox_mode=\"workspace-write\""]
            : ["exec", "--json", "--skip-git-repo-check", "--sandbox", "workspace-write"];
        string[] unchanged = [.. prefix, "-c", "sandbox_workspace_write.network_access=true", "-c", "features.plugins=false", "-c", "features.apps=false",
            "-c", "mcp_servers.a.enabled=false", "-c", "mcp_servers.playwright.enabled=false", "-"];
        Assert.Equal(unchanged, adapter.BuildLaunch(context, windows: false).Arguments);
        var kept = context with { KeepAgentTools = ["playwright"] };
        var launch = adapter.BuildLaunch(kept, windows: false);
        Assert.Null(launch.CannotStart);
        Assert.Equal(unchanged.Select(a => a == "mcp_servers.playwright.enabled=false" ? "mcp_servers.playwright.default_tools_approval_mode=\"approve\"" : a), launch.Arguments);
        Assert.Contains("This run keeps these tools: playwright. Use them instead of starting programs yourself.", launch.StandardInput);
        Assert.Equal("Keeps these Codex tools: playwright.", launch.Note);

        var missing = "This role's runs keep playwright, but Codex on this computer doesn't have it set up. Add it to Codex's settings here, or run this role on another computer.";
        File.WriteAllText(config, servers.Replace("mcp_servers.playwright", "mcp_servers.Playwright"));
        Assert.Equal(missing, adapter.BuildLaunch(kept, false).CannotStart);
        File.WriteAllText(config, servers + "enabled = false\n");
        Assert.Equal(missing, adapter.BuildLaunch(kept, false).CannotStart);
        File.WriteAllText(config, servers + "[mcp_servers.Playwright]\ncommand = 'other'\n");
        Assert.Equal(missing, adapter.BuildLaunch(kept, false).CannotStart);

        var repositoryConfig = Directory.CreateDirectory(Path.Combine(context.WorkingDirectory, ".codex")).FullName;
        string[] repositorySettings =
        [
            "[mcp_servers.playwright]\ncommand = 'repository'\n",
            "[mcp_servers]\nplaywright = { command = 'repository' }\n",
            "mcp_servers.playwright.command = 'repository'\n",
            "[mcp_servers.playwright.env]\nNODE_OPTIONS = 'repository'\n",
            "[\"mcp_servers\".\"playwright\"]\ncommand = 'repository'\n",
            "[\"\\u006dcp_servers\".playwright]\ncommand = 'repository'\n",
            "[\"\\U0000006dcp_servers\".playwright]\ncommand = 'repository'\n",
            "# MCP settings\n",
        ];
        var nested = kept with { WorkingDirectory = Directory.CreateDirectory(Path.Combine(context.WorkingDirectory, "src", "nested")).FullName };
        foreach (var trusted in new[] { true, false })
        {
            File.WriteAllText(config, servers + (trusted
                ? $"[projects.{MessageBus.Cli.Connect.CodexTomlConfig.Quote(context.WorkingDirectory)}]\ntrust_level = 'trusted'\n" : ""));
            foreach (var settings in repositorySettings)
            {
                File.WriteAllText(Path.Combine(repositoryConfig, "config.toml"), settings);
                Assert.Equal("This repository's own Codex settings set up agent tools, which could change playwright. Runs keep a tool only from this computer's own Codex settings.",
                    adapter.BuildLaunch(kept, false).CannotStart);
                Assert.Equal(adapter.BuildLaunch(kept, false).CannotStart, adapter.BuildLaunch(nested, false).CannotStart);
                Assert.Null(adapter.BuildLaunch(context, false).CannotStart);
            }
            File.WriteAllText(Path.Combine(repositoryConfig, "config.toml"), "model = 'example'\n");
            Assert.Null(adapter.BuildLaunch(kept, false).CannotStart);
        }
        // A present path that cannot be read as settings also fails closed.
        File.Delete(Path.Combine(repositoryConfig, "config.toml"));
        Directory.CreateDirectory(Path.Combine(repositoryConfig, "config.toml"));
        Assert.NotNull(adapter.BuildLaunch(kept, false).CannotStart);
    }

    [Fact]
    public void Inside_the_runners_sandbox_an_editing_codex_has_no_sandbox_of_its_own_so_the_runner_wraps_it()
    {
        var launch = Launch(Developer, runnerSandboxed: true);

        Assert.Equal("danger-full-access", After(launch.Arguments, "--sandbox"));
        // The runner's sandbox then holds it: Wrap only skips agents that start their own.
        Assert.False(MacSandbox.StartsItsOwnSandbox(new ProcessLaunch { FileName = "codex", Arguments = launch.Arguments, WorkingDirectory = _run }));
        // Nothing of Codex's own sandbox: the runner's limits the network and the files.
        Assert.DoesNotContain(launch.Arguments, a => a.StartsWith("sandbox_workspace_write.", StringComparison.Ordinal));
        Assert.Empty(launch.Environment);

        // A run that only reads keeps Codex's read-only sandbox, also inside the runner's.
        Assert.Equal("read-only", After(Launch(Developer with { CanEditFiles = false }, runnerSandboxed: true).Arguments, "--sandbox"));
    }

    [Fact]
    public void Without_the_runners_sandbox_codex_keeps_its_own_and_may_write_the_runs_git_and_package_caches()
    {
        var launch = Launch(Developer, runnerSandboxed: false);

        Assert.Equal("workspace-write", After(launch.Arguments, "--sandbox"));
        var roots = launch.Arguments.Single(a => a.StartsWith("sandbox_workspace_write.writable_roots=", StringComparison.Ordinal));
        var cache = Path.Combine(_run, CodexAdapter.RunFolders.CacheFolder);
        Assert.Contains(Connect.CodexTomlConfig.Quote(Path.Combine(_run, "work", ".git")), roots);
        Assert.Contains(Connect.CodexTomlConfig.Quote(cache), roots);
        Assert.StartsWith(cache, launch.Environment["NUGET_PACKAGES"], StringComparison.Ordinal);
        Assert.StartsWith(cache, launch.Environment["npm_config_cache"], StringComparison.Ordinal);
        Assert.True(Directory.Exists(launch.Environment["NUGET_PACKAGES"]));
    }

    [Fact]
    public void Windows_owned_runs_build_without_persistent_servers_and_explain_the_remaining_sandbox_limits()
    {
        var context = Context(Developer, runnerSandboxed: false);
        File.WriteAllText(Path.Combine(context.WorkingDirectory, "app.slnx"), "");
        var adapter = new CodexAdapter("gpt-5", codexHome: Path.Combine(_run, ".codex"));
        var windows = adapter.BuildLaunch(context, windows: true);

        Assert.Equal("1", windows.Environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("0", windows.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
        Assert.Equal("false", windows.Environment["UseSharedCompilation"]);
        Assert.Contains(CodexAdapter.DotnetPromptNote, windows.StandardInput);
        Assert.Equal(CodexAdapter.DotnetSettleNote, windows.SettleNote);

        var other = adapter.BuildLaunch(context, windows: false);
        Assert.DoesNotContain("MSBUILDDISABLENODEREUSE", other.Environment.Keys);
        Assert.DoesNotContain("DOTNET_CLI_USE_MSBUILD_SERVER", other.Environment.Keys);
        Assert.DoesNotContain("UseSharedCompilation", other.Environment.Keys);
        Assert.Equal(context.Prompt, other.StandardInput);
        Assert.Empty(adapter.BuildLaunch(context with { ResumeSessionId = "existing-session" }, windows: true).Environment);
    }

    [Theory]
    [InlineData(true, true, CodexWindowsSandbox.Sandbox, null, "workspace-write")]
    [InlineData(true, true, CodexWindowsSandbox.SameAsOtherAgents, null, "danger-full-access")]
    [InlineData(true, false, CodexWindowsSandbox.SameAsOtherAgents, null, "read-only")]
    [InlineData(false, true, CodexWindowsSandbox.SameAsOtherAgents, null, "workspace-write")]
    [InlineData(true, true, CodexWindowsSandbox.SameAsOtherAgents, "always", "workspace-write")]
    [InlineData(true, true, CodexWindowsSandbox.SameAsOtherAgents, "unelevated", "workspace-write")]
    public void Role_opt_in_and_machine_override_select_the_windows_sandbox(bool windows, bool writable, CodexWindowsSandbox roleMode, string? machine, string expected)
    {
        var context = Context(Developer with { CanEditFiles = writable }, false) with { CodexOnWindows = roleMode };
        var adapter = new CodexAdapter(null, codexHome: Path.Combine(_run, ".codex"), getEnvironmentVariable: _ => machine);
        File.WriteAllText(Path.Combine(context.WorkingDirectory, "global.json"), "{}");
        var launch = adapter.BuildLaunch(context, windows);
        Assert.Equal(expected, After(launch.Arguments, "--sandbox"));
        Assert.Equal(windows && writable && expected != "danger-full-access", launch.SettleNote is not null);
        Assert.Equal(windows && writable && roleMode == CodexWindowsSandbox.SameAsOtherAgents, launch.Note is not null);
        if (expected == "danger-full-access")
        {
            Assert.Empty(launch.Environment);
            Assert.DoesNotContain(launch.Arguments, a => a.StartsWith("sandbox_workspace_write.", StringComparison.Ordinal));
            Assert.Equal(context.Prompt, launch.StandardInput);
        }
        if (machine == "unelevated") Assert.Contains("features.prefer_mxc=false", launch.Arguments);
    }

    [Fact]
    public void Resumed_sessions_keep_the_sandbox_and_the_command_line_owner_override_wins_over_environment()
    {
        var context = Context(Developer, false) with { CodexOnWindows = CodexWindowsSandbox.SameAsOtherAgents };
        var adapter = new CodexAdapter(null, getEnvironmentVariable: _ => "unelevated", sandboxOverride: "always");
        var launch = adapter.BuildLaunch(context, windows: true);
        Assert.Equal("workspace-write", After(launch.Arguments, "--sandbox"));
        Assert.Contains("features.prefer_mxc=true", launch.Arguments);
        var resume = new CodexAdapter(null, getEnvironmentVariable: _ => null).BuildLaunch(context with { ResumeSessionId = "session" }, true);
        Assert.Contains("sandbox_mode=\"workspace-write\"", resume.Arguments);
        var isolationOff = new CodexAdapter(null, getEnvironmentVariable: _ => null)
            .BuildLaunch(context with { RunnerIsolationEnabled = false }, true);
        Assert.Equal("workspace-write", After(isolationOff.Arguments, "--sandbox"));
    }

    [Fact]
    public void Dotnet_notice_only_applies_when_commands_or_tests_can_run_in_a_dotnet_working_copy()
    {
        var context = Context(Developer, false);
        var adapter = new CodexAdapter(null, getEnvironmentVariable: _ => null);
        Assert.Null(adapter.BuildLaunch(context, true).SettleNote);
        File.WriteAllText(Path.Combine(context.WorkingDirectory, "app.csproj"), "");
        Assert.Null(adapter.BuildLaunch(context with { Profile = Developer with { CanRunCommands = false, CanRunTests = false } }, true).SettleNote);
        var testsOnly = adapter.BuildLaunch(context with { Profile = Developer with { CanEditFiles = false, CanRunCommands = false, CanRunTests = true } }, true);
        Assert.DoesNotContain(CodexAdapter.DotnetPromptNote, testsOnly.StandardInput);
        Assert.Null(testsOnly.SettleNote);
        Assert.Null(adapter.BuildLaunch(context, false).SettleNote);
        var note = adapter.BuildLaunch(context, true).SettleNote;
        var result = AgentRun.Compose(StopReason.None, null, null, 0, null,
            new MessageBus.Contracts.Mcp.SettleInput { ThreadId = "thr_test", Result = new() { Status = MessageBus.Contracts.Messaging.SettleStatus.Succeeded, Summary = "Done" } },
            [], TimeSpan.Zero, runnerNote: note);
        Assert.Equal("Done\n" + CodexAdapter.DotnetSettleNote, result.Summary);
        var failed = AgentRun.Compose(StopReason.None, null, null, 1, null,
            new MessageBus.Contracts.Mcp.SettleInput { ThreadId = "thr_test", Result = new() { Status = MessageBus.Contracts.Messaging.SettleStatus.Succeeded, Summary = "Done" } },
            [], TimeSpan.Zero, runnerNote: note);
        Assert.Equal("Done", failed.Summary);
    }

    [Theory]
    [InlineData("app.sln", true)]
    [InlineData("src/app.slnx", true)]
    [InlineData("src/app.csproj", true)]
    [InlineData("global.json", true)]
    [InlineData("src/global.json", true)]
    [InlineData("src/deeper/app.csproj", false)]
    [InlineData("node_modules/app.csproj", false)]
    [InlineData("package.json", false)]
    public void Dotnet_detection_is_bounded_to_the_root_and_one_level(string marker, bool expected)
    {
        var work = Context(Developer, false).WorkingDirectory;
        var file = Path.Combine(work, marker.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{}");
        Assert.Equal(expected, TestCommands.IsDotnetRepository(work));
    }

    [Fact]
    public void Only_the_runners_own_clone_gets_its_git_opened_up_never_a_resumed_session_or_a_folder_outside_the_run()
    {
        var context = Context(Developer, runnerSandboxed: false);

        Assert.Null(CodexAdapter.RunFolders.For(context with { ResumeSessionId = "01a1d693-49de-7192-acc6-dc991d600218" }));
        var elsewhere = Directory.CreateTempSubdirectory("mbus-codex-repo-");
        try
        {
            Assert.Null(CodexAdapter.RunFolders.For(context with { WorkingDirectory = elsewhere.FullName }));
        }
        finally
        {
            elsewhere.Delete(recursive: true);
        }
        // Windows: NuGet's user settings come from the run too, since Codex's sandbox can't read the user's AppData.
        Assert.Contains("APPDATA", CodexAdapter.RunFolders.For(context, windows: true)!.Environment.Keys);
        Assert.DoesNotContain("APPDATA", CodexAdapter.RunFolders.For(context, windows: false)!.Environment.Keys);
    }

    [Theory]
    [InlineData("cache", false)]
    [InlineData("work/.git", false)]
    [InlineData("cache", true)]
    [InlineData("work/.git", true)]
    public async Task A_link_in_the_run_folder_opens_nothing_up_and_nothing_is_created_where_it_points(string place, bool junction)
    {
        if (junction && !OperatingSystem.IsWindows())
        {
            Assert.Skip("Junctions are Windows only.");
        }
        if (junction) await WindowsRunCapabilities.RequireJunctionsAsync();
        var context = Context(Developer, runnerSandboxed: false);
        var outside = Directory.CreateTempSubdirectory("mbus-codex-outside-");
        var link = Path.Combine(_run, place.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            Link(link, outside.FullName, junction);

            Assert.Null(CodexAdapter.RunFolders.For(context));
            Assert.Empty(outside.EnumerateFileSystemInfos());
            var launch = new CodexAdapter("gpt-5", codexHome: Path.Combine(_run, ".codex")).BuildLaunch(context);
            Assert.DoesNotContain(launch.Arguments, a => a.StartsWith("sandbox_workspace_write.writable_roots=", StringComparison.Ordinal));
            Assert.Empty(launch.Environment);
        }
        finally
        {
            // The link first: Windows won't delete a junction whose target is already gone as part of its folder.
            if (new DirectoryInfo(link).LinkTarget is not null)
            {
                Directory.Delete(link);
            }
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_git_file_pointing_elsewhere_opens_nothing_up()
    {
        var context = Context(Developer, runnerSandboxed: false);
        Directory.Delete(Path.Combine(_run, "work", ".git"));
        File.WriteAllText(Path.Combine(_run, "work", ".git"), "gitdir: /elsewhere/.git\n");

        Assert.Null(CodexAdapter.RunFolders.For(context));
        Assert.False(Directory.Exists(Path.Combine(_run, CodexAdapter.RunFolders.CacheFolder)));
    }

    private static void Link(string path, string target, bool junction)
    {
        if (!junction)
        {
            try
            {
                Directory.CreateSymbolicLink(path, target);
            }
            catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("This Windows account can't make symbolic links (the junction case covers Windows).");
            }
            return;
        }
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", path, target])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }
}
