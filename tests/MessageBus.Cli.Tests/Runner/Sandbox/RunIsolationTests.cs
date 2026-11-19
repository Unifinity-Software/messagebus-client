using System.Diagnostics;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner.Sandbox;

/// <summary>
/// Run isolation (the original issue) with real processes: the runner starts the fake agent through
/// <see cref="RunIsolation"/> exactly as it would start claude, and the fake agent probes what it
/// can see and reach. Each test asserts only what this OS's isolation mode enforces.
/// </summary>
public sealed class RunIsolationTests
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public async Task A_run_without_configured_git_identity_commits_as_the_neutral_runner()
    {
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        var root = Directory.CreateTempSubdirectory("mbus-git-identity-").FullName;
        try
        {
            using var workspace = RunWorkspace.Create(root, "dlv_git_identity", 1);
            Directory.CreateDirectory(workspace.WorkTree);
            var sandbox = RunIsolation.Create(new RunIsolationOptions
            {
                WorkRoot = root,
                HomeDirectory = root,
                MachineName = "example-workstation",
                Requested = IsolationLevel.Basic,
                // Leave Git unset so this test never reads the user's global configuration.
            }, TextWriter.Null);
            var profile = new PermissionProfile { Name = "worker" };

            async Task<IReadOnlyList<string>> Git(params string[] arguments)
            {
                using var process = AgentProcess.Start(sandbox.Wrap(new ProcessLaunch
                {
                    FileName = git!,
                    Arguments = arguments,
                    WorkingDirectory = workspace.WorkTree,
                }, workspace, profile));
                var exitCode = await process.WaitForExitAsync();
                var output = process.Tail();
                Assert.True(exitCode == 0, $"git {string.Join(' ', arguments)} failed: {string.Join("\n", output)}");
                return output;
            }

            try
            {
                await Git("init", "--quiet");
                await Git("commit", "--quiet", "--allow-empty", "-m", "Test commit");
                var identities = await Git("log", "-1", "--format=%an <%ae>%n%cn <%ce>");
                const string expected = "mbus runner <runner@messagebus.invalid>";
                Assert.Equal(new[] { expected, expected }, identities);
            }
            finally
            {
                sandbox.Ended(workspace);
            }
        }
        finally
        {
            RunWorkspace.TryDelete(root);
        }
    }

    [Fact]
    public void File_key_startup_explains_sandbox_protection_and_warns_with_isolation_off()
    {
        Assert.Contains("can't read mbus's settings folder", RunIsolation.MachineKeyLimit(true, "file"));
        Assert.DoesNotContain("Keychain", RunIsolation.MachineKeyLimit(true, "file"));
        var log = new StringWriter();
        RunIsolation.Create(new RunIsolationOptions
        {
            WorkRoot = Path.GetTempPath(), HomeDirectory = Home, KeyStore = "file", Requested = IsolationLevel.Off,
        }, log);
        Assert.Contains("Warning:", log.ToString());
        Assert.Contains("can read the key file", log.ToString());
    }

    [Theory]
    [InlineData(IsolationLevel.Basic)]
    [InlineData(IsolationLevel.OsSandbox)]
    public async Task A_run_inherits_none_of_the_runners_secrets_but_keeps_what_the_agent_needs(IsolationLevel requested)
    {
        var id = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var secret = $"MBUS_TEST_SECRET_{id}";
        var modelKey = $"ANTHROPIC_TEST_KEY_{id}";
        // The owner lets their collector settings through; the run's telemetry still goes only where the service says (the original issue).
        var ownerCollector = "OTEL_EXPORTER_OTLP_TRACES_HEADERS";
        Environment.SetEnvironmentVariable(secret, "s3cret");
        Environment.SetEnvironmentVariable(modelKey, "model-key");
        Environment.SetEnvironmentVariable(ownerCollector, "x-api-key=owners-collector-key");
        try
        {
            var telemetry = new RunTelemetrySettings(new Uri("https://mbus.example/api/w/w1/otlp"), "mbt_run-token", Traces: true, Metrics: false, Logs: true);
            RunIsolation? isolation = null;
            await using var h = new RunnerHarness(agentEnvironment: new Dictionary<string, string>
            {
                ["MSBUILDDISABLENODEREUSE"] = "0",
                ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "1",
                ["UseSharedCompilation"] = "true",
            }, sandbox: root => isolation = (RunIsolation)RunIsolation.Create(new RunIsolationOptions
            {
                WorkRoot = root, HomeDirectory = Home, MachineName = "test-machine", PassEnvironment = [ownerCollector],
                Requested = requested,
                Telemetry = new FixedTelemetry(telemetry),
            }, TextWriter.Null));
            Assert.True((await h.DeliverAsync(h.Task("dlv_env", directives: d => d["env"] =
                $"{secret},{modelKey},MBUS_DELIVERY_ID,TEMP,MAC_CHROMIUM_TMPDIR,MSBUILDDISABLENODEREUSE,DOTNET_CLI_USE_MSBUILD_SERVER,UseSharedCompilation,{ownerCollector},OTEL_EXPORTER_OTLP_ENDPOINT,OTEL_EXPORTER_OTLP_HEADERS,OTEL_METRICS_EXPORTER"))).Accepted);

            var settle = await h.Service.SettledAsync("dlv_env");

            Assert.Equal(SettleStatus.Succeeded, settle.Result.Status);
            var seen = File.ReadAllLines(Path.Combine(h.WorkTreeOf("dlv_env"), "env.txt"))
                .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
            Assert.Equal("<unset>", seen[secret]);
            Assert.Equal("model-key", seen[modelKey]);
            Assert.Equal("dlv_env", seen["MBUS_DELIVERY_ID"]);
            Assert.StartsWith(Path.GetDirectoryName(h.WorkTreeOf("dlv_env"))!, seen["TEMP"]);
            // the original issue: Chrome on macOS finds a temp folder it may use inside the sandbox.
            Assert.Equal(OperatingSystem.IsMacOS() ? seen["TEMP"] : "<unset>", seen["MAC_CHROMIUM_TMPDIR"]);
            var ownBuildProcesses = OperatingSystem.IsMacOS() ||
                (OperatingSystem.IsLinux() && isolation!.Level == IsolationLevel.OsSandbox);
            Assert.Equal(ownBuildProcesses ? "1" : "0", seen["MSBUILDDISABLENODEREUSE"]);
            Assert.Equal(ownBuildProcesses ? "0" : "1", seen["DOTNET_CLI_USE_MSBUILD_SERVER"]);
            Assert.Equal(ownBuildProcesses ? "false" : "true", seen["UseSharedCompilation"]);
            Assert.Equal("<unset>", seen[ownerCollector]);
            Assert.Equal("https://mbus.example/api/w/w1/otlp", seen["OTEL_EXPORTER_OTLP_ENDPOINT"]);
            Assert.Equal("x-mbus-run-telemetry=mbt_run-token", seen["OTEL_EXPORTER_OTLP_HEADERS"]);
            Assert.Equal("none", seen["OTEL_METRICS_EXPORTER"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret, null);
            Environment.SetEnvironmentVariable(modelKey, null);
            Environment.SetEnvironmentVariable(ownerCollector, null);
        }
    }

    [Fact]
    public async Task The_runs_credential_answers_only_for_the_task_repository_and_stops_when_the_run_ends()
    {
        await WindowsRunCapabilities.RequireGitShellAsync();
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        var token = "tok_" + Guid.NewGuid().ToString("N");
        await using var h = new RunnerHarness(sandbox: root => Isolation(root, new IssueEveryone(token)));
        const string taskRepo = "https://github.com/acme/app.git";
        var task = h.Task("dlv_cred", directives: d => d["git-credential"] = $"{taskRepo}|https://github.com/acme/other.git|https://gitlab.com/acme/app.git");
        task = task with { Envelope = task.Envelope with { Refs = [new Ref { Kind = RefKinds.GitHubPullRequest, Repo = "acme/app", Number = 7 }] } };

        Assert.True((await h.DeliverAsync(task)).Accepted);
        var settle = await h.Service.SettledAsync("dlv_cred");

        Assert.Equal(SettleStatus.Succeeded, settle.Result.Status);
        var answers = File.ReadAllLines(Path.Combine(h.WorkTreeOf("dlv_cred"), "credentials.txt"))
            .Select(l => l.Split(' ', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(token, answers[taskRepo]);
        Assert.Equal("<none>", answers["https://github.com/acme/other.git"]);
        Assert.Equal("<none>", answers["https://gitlab.com/acme/app.git"]);

        // Anything still holding the run's git settings after it ended (an orphan, a leftover
        // terminal) gets nothing.
        Assert.Null(GitPassword(git!, Path.Combine(Path.GetDirectoryName(h.WorkTreeOf("dlv_cred"))!, "gitconfig"), taskRepo));
    }

    [Fact]
    public async Task A_background_process_the_agent_leaves_behind_ends_with_the_run()
    {
        // Windows: the run's job object. macOS and Linux (the original issue): the run's marker variable finds it, though it left the
        // agent's session; under the Linux sandbox its PID namespace ends it first.
        await using var h = new RunnerHarness(sandbox: Isolation);

        Assert.True((await h.DeliverAsync(h.Task("dlv_orphan", directives: d => d["mode"] = "orphan"))).Accepted);
        var settle = await h.Service.SettledAsync("dlv_orphan");

        Assert.Equal(SettleStatus.Succeeded, settle.Result.Status);
        if (!OperatingSystem.IsWindows())
        {
            // Under the Linux sandbox the pid the agent saw is its own namespace's, so look for the run's processes by
            // a variable they all inherit instead. SIGKILL takes a moment to land.
            var table = PosixRunProcesses.Current!;
            var entry = System.Text.Encoding.UTF8.GetBytes("MBUS_DELIVERY_ID=dlv_orphan");
            bool Left() => table.List().Any(p => p.Pid != Environment.ProcessId && table.HasEnvironmentEntry(p.Pid, entry));
            for (var i = 0; i < 100 && Left(); i++)
            {
                Thread.Sleep(50);
            }
            Assert.False(Left(), "A program the run started is still running.");
            return;
        }
        var pid = int.Parse(File.ReadAllText(Path.Combine(h.WorkTreeOf("dlv_orphan"), "pids.txt")), System.Globalization.CultureInfo.InvariantCulture);
        Process orphan;
        try
        {
            orphan = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return; // already gone
        }
        using (orphan)
        {
            Assert.True(orphan.WaitForExit(TimeSpan.FromSeconds(5)), $"The orphan {pid} is still running.");
        }
    }

    [Fact]
    public async Task On_macos_and_linux_a_task_that_reads_or_writes_outside_its_run_fails_and_that_is_its_result()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(), "Only the macOS and Linux sandboxes keep a run out of files outside its worktree.");
        // the original issue: macOS can't start a sandbox inside another one.
        Assert.SkipWhen(OperatingSystem.IsMacOS() && MessageBus.Cli.Security.MacSecurity.InSandbox(),
            "These tests run inside a run's macOS sandbox, and macOS can't start a run's sandbox inside it.");
        RunIsolation? isolation = null;
        await using var h = new RunnerHarness(sandbox: root => isolation = (RunIsolation)Isolation(root));
        // the original issue: Linux needs bubblewrap, and user namespaces the machine allows; without them runs say why and get basic isolation.
        Assert.SkipWhen(OperatingSystem.IsLinux() && isolation!.Level != IsolationLevel.OsSandbox, isolation!.SandboxProblem ?? "No Linux sandbox here.");
        Assert.True(isolation!.Level == IsolationLevel.OsSandbox, string.Join(" ", isolation.Limitations));
        // Another run's worktree on the same machine (a different sender's code), and a folder outside the work folder.
        var otherRun = Directory.CreateDirectory(Path.Combine(h.WorkRoot, "dlv_someone_else-1", "work")).FullName;
        var secret = Path.Combine(otherRun, "secret.txt");
        File.WriteAllText(secret, "not yours");
        // On Linux the harness's scratch folder is in /tmp, which runs share (as on macOS), so the write goes to the other run.
        var outside = OperatingSystem.IsLinux() ? Path.Combine(otherRun, "escaped.txt") : Path.Combine(h.Scratch, "escaped.txt");

        Assert.True((await h.DeliverAsync(h.Task("dlv_read", directives: d => d["read"] = secret))).Accepted);
        Assert.True((await h.DeliverAsync(h.Task("dlv_write", directives: d => d["write"] = outside))).Accepted);
        var read = await h.Service.SettledAsync("dlv_read");
        var write = await h.Service.SettledAsync("dlv_write");

        Assert.Equal(SettleStatus.Failed, read.Result.Status);
        Assert.Contains($"Couldn't read {secret}", read.Result.Error);
        Assert.Equal(SettleStatus.Failed, write.Result.Status);
        Assert.Contains($"Couldn't write {outside}", write.Result.Error);
        Assert.False(File.Exists(outside));
        // Inside its own worktree the agent works as usual.
        Assert.Equal("dlv_read", File.ReadAllText(Path.Combine(h.WorkTreeOf("dlv_read"), "fake-agent.txt")));
    }

    [Fact]
    public async Task A_run_the_owner_lets_use_their_git_sign_in_gets_it_instead_of_a_run_token()
    {
        await WindowsRunCapabilities.RequireGitShellAsync();
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        // The owner's own git settings: a credential helper that answers with their password. The empty helper first
        // drops any machine-wide helper (Git for Windows' credential manager), so nothing here can open a sign-in window.
        var home = Directory.CreateTempSubdirectory("mbus-owner-home-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(home, ".gitconfig"),
                "[credential]\n\thelper =\n\thelper = \"!f() { echo username=owner; echo password=owner-pass; }; f\"\n");
            await using var h = new RunnerHarness(sandbox: root => RunIsolation.Create(new RunIsolationOptions
            {
                WorkRoot = root, HomeDirectory = home, Credentials = new IssueEveryone("run-token"), MachineName = "test-machine",
                UseOwnerGit = d => d.DeliveryId == "dlv_owner",
            }, TextWriter.Null));
            const string repo = "https://github.com/acme/app.git";
            foreach (var id in new[] { "dlv_owner", "dlv_other" })
            {
                var task = h.Task(id, directives: d => d["git-credential"] = repo);
                task = task with { Envelope = task.Envelope with { Refs = [new Ref { Kind = RefKinds.GitHubPullRequest, Repo = "acme/app", Number = 7 }] } };
                Assert.True((await h.DeliverAsync(task)).Accepted);
                Assert.Equal(SettleStatus.Succeeded, (await h.Service.SettledAsync(id)).Result.Status);
            }

            string Answer(string id) => File.ReadAllLines(Path.Combine(h.WorkTreeOf(id), "credentials.txt")).Single().Split(' ', 2)[1];
            Assert.Equal("owner-pass", Answer("dlv_owner"));
            Assert.Equal("run-token", Answer("dlv_other"));
        }
        finally
        {
            RunWorkspace.TryDelete(home);
        }
    }

    private static IRunSandbox Isolation(string workRoot) => Isolation(workRoot, new NoRepoCredentials());

    private static IRunSandbox Isolation(string workRoot, IRepoCredentialSource credentials) =>
        RunIsolation.Create(new RunIsolationOptions { WorkRoot = workRoot, HomeDirectory = Home, Credentials = credentials, MachineName = "test-machine" }, TextWriter.Null);

    /// <summary>What git answers for <paramref name="url"/> with only the run's git settings, or null.</summary>
    private static string? GitPassword(string git, string runConfig, string url)
    {
        var info = new ProcessStartInfo(git) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("credential");
        info.ArgumentList.Add("fill");
        info.Environment["GIT_CONFIG_GLOBAL"] = runConfig;
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment.Remove("GIT_ASKPASS");
        info.Environment.Remove("SSH_ASKPASS");
        using var process = Process.Start(info)!;
        process.StandardInput.Write($"url={url}\n\n");
        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        _ = errors.Result;
        return process.ExitCode != 0 ? null
            : output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("password=", StringComparison.Ordinal))?["password=".Length..];
    }

    private sealed class FixedTelemetry(RunTelemetrySettings settings) : IRunTelemetrySource
    {
        public Task<RunTelemetrySettings?> IssueAsync(string deliveryId, CancellationToken cancellationToken) => Task.FromResult<RunTelemetrySettings?>(settings);
    }

    /// <summary>An issuer that gives every run the same token for whatever repository it asks about, so any scoping comes from the runner.</summary>
    private sealed class IssueEveryone(string token) : IRepoCredentialSource
    {
        public string Description => "Test tokens.";

        public Task<RepoCredential?> IssueAsync(RepoCredentialRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<RepoCredential?>(new RepoCredential { UserName = "x-access-token", Secret = token, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
    }
}
