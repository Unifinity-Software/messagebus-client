using MessageBus.Cli.Runner;
using MessageBus.Cli.Tests.Runner;
using MessageBus.Cli.Update;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Releases;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Update;

/// <summary>
/// The issue's "done when": <c>mbus runner</c> at 0.1.0 is asked for an update while a task runs; the
/// task finishes and settles normally, then 0.2.0 from the local feed replaces the binary and the
/// runner exits 75 for its service manager to restart it. How the update ended is kept for the next
/// start to report (the original issue).
/// </summary>
public sealed class RunnerUpdateTests
{
    [Fact]
    public async Task A_runner_asked_to_update_finishes_its_task_then_installs_the_new_version_and_exits_75()
    {
        await using var h = new RunnerHarness();
        using var feed = new TestReleaseFeed();
        var installed = File.ReadAllText(feed.InstallCurrent("0.1.0"));
        var release = feed.Publish("0.2.0");
        var record = RunnerUpdateRecord.In(Path.Combine(h.Scratch, "settings"));
        var updater = Updater(feed, record);
        var transport = new HeldTransport();
        var options = new RunnerOptions
        {
            Slots = 2,
            WorkRoot = Path.Combine(h.Scratch, "runs"),
            AgentCommands = new Dictionary<AgentKind, string> { [AgentKind.ClaudeCode] = RunnerHarness.FakeAgentPath },
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var log = new StringWriter();
        var runner = RunnerCommand.RunAsync(new Host(h.Service), options, transport, TextWriter.Synchronized(log), h.Time, stop.Token, updater);
        var routes = await transport.Serving.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var gate = Path.Combine(h.Scratch, "release");
        Assert.True((await DeliverAsync(routes, h.Task("dlv_busy", directives: d => { d["wait"] = gate; d["settle"] = "yes"; }))).Accepted);
        var update = MessageBusJson.Serialize(new UpdateRequest { Version = "0.2.0", Channel = "stable" });
        Assert.Equal(202, (await routes.HandleAsync("POST", RunnerRoutes.Update, update, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Contains("Update to 0.2.0 requested; waiting for 1 running task to finish: Add due dates (Claude Code).", log.ToString());
        Assert.DoesNotContain("Starting to install", log.ToString());

        // Draining: no new work, the running task carries on, and nothing is replaced yet.
        Assert.Equal(DeclineReason.Draining, (await DeliverAsync(routes, h.Task("dlv_new"))).Reason);
        Assert.False(runner.IsCompleted);
        Assert.Equal(installed, File.ReadAllText(feed.Target));

        File.WriteAllText(gate, "");
        var settle = await h.Service.SettledAsync("dlv_busy");
        var exitCode = await runner.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(SettleStatus.Succeeded, settle.Result.Status);
        Assert.Equal(RunnerCommand.UpdateExitCode, exitCode);
        Assert.Contains("Starting to install mbus 0.2.0.", log.ToString());
        // the original issue: the Runners page followed it: waiting for the task, installing, then restarting.
        Assert.Equal([RunnerUpdateStage.Waiting, RunnerUpdateStage.Installing, RunnerUpdateStage.Restarting],
            new[] { await h.Service.NextUpdateProgressAsync(), await h.Service.NextUpdateProgressAsync(), await h.Service.NextUpdateProgressAsync() }.Select(p => p.Stage));
        Assert.Equal(release, File.ReadAllText(feed.Target));
        Assert.Equal((RunnerUpdateOutcome.Updated, "0.1.0", "0.2.0"), Summary(record.Load("0.2.0", toolInstall: null)));
        // Started again on the old version all the same (its service manager ran the old file): that reads as rolled back.
        Assert.Equal(RunnerUpdateOutcome.RolledBack, record.Load("0.1.0", toolInstall: null)?.Outcome);
    }

    [Fact]
    public async Task A_runner_whose_new_version_fails_to_start_puts_the_old_one_back_says_why_and_keeps_taking_work()
    {
        await using var h = new RunnerHarness();
        using var feed = new TestReleaseFeed();
        var installed = File.ReadAllText(feed.InstallCurrent("0.1.0"));
        feed.Publish("0.2.0", startExitCode: 1);
        // The persisted result belongs to a standalone versioned install, as a real runner now uses (the original issue).
        var current = VersionedInstall.NewVersionFile(feed.Target, "0.1.0");
        File.Copy(feed.Target, current);
        VersionedInstall.Switch(feed.Target, current, "0.1.0");
        var record = RunnerUpdateRecord.In(Path.Combine(h.Scratch, "settings"));
        var options = new RunnerOptions { Slots = 1, WorkRoot = Path.Combine(h.Scratch, "runs") };
        RunnerExtensions Reporting(HttpClient _) => new() { LastUpdate = () => record.Load("0.1.0", toolInstall: null) };

        var transport = new HeldTransport();
        using var stopFirst = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = RunnerCommand.RunAsync(new Host(h.Service), options, transport, TextWriter.Null, h.Time, stopFirst.Token,
            Updater(feed, record), Reporting);
        var routes = await transport.Serving.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Null((await h.Service.NextInventoryAsync()).LastUpdate); // it never updated before
        await routes.HandleAsync("POST", RunnerRoutes.Update, MessageBusJson.Serialize(new UpdateRequest { Version = "0.2.0" }), TestContext.Current.CancellationToken);

        // the original issue: not installed. The old mbus stays, the runner says why at once and doesn't exit: it takes work again.
        Assert.Equal(RunnerUpdateStage.Installing, (await h.Service.NextUpdateProgressAsync()).Stage);
        var ended = await h.Service.NextUpdateProgressAsync();
        Assert.Equal(RunnerUpdateStage.Ended, ended.Stage);
        Assert.Equal((RunnerUpdateOutcome.RolledBack, "0.1.0", "0.2.0"), Summary(ended.Report));
        Assert.Equal(installed, File.ReadAllText(feed.Target));
        Assert.False(runner.IsCompleted);
        Assert.NotEqual(DeclineReason.Draining, (await DeliverAsync(routes, h.Task("dlv_after"))).Reason);
        await stopFirst.CancelAsync();
        await runner.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Started again later, it still says how that update ended.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var againTransport = new HeldTransport();
        var again = RunnerCommand.RunAsync(new Host(h.Service), options, againTransport, TextWriter.Null, h.Time, stop.Token, null, Reporting);
        var report = (await h.Service.NextInventoryAsync()).LastUpdate;
        Assert.Equal((RunnerUpdateOutcome.RolledBack, "0.1.0", "0.2.0"), Summary(report));
        Assert.Contains("didn't start", report!.Reason);
        Assert.Contains("code 1", report.Reason); // what the new mbus did, for the Runners page
        // Stopped only once it has started, not while it still reads its work policy (a race under load).
        await againTransport.Serving.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await again.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_runner_that_cant_update_itself_says_so_refuses_the_request_with_the_reason_and_keeps_taking_work()
    {
        await using var h = new RunnerHarness();
        const string why = "This mbus is a build from source and can't update itself.";
        var options = new RunnerOptions { Slots = 1, WorkRoot = Path.Combine(h.Scratch, "runs") };
        var transport = new HeldTransport();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = RunnerCommand.RunAsync(new Host(h.Service), options, transport, TextWriter.Null, h.Time, stop.Token, updater: null,
            _ => new RunnerExtensions { Updates = new RunnerUpdateAbility { CanUpdate = false, Reason = why } });
        var routes = await transport.Serving.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        // The Runners page learns it from the inventory, so it shows the reason instead of an Update button.
        Assert.Equal(new RunnerUpdateAbility { CanUpdate = false, Reason = why }, (await h.Service.NextInventoryAsync()).Updates);

        await routes.HandleAsync("POST", RunnerRoutes.Update, MessageBusJson.Serialize(new UpdateRequest { Version = "latest" }), TestContext.Current.CancellationToken);

        var ended = await h.Service.NextUpdateProgressAsync();
        Assert.Equal((RunnerUpdateStage.Ended, RunnerUpdateOutcome.NotInstalled, why), (ended.Stage, ended.Report?.Outcome, ended.Report?.Reason));
        // It was asked while it could have been busy: it neither stops taking work nor exits.
        Assert.False(runner.IsCompleted);
        Assert.NotEqual(DeclineReason.Draining, (await DeliverAsync(routes, h.Task("dlv_after"))).Reason);
        await stop.CancelAsync();
        await runner.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dotnet_runner_keeps_working_then_rechecks_holders_after_the_drain_before_exit_75()
    {
        await using var h = new RunnerHarness();
        var updater = new SessionUpdater();
        var transport = new HeldTransport();
        var options = new RunnerOptions { Slots = 2, WorkRoot = Path.Combine(h.Scratch, "dotnet-runs"),
            AgentCommands = new Dictionary<AgentKind, string> { [AgentKind.ClaudeCode] = RunnerHarness.FakeAgentPath } };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = RunnerCommand.RunAsync(new Host(h.Service), options, transport, TextWriter.Null, h.Time, stop.Token, updater);
        var routes = await transport.Serving.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await routes.HandleAsync("POST", RunnerRoutes.Update, MessageBusJson.Serialize(new UpdateRequest { Version = "2.0.0" }), TestContext.Current.CancellationToken);
        Assert.Equal(2, (await h.Service.NextUpdateProgressAsync()).BlockedBy?.Count);
        var gate = Path.Combine(h.Scratch, "dotnet-release");
        Assert.True((await DeliverAsync(routes, h.Task("dlv_dotnet", directives: d => { d["wait"] = gate; d["settle"] = "yes"; }))).Accepted);
        updater.Count = 0;
        h.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(RunnerUpdateStage.Waiting, (await h.Service.NextUpdateProgressAsync()).Stage);
        Assert.Equal(DeclineReason.Draining, (await DeliverAsync(routes, h.Task("dlv_drain"))).Reason);
        Assert.Equal(0, updater.Calls);
        File.WriteAllText(gate, "");
        await h.Service.SettledAsync("dlv_dotnet");
        // Simulate a holder appearing between the initial scan and InstallAsync's final check.
        Assert.Equal(1, (await h.Service.NextUpdateProgressAsync()).BlockedBy?.Count);
        Assert.False(runner.IsCompleted);
        updater.Count = 0;
        h.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(RunnerUpdateStage.Waiting, (await h.Service.NextUpdateProgressAsync()).Stage);
        Assert.Equal(RunnerUpdateStage.Restarting, (await h.Service.NextUpdateProgressAsync()).Stage);
        Assert.Equal(75, await runner.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(2, updater.Calls);
    }

    private sealed class SessionUpdater : ISessionRunnerUpdater
    {
        public int Count { get; set; } = 2;
        public int Calls { get; private set; }
        public string CurrentVersion => "1.0.0";
        public bool WaitingAgain { get; private set; }
        public UpdateRequest? Waiting { get; private set; }
        public void KeepWaiting(UpdateRequest request) => Waiting = request;
        public void ClearWaiting() => Waiting = null;
        public void ConfirmConnected() { }
        public IReadOnlyList<ToolInstallHolder> Holders(bool final = false) => Enumerable.Range(1, Count).Select(i => new ToolInstallHolder(i, "mbus")).ToArray();
        public Task<bool> InstallAsync(UpdateRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            WaitingAgain = Calls == 1;
            if (WaitingAgain) Count = 1;
            return Task.FromResult(!WaitingAgain);
        }
    }

    // the original issue: after installing, a runner started in a terminal starts the new version itself; one a service manager started
    // (always with --log-file when mbus runner install set it up, or directly by launchd, systemd or a container's init)
    // exits 75 for it. MBUS_RUNNER_SUPERVISED overrides the guess either way.
    [Theory]
    [InlineData(false, null, 4242, "zsh", false)]            // ssh session or Terminal on macOS/Linux
    [InlineData(false, null, null, null, false)]              // a Windows terminal (no parent check)
    [InlineData(true, null, 4242, "zsh", true)]               // mbus runner install (--log-file)
    [InlineData(false, null, 1, "launchd", true)]             // launchd agent, or a container's init
    [InlineData(false, null, 812, "systemd", true)]           // systemd --user unit
    [InlineData(false, "1", 4242, "bash", true)]              // a supervisor of your own
    [InlineData(true, "0", 1, "launchd", false)]              // told to restart itself
    public void A_runner_restarts_itself_after_an_update_only_when_no_service_manager_started_it(
        bool logFile, string? supervisedVariable, int? parentId, string? parentName, bool supervised)
    {
        (int, string?)? parent = parentId is { } id ? (id, parentName) : null;
        Assert.Equal(supervised, RunnerRestart.IsSupervised(logFile, name => name == RunnerRestart.SupervisedEnvVar ? supervisedVariable : null, parent));
    }

    private static RunnerSelfUpdater Updater(TestReleaseFeed feed, RunnerUpdateRecord record) => new(_ => new SelfUpdater(new SelfUpdaterOptions
    {
        Source = new FolderReleaseSource(feed.ChannelFolder),
        PublicKey = feed.PublicKey,
        CurrentVersion = "0.1.0",
        TargetPath = feed.Target,
    }), TextWriter.Null, record, "0.1.0");

    private static (RunnerUpdateOutcome?, string?, string?) Summary(RunnerUpdateReport? report) => (report?.Outcome, report?.FromVersion, report?.ToVersion);

    private static async Task<DeliveryDecision> DeliverAsync(RunnerRouteDispatcher routes, TaskDelivery delivery)
    {
        var response = await routes.HandleAsync("POST", RunnerRoutes.Deliver, MessageBusJson.Serialize(delivery), TestContext.Current.CancellationToken);
        return MessageBusJson.Deserialize<DeliveryDecision>(response.Json!)!;
    }

    /// <summary>A connection that hands the test the runner's routes and stays open until the runner stops it.</summary>
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

    private sealed class Host(RecordingService service) : IRunnerHostContext
    {
        public Uri ServerUrl { get; } = new("http://127.0.0.1:9");
        public string? MachineId => "mch_test";
        public string MachineName => "test-machine";
        public string? Location => null;
        public RunnerPolicy Policy => RunnerPolicy.Workspace;
        public IReadOnlyDictionary<string, AgentKind> AgentIds { get; } = new Dictionary<string, AgentKind>();
        // Not the temp folder itself: the Linux sandbox (the original issue) hides a home folder's own files from runs, and these tests
        // keep their gate files in the temp folder. Never created, so there is nothing to hide.
        public string HomeDirectory => Path.Combine(Path.GetTempPath(), "mbus-test-home");
        public HttpClient CreateServiceClient() => new(service, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1:9/") };
        public IReadOnlyList<KeyValuePair<string, string>> CreateConnectHeaders(string method, string pathAndQuery) => [];
        public string? LocateCommand(string command) => CommandLocator.Find(command, Environment.GetEnvironmentVariable, []);
        public Task<MachineInventory> BuildInventoryAsync(int slots, CancellationToken cancellationToken) =>
            Task.FromResult(RunnerEnvironment.BasicInventory(this, slots, DateTimeOffset.UnixEpoch));
        public string? GetEnvironmentVariable(string name) => null;
    }
}
