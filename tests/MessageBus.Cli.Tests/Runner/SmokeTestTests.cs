using MessageBus.Cli.Commands;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Spectre.Console;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

/// <summary>
/// <c>mbus doctor --smoke</c> (the original issue): the fake agent, started as the runner starts Claude Code, works in the scratch
/// repository (the shell-script project, so no .NET build runs), and the check decides each step from what is on disk
/// and in git, not from what the agent says.
/// </summary>
public sealed class SmokeTestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mbus-smoke-tests", Guid.NewGuid().ToString("N"));

    private string WorkRoot => Path.Combine(_root, "runs");

    private static readonly string Build = OperatingSystem.IsWindows() ? @".\build.cmd" : "sh build.sh";
    private static readonly string Test = OperatingSystem.IsWindows() ? @".\test.cmd" : "sh test.sh";

    private SmokeTest Smoke(string directives, bool keep = false, string? openAiKey = null, IRunSandbox? sandbox = null) => new(new SmokeTestOptions
    {
        WorkRoot = WorkRoot,
        Git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []) ?? throw new InvalidOperationException("git is needed for these tests."),
        Dotnet = null,
        Sandbox = sandbox ?? new NoSandbox(),
        Keep = keep,
        PromptSuffix = directives,
        GetEnvironmentVariable = name => name == "OPENAI_API_KEY" ? openAiKey : null,
    });

    private static SmokeAgent FakeClaude => new(AgentKind.ClaudeCode, new ClaudeCodeAdapter(), RunnerHarness.FakeAgentPath);

    [Fact]
    public async Task Codex_smoke_checks_a_model_response_using_the_normalized_key()
    {
        var codex = new SmokeAgent(AgentKind.Codex, new CodexAdapter(null, codexHome: Path.Combine(_root, ".codex")), RunnerHarness.FakeAgentPath);
        var token = TestContext.Current.CancellationToken;
        const string key = "fixture-smoke-credential";
        var success = await Smoke($"""
            fake-agent: env=CODEX_API_KEY
            fake-agent: replace=word.txt|word=before|word=after
            fake-agent: run={Build} && {Test}
            fake-agent: commit={SmokeTest.CommitMessage}
            """, keep: true, openAiKey: key).RunAsync(codex, token);
        Assert.True(success.Passed, string.Join("; ", success.Steps.Select(step => step.Detail)));
        Assert.Equal("CODEX_API_KEY=" + key, File.ReadAllText(Path.Combine(success.KeptFolder!, "work", "env.txt")).Trim());
        Assert.Contains("completed an authenticated model request", success.Steps.First().Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Codex_smoke_reports_a_provider_refusal_without_its_key(bool wrapped)
    {
        var codex = new SmokeAgent(AgentKind.Codex, new CodexAdapter(null, codexHome: Path.Combine(_root, ".codex")), RunnerHarness.FakeAgentPath);
        const string key = "fixture-smoke-credential";
        var refused = await Smoke(wrapped ? "fake-agent: mode=claim" : "fake-agent: mode=codex-ineligible", openAiKey: key,
            sandbox: wrapped ? new FileEnvironmentSandbox(key, emitDiagnostic: true) : null)
            .RunAsync(codex, TestContext.Current.CancellationToken);
        Assert.False(refused.Passed);
        Assert.Contains(wrapped ? "fixture provider rejected [redacted]" : "Codex-eligible sign-in or API project", Assert.Single(refused.Steps).Detail);
        Assert.DoesNotContain(key, Report([refused]).Output);
    }

    [Fact]
    public async Task Smoke_awaits_cleanup_and_preserves_a_quarantined_workspace()
    {
        var sandbox = new SmokeCleanup();
        var codex = new SmokeAgent(AgentKind.Codex, new CodexAdapter(null, codexHome: Path.Combine(_root, ".codex")), RunnerHarness.FakeAgentPath);
        var run = Smoke("fake-agent: mode=codex-ineligible", sandbox: sandbox).RunAsync(codex, TestContext.Current.CancellationToken);
        try
        {
            await sandbox.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.False(run.IsCompleted);
            Assert.Equal(AgentKind.Codex, sandbox.Workspace!.AgentKind);
            Assert.True(Directory.Exists(sandbox.Workspace.Root));
        }
        finally { sandbox.Release.TrySetResult(); }
        await Assert.ThrowsAsync<IOException>(() => run);
        Assert.True(Directory.Exists(sandbox.Workspace!.Root));
        Assert.Contains("9999-12-31", File.ReadAllText(Path.Combine(sandbox.Workspace.Root, RunWorkspace.HoldFileName)));
    }

    private sealed class SmokeCleanup : IRunSandbox
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal RunWorkspace? Workspace;
        public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile) => launch;
        public Task PrepareAsync(RunWorkspace workspace, TaskDelivery delivery, PermissionProfile profile, CancellationToken cancellationToken)
        {
            Workspace = workspace;
            return Task.CompletedTask;
        }
        public void Ended(RunWorkspace workspace) => throw new InvalidOperationException("Legacy cleanup must not bypass asynchronous teardown.");
        public async Task EndedAsync(RunWorkspace workspace)
        {
            Entered.TrySetResult();
            await Release.Task;
            workspace.SandboxCleanupIncomplete = true;
            throw new IOException("Owned cleanup remains unverified.");
        }
    }

    [Fact]
    public async Task An_agent_that_edits_builds_tests_and_commits_passes_every_step_and_its_run_folder_is_removed()
    {
        var result = await Smoke($"""
            fake-agent: replace=word.txt|word=before|word=after
            fake-agent: run={Build} && {Test}
            fake-agent: commit={SmokeTest.CommitMessage}
            """).RunAsync(FakeClaude, TestContext.Current.CancellationToken);

        Assert.True(result.Passed, string.Join("; ", result.Steps.Select(s => $"{s.Name}: {s.Detail}")));
        Assert.Equal(SmokeTest.StepNames, result.Steps.Select(s => s.Name));
        Assert.Contains("Fake agent <fake-agent@messagebus.invalid>", result.Steps.Single(s => s.Name == "commit").Detail);
        Assert.Equal(0.0421m, result.Usage?.EstimatedCostUsd);
        Assert.Null(result.KeptFolder);
        Assert.Empty(Directory.EnumerateFileSystemEntries(WorkRoot));

        var (output, ok) = Report([result]);
        Assert.True(ok);
        Assert.Contains("about $0.04", output);
        Assert.Contains("can edit, build, test and commit here", output);
    }

    [Fact]
    public async Task An_agent_that_only_edits_and_claims_success_fails_build_test_and_commit_and_an_agent_not_ready_is_not_started()
    {
        // mode=claim: says "All tests pass" and exits 2, having run nothing.
        var claimed = await Smoke("""
            fake-agent: mode=claim
            fake-agent: replace=word.txt|word=before|word=after
            """, keep: true).RunAsync(FakeClaude, TestContext.Current.CancellationToken);
        var notReady = await Smoke("").RunAsync(new SmokeAgent(AgentKind.Codex, null, null, "Codex's Windows sandbox isn't set up."), TestContext.Current.CancellationToken);

        Assert.False(claimed.Passed);
        var steps = claimed.Steps.ToDictionary(s => s.Name);
        Assert.True(steps["edit"].Ok);
        Assert.False(steps["build"].Ok);
        // The first failure says what the agent claimed, and what running the build here gives.
        Assert.Contains("No build output", steps["build"].Detail);
        Assert.Contains("The agent said: All tests pass", steps["build"].Detail);
        Assert.Contains($"Run here, {Build} works.", steps["build"].Detail);
        Assert.False(steps["test"].Ok);
        Assert.Contains("The test didn't run.", steps["test"].Detail);
        Assert.Equal($"No new commit on {SmokeTest.Branch}.", steps["commit"].Detail);
        Assert.NotNull(claimed.KeptFolder);
        Assert.True(Directory.Exists(Path.Combine(claimed.KeptFolder, "work", ".git")));

        Assert.False(notReady.Passed);
        Assert.Empty(notReady.Steps);
        Assert.Single(Directory.EnumerateDirectories(WorkRoot));

        var (output, ok) = Report([claimed, notReady]);
        Assert.False(ok);
        Assert.Contains("failed  commit", output);
        Assert.Contains("not run  Codex: Codex's Windows sandbox isn't set up.", output);
        Assert.Contains($"kept    {claimed.KeptFolder}", output);
        Assert.Contains("0 of 2 agents passed.", output);
    }

    private static (string Output, bool Ok) Report(IReadOnlyList<SmokeResult> results)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
        });
        console.Profile.Width = 1000;
        foreach (var result in results)
        {
            DoctorSmoke.Write(console, result);
        }
        var ok = DoctorSmoke.Summary(console, results);
        return (writer.ToString(), ok);
    }

    public void Dispose() => RunWorkspace.TryDelete(_root);
}
