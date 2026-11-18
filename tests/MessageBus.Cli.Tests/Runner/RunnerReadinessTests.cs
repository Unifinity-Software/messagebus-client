using MessageBus.Cli.Readiness;
using MessageBus.Cli.Runner;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

/// <summary>
/// <c>mbus runner</c> as it starts (<see cref="RunnerCommand.RunAsync"/>): what it does when the machine,
/// not the task, is the problem (the original issue, the original issue), and the per-user tool folders each run gets on PATH (the original issue).
/// The fake agent stands in for Claude Code and Codex; the service records what the runner sends.
/// </summary>
public sealed class RunnerReadinessTests
{
    [Fact]
    public async Task A_run_that_cant_use_its_sign_in_is_handed_back_and_that_agent_takes_no_work_until_a_scan_finds_it_ready()
    {
        await using var h = new RunnerHarness();
        await using var runner = await Runner.StartAsync(h);
        Assert.Null((await h.Service.NextInventoryAsync()).Agents.Single(a => a.Kind == AgentKind.ClaudeCode).NotReady);

        // Claude Code answers "Not logged in" (the keychain over SSH on macOS): not the task's fault.
        Assert.True((await runner.DeliverAsync(h.Task("dlv_out", directives: d => d["mode"] = "signed-out"))).Accepted);
        var handedBack = await h.Service.SettledAsync("dlv_out");
        Assert.Equal(DeclineReason.MachineNotReady, handedBack.Declined);
        Assert.Equal(SettleStatus.Abandoned, handedBack.Result.Status);

        // The service is told at once, so it stops offering Claude Code here; Codex is still fine.
        var told = await h.Service.NextInventoryAsync();
        Assert.NotNull(told.Agents.Single(a => a.Kind == AgentKind.ClaudeCode).NotReady);
        Assert.Null(told.Agents.Single(a => a.Kind == AgentKind.Codex).NotReady);
        Assert.DoesNotContain("agent:claude-code", told.Capabilities);
        Assert.Contains("agent:codex", told.Capabilities);

        // An offer made anyway (the service's view was older) is declined without running, and the runner scans again.
        var declined = await runner.DeliverAsync(h.Task("dlv_stale"));
        Assert.Equal((false, DeclineReason.MachineNotReady), (declined.Accepted, declined.Reason));
        Assert.True((await runner.DeliverAsync(h.Task("dlv_codex", "codex"))).Accepted);
        Assert.Equal(SettleStatus.Succeeded, (await h.Service.SettledAsync("dlv_codex")).Result.Status);

        // That scan finds Claude Code ready (fixed, say, with claude setup-token), and it takes work again.
        Assert.Null((await h.Service.NextInventoryAsync()).Agents.Single(a => a.Kind == AgentKind.ClaudeCode).NotReady);
        Assert.True((await runner.DeliverAsync(h.Task("dlv_fixed"))).Accepted);
        Assert.Equal(SettleStatus.Succeeded, (await h.Service.SettledAsync("dlv_fixed")).Result.Status);
        Assert.False(File.Exists(Path.Combine(h.WorkTreeOf("dlv_stale"), "fake-agent.txt")));
    }

    [Fact]
    public async Task Each_run_gets_the_per_user_tool_folders_that_exist_on_its_PATH()
    {
        await using var h = new RunnerHarness();
        var home = Directory.CreateDirectory(Path.Combine(h.Scratch, "home")).FullName;
        var dotnet = Directory.CreateDirectory(Path.Combine(home, ".dotnet")).FullName;
        var localBin = Directory.CreateDirectory(Path.Combine(home, ".local", "bin")).FullName;
        await using var runner = await Runner.StartAsync(h, home);

        Assert.True((await runner.DeliverAsync(h.Task("dlv_env", "codex", directives: d => d["env"] = "PATH"))).Accepted);
        await h.Service.SettledAsync("dlv_env");

        // After the runner's own PATH (so a tool already found there still wins), in this order; .dotnet/tools and the others don't exist.
        var path = File.ReadAllLines(Path.Combine(h.WorkTreeOf("dlv_env"), "env.txt")).Single()["PATH=".Length..];
        var own = Environment.GetEnvironmentVariable("PATH")!.TrimEnd(Path.PathSeparator);
        Assert.StartsWith(own, path, StringComparison.Ordinal);
        var added = path[own.Length..].Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal([dotnet, localBin], added.Where(d => d.StartsWith(home, StringComparison.Ordinal)));
    }

    [Fact]
    public void A_reason_names_this_mbus_only_in_the_runners_own_log_and_goes_to_the_service_unchanged()
    {
        // the original issue: the full path holds the user's folder names, so it stays on this machine.
        var log = new StringWriter();
        var readiness = new RunnerReadiness(log, advice: new MessageBus.Cli.Hosting.MbusOnPath("/home/jane/mbus/mbus", null) { Windows = false }.Advice);
        var inventory = new MachineInventory
        {
            MachineId = "mch_test", HostName = "test-machine", Os = MachineOs.Linux, Architecture = CpuArchitecture.X64, CliVersion = "0.1.0",
            Agents = [new DetectedAgent { Kind = AgentKind.Codex, Found = true, NotReady = "Codex isn't signed in. Fix: run codex login, then mbus connect --rescan." }],
            ScannedAt = DateTimeOffset.UnixEpoch,
        };

        var sent = readiness.Scanned(inventory, _ => null);

        Assert.Equal("Codex isn't signed in. Fix: run codex login, then mbus connect --rescan.", sent.Agents.Single().NotReady);
        Assert.Contains("then /home/jane/mbus/mbus connect --rescan.", log.ToString());
    }

    [Fact]
    public void DOTNET_ROOT_is_set_only_when_the_dotnet_a_run_finds_is_the_per_user_one()
    {
        var root = Path.Combine(Path.GetTempPath(), "mbus-toolpaths-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dotnetRoot = Directory.CreateDirectory(Path.Combine(root, "home", ".dotnet")).FullName;
            var system = Directory.CreateDirectory(Path.Combine(root, "system")).FullName;
            var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            File.WriteAllText(Path.Combine(dotnetRoot, exe), "");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Combine(dotnetRoot, exe), UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            Func<string, string?> Env(string path) => name => name switch { "PATH" => path, "PATHEXT" => ".EXE", _ => null };

            var perUser = ToolPaths.RunVariables(Path.Combine(root, "home"), Env(system));
            Assert.Equal(dotnetRoot, perUser["DOTNET_ROOT"]);
            Assert.Contains(dotnetRoot, perUser["PATH"].Split(Path.PathSeparator));

            // A dotnet already on PATH wins, so it keeps finding its own SDKs.
            File.WriteAllText(Path.Combine(system, exe), "");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Combine(system, exe), UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            Assert.False(ToolPaths.RunVariables(Path.Combine(root, "home"), Env(system)).ContainsKey("DOTNET_ROOT"));
        }
        finally
        {
            RunWorkspace.TryDelete(root);
        }
    }

    /// <summary><c>mbus runner</c> started the way the command starts it, with a connection that hands the test its routes.</summary>
    private sealed class Runner : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private Task<int> _running = Task.FromResult(0);
        private RunnerRouteDispatcher _routes = null!;

        public static async Task<Runner> StartAsync(RunnerHarness h, string? home = null)
        {
            var runner = new Runner();
            var transport = new HeldTransport();
            var options = new RunnerOptions
            {
                Slots = 2,
                WorkRoot = h.WorkRoot,
                KeepRunFolders = true,
                AgentCommands = new Dictionary<AgentKind, string> { [AgentKind.ClaudeCode] = RunnerHarness.FakeAgentPath, [AgentKind.Codex] = RunnerHarness.FakeAgentPath },
            };
            home ??= Directory.CreateDirectory(Path.Combine(h.Scratch, "home")).FullName;
            runner._running = RunnerCommand.RunAsync(new Host(h.Service, home),
                options, transport, TextWriter.Null, h.Time, runner._stop.Token);
            runner._routes = await transport.Serving.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return runner;
        }

        public async Task<DeliveryDecision> DeliverAsync(TaskDelivery delivery)
        {
            var response = await _routes.HandleAsync("POST", RunnerRoutes.Deliver, MessageBusJson.Serialize(delivery), TestContext.Current.CancellationToken);
            return MessageBusJson.Deserialize<DeliveryDecision>(response.Json!)!;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _running.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
            _stop.Dispose();
        }
    }

    private sealed class HeldTransport : IRunnerTransport
    {
        private readonly TaskCompletionSource<RunnerRouteDispatcher> _serving = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RunnerRouteDispatcher> Serving => _serving.Task;

        public async Task RunAsync(RunnerRouteDispatcher dispatcher, CancellationToken cancellationToken)
        {
            _serving.TrySetResult(dispatcher);
            var stopped = new TaskCompletionSource();
            using (cancellationToken.Register(() => stopped.TrySetResult()))
            {
                await stopped.Task;
            }
        }
    }

    /// <summary>A machine whose scan finds Claude Code and Codex ready, with the real PATH.</summary>
    private sealed class Host(RecordingService service, string home) : IRunnerHostContext
    {
        public Uri ServerUrl { get; } = new("http://127.0.0.1:9");
        public string? MachineId => "mch_test";
        public string MachineName => "test-machine";
        public string? Location => null;
        public RunnerPolicy Policy => RunnerPolicy.Workspace;
        public IReadOnlyDictionary<string, AgentKind> AgentIds { get; } = new Dictionary<string, AgentKind>();
        public string HomeDirectory => home;
        public HttpClient CreateServiceClient() => new(service, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1:9/") };
        public IReadOnlyList<KeyValuePair<string, string>> CreateConnectHeaders(string method, string pathAndQuery) => [];
        public string? LocateCommand(string command) => CommandLocator.Find(command, Environment.GetEnvironmentVariable, []);
        public string? GetEnvironmentVariable(string name) => name == "PATH" ? Environment.GetEnvironmentVariable("PATH") : null;

        public Task<MachineInventory> BuildInventoryAsync(int slots, CancellationToken cancellationToken) => Task.FromResult(new MachineInventory
        {
            MachineId = "mch_test", HostName = "test-machine", Os = MachineOs.Linux, Architecture = CpuArchitecture.X64, CliVersion = "0.1.0", Slots = slots,
            Capabilities = ["os:linux", "agent:claude-code", "agent:codex"],
            Agents = [new DetectedAgent { Kind = AgentKind.ClaudeCode, Found = true, SignedIn = true }, new DetectedAgent { Kind = AgentKind.Codex, Found = true, SignedIn = true }],
            ScannedAt = DateTimeOffset.UnixEpoch,
        });
    }
}
