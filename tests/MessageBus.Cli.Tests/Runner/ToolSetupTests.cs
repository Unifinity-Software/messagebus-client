using System.Net;
using System.Net.Http.Json;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class ToolSetupTests
{
    [Fact]
    public async Task Installs_only_after_one_use_claim_then_verifies_and_reports_actual_version()
    {
        await using var h = new SetupHarness();
        h.Setup.Start();
        await h.Setup.Pending;
        Assert.Equal("succeeded", h.Setup.Status.State);
        Assert.Equal("2.51.0.windows.1", h.Setup.Status.AfterVersion);
        Assert.Equal(["approval", "claim", "install", "rescan", "result"], h.Order);
        var result = Assert.Single(h.Results);
        Assert.Equal(h.Plan, result.Plan);
        Assert.Equal(result.ApprovalId, result.Report.ApprovalId);
        Assert.Equal(result.Plan, result.Report.Plan);
        Assert.Equal(0, result.Report.ExitCode);
        Assert.True(h.PauseReleased);
    }

    [Theory]
    [InlineData("declined", false, "declined")]
    [InlineData("approved", true, "declined")]
    public async Task Declined_or_expired_approvals_never_claim_or_install(string decision, bool expired, string expected)
    {
        await using var h = new SetupHarness(decision, expired);
        h.Setup.Start();
        await h.Setup.Pending;
        Assert.Equal(expected, h.Setup.Status.State);
        Assert.Equal(["approval"], h.Order);
        Assert.Empty(h.Results);
    }

    [Fact]
    public async Task Reused_claim_is_refused_without_starting_an_installer()
    {
        await using var h = new SetupHarness(claimStatus: HttpStatusCode.Conflict);
        h.Setup.Start();
        await h.Setup.Pending;
        Assert.Equal("failed", h.Setup.Status.State);
        Assert.Equal(["approval", "claim"], h.Order);
        Assert.Empty(h.Results);
    }

    [Fact]
    public async Task Successful_installer_exit_is_not_success_without_working_matching_git()
    {
        await using var h = new SetupHarness(afterVersion: "2.49.0.windows.1");
        h.Setup.Start();
        await h.Setup.Pending;
        Assert.Equal("failed", h.Setup.Status.State);
        Assert.Equal("2.49.0.windows.1", Assert.Single(h.Results).Report.AfterVersion);
        Assert.Contains("did not verify", h.Setup.Status.Detail);
    }

    [Fact]
    public async Task Setup_gate_prevents_tasks_and_does_not_overlap_active_runs()
    {
        await using var h = new RunnerHarness();
        using (var idle = h.Engine.TryPauseForToolSetup())
        {
            Assert.NotNull(idle);
            Assert.Null(h.Engine.TryPauseForToolSetup());
            Assert.Equal(DeclineReason.Draining, (await h.DeliverAsync(h.Task("dlv_setup_blocked"))).Reason);
            Assert.True(h.Engine.GetStatus().Draining);
        }
        Assert.False(h.Engine.GetStatus().Draining);
        Assert.True((await h.DeliverAsync(h.Task("dlv_active", directives: d => d["mode"] = "hang"))).Accepted);
        Assert.Null(h.Engine.TryPauseForToolSetup());
    }

    [Fact]
    public async Task Local_opt_out_stops_before_even_reserving_the_runner_or_contacting_service()
    {
        await using var h = new SetupHarness(disabled: "The owner hasn't opted in.");
        Assert.Equal("disabled", h.Setup.Start().State);
        await h.Setup.Pending;
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("The owner hasn't opted in.", (await h.Setup.CurrentAsync(ct)).Detail);
        // A machine that already has Git is told so rather than asked to opt in; the opt-in still guards Start.
        h.GitWorks = true;
        var current = await h.Setup.CurrentAsync(ct);
        Assert.Equal(("installed", "Git 2.51.0.windows.1 is installed."), (current.State, current.Detail));
        Assert.Equal("disabled", h.Setup.Start().State);
        Assert.Empty(h.Order);
        Assert.False(h.PauseTaken);
    }

    [Theory]
    [InlineData("Git [Git.Git]\nVersion\n-------\n2.51.0.1\n2.50.0\n", "2.51.0.1")]
    [InlineData("Git 2.51.0\n--version 2.51.0 & calc.exe\n", null)]
    public void Version_resolution_accepts_only_numeric_package_rows(string output, string? expected) =>
        Assert.Equal(expected, WindowsGitSetup.ReadVersionList(output));

    [Fact]
    public async Task Local_single_process_lock_prevents_duplicate_installs()
    {
        await using var h = new SetupHarness();
        Directory.CreateDirectory(h.Root);
        using var other = new FileStream(Path.Combine(h.Root, "tool-setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal("failed", h.Setup.Start().State);
        await h.Setup.Pending;
        Assert.Empty(h.Order);
        Assert.True(h.PauseReleased);
    }

    [Fact]
    public async Task Audit_result_survives_an_outage_and_replays_without_repeating_installation()
    {
        await using var h = new SetupHarness();
        h.RefuseResult = true;
        h.Setup.Start();
        await h.Setup.Pending;
        Assert.Equal("succeeded", h.Setup.Status.State);
        Assert.True(File.Exists(Path.Combine(h.Root, "tool-setup-result.json")));
        h.RefuseResult = false;
        await h.Setup.RecoverAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(h.Root, "tool-setup-result.json")));
        Assert.Single(h.Order, x => x == "install");
        Assert.Single(h.Results);
    }

    [Fact]
    public async Task Unknown_native_outcome_keeps_work_and_new_installs_blocked_across_restart_until_local_acknowledgement()
    {
        await using var h = new SetupHarness();
        h.InstallException = new CliException("The elevated child could not be stopped.");
        h.Setup.Start();
        await h.Setup.Pending;
        Assert.True(h.Setup.BlocksWork);
        Assert.Equal("failed", h.Setup.Status.State);
        Assert.False(h.Setup.Status.CanStart);
        Assert.Contains("stays paused", h.Setup.Status.Detail);
        var calls = h.Order.Count;
        Assert.Equal("failed", h.Setup.Start().State);
        Assert.Equal(calls, h.Order.Count);
        var restarted = h.NewSetup();
        Assert.True(restarted.BlocksWork);
        Assert.Contains("acknowledge-interrupted", restarted.Status.Detail);
        await using (var runner = new RunnerHarness(toolSetup: restarted))
        {
            Assert.True(runner.Engine.GetStatus().Draining);
            Assert.Equal(DeclineReason.Draining, (await runner.DeliverAsync(runner.Task("dlv_interrupted"))).Reason);
            Assert.Null(runner.Engine.TryPauseForToolSetup());
            runner.Engine.RequestUpdate(new UpdateRequest { Version = "0.2.0" });
            Assert.False(runner.Engine.UpdateReady.IsCompleted);
            ToolSetupRequests.AcknowledgeInterrupted(h.Root);
            Assert.False(restarted.BlocksWork);
            Assert.True(restarted.Status.CanStart);
            runner.Time.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal("0.2.0", (await runner.Engine.UpdateReady.WaitAsync(TestContext.Current.CancellationToken)).Version);
        }
    }

    [Fact]
    public async Task Recovery_and_acknowledgement_cannot_touch_an_active_installers_placeholder()
    {
        await using var h = new SetupHarness();
        h.InstallGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Setup.Start();
        await h.Installing.Task;
        Assert.Equal("installing", h.Setup.Status.State);
        Assert.True(File.Exists(Path.Combine(h.Root, "tool-setup-result.json")));
        var second = h.NewSetup();
        await second.RecoverAsync(TestContext.Current.CancellationToken);
        Assert.Empty(h.Results);
        Assert.True(File.Exists(Path.Combine(h.Root, "tool-setup-result.json")));
        Assert.Throws<CliException>(() => ToolSetupRequests.AcknowledgeInterrupted(h.Root));
        h.InstallGate.SetResult();
        await h.Setup.Pending;
        Assert.Equal("succeeded", h.Setup.Status.State);
        Assert.False(h.Setup.BlocksWork);
        Assert.Single(h.Results);
        await second.StopAsync();
    }

    [Fact]
    public async Task Verified_install_stays_successful_when_readiness_delivery_fails_and_retries_without_changing_audited_result()
    {
        await using var h = new SetupHarness();
        h.RescanFailures = 1;
        h.Setup.Start();
        await h.Setup.Pending;
        Assert.Equal("succeeded", h.Setup.Status.State);
        Assert.Contains("will retry", h.Setup.Status.Detail);
        var audited = Assert.Single(h.Results).Report;
        Assert.Equal("succeeded", audited.State);
        h.Time.Advance(TimeSpan.FromSeconds(15));
        await h.RescanSucceeded.Task;
        Assert.Single(h.Results);
        Assert.Equal(audited, h.Results[0].Report);
        Assert.False(h.Setup.BlocksWork);
    }

    [Fact]
    public async Task An_agent_task_cannot_acknowledge_the_local_pause_or_resume_work()
    {
        using var machine = new TestMachine();
        var marker = machine.WriteHomeFile(".mbus/" + ToolSetupRequests.InterruptedFile,
            MessageBusJson.Serialize(new ToolSetupReport { State = "failed", Detail = ToolSetupRequests.InterruptedDetail }));
        machine.Env[RunToolsVariables.File] = "a-task-credential.json";
        var refused = await machine.RunAsync("machine", "setup", "git", "--acknowledge-interrupted");
        Assert.NotEqual(0, refused.ExitCode);
        Assert.True(File.Exists(marker));
        machine.Env.Remove(RunToolsVariables.File);
        // the original issue: this command acts on the real machine, so the real macOS sandbox refuses it too.
        Assert.SkipWhen(OperatingSystem.IsMacOS() && MessageBus.Cli.Security.MacSecurity.InSandbox(),
            "These tests run inside a run's macOS sandbox, where mbus refuses tool setup; the refusal above was checked.");
        var accepted = await machine.RunAsync("machine", "setup", "git", "--acknowledge-interrupted");
        Assert.Equal(0, accepted.ExitCode);
        Assert.False(File.Exists(marker));
        Assert.Empty(machine.Server.Requests);
    }

    [Theory]
    [InlineData("declined", true)]
    [InlineData("approved", false)]
    public async Task Mac_setup_requires_a_claim_before_native_authorization_and_reports_actual_Git_separately_from_CLT(string decision, bool noInstall)
    {
        using var machine = MacGitSetupTests.Machine();
        var system = new FakeMacToolSetupSystem();
        await using var h = new SetupHarness(decision: decision, platform: new MacGitSetup(machine.CreateHost(), system));
        h.Setup.Start();
        await h.Setup.Pending;
        Assert.Equal(FakeMacToolSetupSystem.Plan, h.Plan);
        Assert.DoesNotContain(system.ReadCalls, call => call.Executable == "/usr/bin/git");
        if (noInstall)
        {
            Assert.Empty(system.InstallCalls);
            Assert.DoesNotContain("claim", h.Order);
            Assert.Equal("declined", h.Setup.Status.State);
        }
        else
        {
            var install = Assert.Single(system.InstallCalls);
            Assert.Equal("/usr/bin/osascript", install.Executable);
            Assert.Equal(["-e", ToolSetupCatalog.AppleAuthorizationScript(FakeMacToolSetupSystem.Plan)], install.Arguments);
            Assert.Equal(["approval", "claim", "rescan", "result"], h.Order);
            Assert.Equal("succeeded", h.Setup.Status.State);
            Assert.Equal("2.50.1", Assert.Single(h.Results).Report.AfterVersion);
            Assert.False(h.Setup.BlocksWork);
        }
    }

    private sealed class SetupHarness : HttpMessageHandler, IToolSetupPlatform, IAsyncDisposable
    {
        private readonly string _decision;
        private readonly bool _expired;
        private readonly HttpStatusCode _claimStatus;
        private readonly string? _after;
        private readonly HttpClient _client;
        private bool _installed;
        public SetupHarness(string decision = "approved", bool expired = false, HttpStatusCode claimStatus = HttpStatusCode.OK,
            string? afterVersion = "2.51.0.windows.1", string? disabled = null, IToolSetupPlatform? platform = null)
        {
            _decision = decision;
            _expired = expired;
            _claimStatus = claimStatus;
            _after = afterVersion;
            DisabledReason = disabled;
            Platform = platform ?? this;
            _client = new HttpClient(this) { BaseAddress = new Uri("https://mbus.invalid/") };
            Setup = NewSetup();
        }
        public ToolSetupRequests NewSetup() => new(Platform, _client, () => { PauseTaken = true; return new Release(() => PauseReleased = true); },
            _ =>
            {
                Order.Add("rescan");
                if (RescanFailures-- > 0) throw new HttpRequestException("Service unavailable");
                RescanSucceeded.TrySetResult();
                return Task.CompletedTask;
            }, Root, new Uri("https://mbus.invalid/"), TextWriter.Null, Time);
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mbus-tool-setup-tests", Guid.NewGuid().ToString("N"));
        public FakeTimeProvider Time { get; } = new();
        public ToolSetupRequests Setup { get; }
        private IToolSetupPlatform Platform { get; }
        public List<string> Order { get; } = [];
        public List<ToolSetupResultRequest> Results { get; } = [];
        public ToolSetupPlan? Plan { get; private set; }
        public bool PauseTaken { get; private set; }
        public bool PauseReleased { get; private set; }
        public bool RefuseResult { get; set; }
        public Exception? InstallException { get; set; }
        public TaskCompletionSource? InstallGate { get; set; }
        public TaskCompletionSource Installing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RescanSucceeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RescanFailures { get; set; }
        public string? DisabledReason { get; }
        public bool GitWorks { set => _installed = value; }
        public Task<string?> GitVersionAsync(CancellationToken ct) => Task.FromResult(_installed ? _after : null);
        public Task<string> ResolveVersionAsync(CancellationToken ct) => Task.FromResult("2.51.0");
        public async Task<int> InstallAsync(ToolSetupPlan plan, CancellationToken ct)
        {
            Assert.Equal(Plan, plan);
            Order.Add("install");
            Installing.TrySetResult();
            if (InstallGate is not null) await InstallGate.Task.WaitAsync(ct);
            if (InstallException is not null) throw InstallException;
            _installed = true;
            return 0;
        }
        public void RefreshPath() { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route == ServiceRoutes.ToolSetupResult)
            {
                Order.Add("result");
                if (RefuseResult) return new(HttpStatusCode.ServiceUnavailable);
                Results.Add((await request.Content!.ReadFromJsonAsync<ToolSetupResultRequest>(MessageBusJson.Options, ct))!);
                return new(HttpStatusCode.NoContent);
            }
            var body = (await request.Content!.ReadFromJsonAsync<ToolSetupApprovalRequest>(MessageBusJson.Options, ct))!;
            Plan ??= body.Plan;
            Assert.Equal(Plan, body.Plan);
            var claiming = route == ServiceRoutes.ToolSetupClaim;
            Order.Add(claiming ? "claim" : "approval");
            if (claiming && _claimStatus != HttpStatusCode.OK) return new(_claimStatus) { Content = new StringContent("Already claimed") };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ToolSetupApprovalState
            {
                ApprovalId = "apr_test", State = _decision, ExpiresAt = Time.GetUtcNow().AddHours(_expired ? -1 : 1),
            }, options: MessageBusJson.Options) };
        }
        public async ValueTask DisposeAsync()
        {
            await Setup.StopAsync();
            _client.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        private sealed class Release(Action release) : IDisposable { public void Dispose() => release(); }
    }
}
