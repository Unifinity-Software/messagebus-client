using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner.Service;
using MessageBus.Cli.Tests.Runner;
using MessageBus.Cli.Update;
using MessageBus.Contracts.Releases;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Update;

public sealed class DotnetToolUpdaterTests : IDisposable
{
    private readonly List<string> _directories = [];
    private string Temp(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        _directories.Add(directory);
        return directory;
    }
    public void Dispose()
    {
        // Leave directories for runner cleanup; remove only the state files owned by these unit tests.
        foreach (var directory in _directories.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.Delete(file);
    }

    private static readonly string ToolPath = Path.Combine(Path.GetTempPath(), "tools");
    private static readonly string DotnetPath = Path.Combine(Path.GetTempPath(), "dotnet");
    private static readonly string MbusPath = Path.Combine(ToolPath, "mbus");
    [Fact]
    public async Task Latest_version_reads_a_gzip_compressed_registration()
    {
        // nuget.org always serves its registration index gzip-compressed (Content-Encoding: gzip).
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var bodies = new Dictionary<string, string>
        {
            ["/v3/index.json"] = $$"""{"resources":[{"@id":"{{origin}}/registration/","@type":"RegistrationsBaseUrl/3.6.0"}]}""",
            ["/registration/messagebus.ai.cli/index.json"] =
                """{"items":[{"items":[{"catalogEntry":{"version":"9.1.0"}},{"catalogEntry":{"version":"9.2.0","listed":false}}]}]}""",
        };
        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < bodies.Count; i++)
            {
                using var connection = await listener.AcceptTcpClientAsync(ct);
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var path = (await reader.ReadLineAsync(ct))!.Split(' ')[1];
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
                using var gzipped = new MemoryStream();
                using (var gzip = new GZipStream(gzipped, CompressionLevel.Fastest, leaveOpen: true))
                    gzip.Write(Encoding.UTF8.GetBytes(bodies[path]));
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\nContent-Length: {gzipped.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
                await stream.WriteAsync(gzipped.ToArray(), ct);
            }
        }, ct);

        Assert.Equal("9.1.0", await DotnetToolUpdater.LatestAsync(origin + "/v3/index.json", "stable", ct));
        await serving;
    }

    [Theory]
    [InlineData(true, "bad%path")]
    [InlineData(true, "bad^path")]
    [InlineData(true, "bad!path")]
    [InlineData(true, "bad\"path")]
    [InlineData(true, "bad\npath")]
    [InlineData(false, "bad\rpath")]
    public void Unsafe_paths_are_refused_before_script_generation(bool windows, string path) =>
        Assert.Throws<IOException>(() => DotnetToolUpdater.Script(windows, path, "assembly", "settings", "apply"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Config_clears_other_sources_and_scripts_quote_paths(bool windows)
    {
        const string path = "/a folder & ' (tool)";
        var config = XDocument.Parse(DotnetToolUpdater.NuGetConfig("https://mirror.example/?a=1&b=2"));
        var sources = config.Root!.Element("packageSources")!;
        Assert.Equal(["clear", "add"], sources.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("https://mirror.example/?a=1&b=2", sources.Element("add")!.Attribute("value")!.Value);
        var script = DotnetToolUpdater.Script(windows, path, path + "/tool.dll", path + "/settings", "apply");
        Assert.Contains(windows ? "DisableDelayedExpansion" : "'\\''", script);
        Assert.Contains("__runner-update apply", script);
        Assert.Contains(windows ? "< NUL" : "</dev/null", script);
    }

    [Theory]
    [InlineData(ServicePlatform.Windows)]
    [InlineData(ServicePlatform.MacOs)]
    public void New_definitions_parse_back_to_the_mbus_command(ServicePlatform platform)
    {
        var root = Path.Combine(Path.GetTempPath(), "service & ' (test)");
        var mbus = Path.Combine(root, "mbus");
        var manager = new ServiceManager(platform, "user", 501, (_, _) => Task.FromResult((0, "")));
        var plan = RunnerServiceDefinitions.Plan(manager, [mbus], ["--slots", "2"], root, root, new Dictionary<string, string>());
        var parsed = RunnerServiceCommand.RegisteredCommand(platform, plan.Definition)!;
        Assert.Equal([mbus, "runner", "--slots", "2", "--log-file", plan.LogPath], parsed.Take(6));
        Assert.True(RunnerServiceCommand.HasUpdateStep(platform, plan.Definition, root, mbus));
        Assert.False(RunnerServiceCommand.HasUpdateStep(platform, plan.Definition, root + "other", mbus));
        Assert.False(RunnerServiceCommand.HasUpdateStep(platform, plan.Definition, root, mbus + "other"));
    }

    [Theory]
    [InlineData("stable", "1.0.0")]
    [InlineData("preview", "1.1.0-preview.2")]
    public void Latest_obeys_channel_and_stable_falls_back_to_preview(string channel, string expected)
    {
        Assert.Equal(expected, DotnetToolUpdater.SelectLatest(["1.0.0", "1.1.0-preview.1", "1.1.0-preview.2"], channel));
        Assert.Equal("1.1.0-preview.2", DotnetToolUpdater.SelectLatest(["1.1.0-preview.1", "1.1.0-preview.2"], "stable"));
    }

    [Theory]
    [InlineData(RunnerUpdateOutcome.Updated, "2.0.0")]
    [InlineData(RunnerUpdateOutcome.NotInstalled, "1.0.0")]
    [InlineData(RunnerUpdateOutcome.RolledBack, "1.0.0")]
    public void Startup_reports_matching_results_as_dotnet_tool_records(RunnerUpdateOutcome outcome, string running)
    {
        var directory = Temp("mbus-update-state-");
        var root = Path.Combine(directory, "update");
        var install = DotnetToolInstallTests.Install(Path.Combine(directory, "tools"), running);
        var record = RunnerUpdateRecord.In(directory);
        var updater = new DotnetToolUpdater(install, directory, DotnetPath, TextWriter.Null, record, running, TimeProvider.System, () => ProcessTable.Empty);
        DotnetToolUpdater.Write(Path.Combine(root, "attempt.json"), new ToolUpdateAttempt("id", "1.0.0", "2.0.0", false, install.ToolsPath!, MbusPath, DotnetPath, 1, false, 0));
        DotnetToolUpdater.Write(Path.Combine(root, "result.json"), new ToolUpdateResult("other", outcome, "diagnostic", false));
        updater.Startup();
        Assert.Null(record.Load(running, install));
        DotnetToolUpdater.Write(Path.Combine(root, "result.json"), new ToolUpdateResult("id", outcome, "diagnostic", false));
        updater.Startup();
        Assert.Equal(outcome, record.Load(running, install)?.Outcome);
        Assert.Contains("dotnet-tool", File.ReadAllText(Path.Combine(directory, RunnerUpdateRecord.FileName)));
    }

    [Fact]
    public void Startup_retries_a_file_in_use_failure_once_and_never_reports_a_stale_attempt()
    {
        var directory = Temp("mbus-update-retry-");
        var root = Path.Combine(directory, "update");
        var install = DotnetToolInstallTests.Install(ToolPath);
        var record = RunnerUpdateRecord.In(directory);
        var updater = new DotnetToolUpdater(install, directory, DotnetPath, TextWriter.Null, record, "1.0.0", TimeProvider.System, () => ProcessTable.Empty);
        DotnetToolUpdater.Write(Path.Combine(root, "attempt.json"), new ToolUpdateAttempt("id", "1.0.0", "2.0.0", false, ToolPath, MbusPath, DotnetPath, 1, false, 0));
        var result = new ToolUpdateResult("id", RunnerUpdateOutcome.NotInstalled, "Access denied", true);
        DotnetToolUpdater.Write(Path.Combine(root, "result.json"), result);
        updater.Startup();
        Assert.Equal("2.0.0", updater.Waiting?.Version);
        Assert.Null(record.Load("1.0.0", install));
        DotnetToolUpdater.Write(Path.Combine(root, "result.json"), result);
        updater.Startup();
        Assert.Null(record.Load("1.0.0", install));
        Assert.True(DotnetToolUpdater.Read<ToolUpdateAttempt>(Path.Combine(root, "attempt.json"))?.RetryPending);
        Assert.True(File.Exists(Path.Combine(root, "result.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Startup_distinguishes_no_attempt_from_a_wrapper_that_never_finished(bool attemptExists)
    {
        var directory = Temp("mbus-update-missing-");
        var root = Path.Combine(directory, "update");
        var install = new DotnetToolInstall(ToolPath, false);
        var record = RunnerUpdateRecord.In(directory);
        var updater = new DotnetToolUpdater(install, directory, DotnetPath, TextWriter.Null, record, "1.0.0", TimeProvider.System, () => ProcessTable.Empty);
        if (attemptExists)
            DotnetToolUpdater.Write(Path.Combine(root, "attempt.json"), new ToolUpdateAttempt("id", "1.0.0", "2.0.0", false, ToolPath, MbusPath, DotnetPath, 0, false, 0));
        updater.Startup();
        Assert.Equal(attemptExists ? RunnerUpdateOutcome.NotInstalled : null, record.Load("1.0.0", install)?.Outcome);
    }

    [Fact]
    public void Startup_file_in_use_with_real_fake_holders_keeps_waiting_without_consuming_the_transient_retry()
    {
        var directory = Temp("mbus-update-holders-");
        var root = Path.Combine(directory, "update");
        var tools = Path.Combine(directory, "tools");
        var shim = Path.Combine(tools, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus");
        var install = DotnetToolInstallTests.Install(tools);
        var table = ProcessTable.Of([new ProcessRecord(Environment.ProcessId, "runner", 0, null, shim, 1, "user"),
            new ProcessRecord(Environment.ProcessId + 1, "session", 0, null, shim, 2, "user")]);
        var record = RunnerUpdateRecord.In(directory);
        var updater = new DotnetToolUpdater(install, directory, DotnetPath, TextWriter.Null, record, "1.0.0", TimeProvider.System, () => table);
        DotnetToolUpdater.Write(Path.Combine(root, "attempt.json"), new ToolUpdateAttempt("id", "1.0.0", "2.0.0", true, tools, shim, DotnetPath, 1, false, 0));
        DotnetToolUpdater.Write(Path.Combine(root, "result.json"), new ToolUpdateResult("id", RunnerUpdateOutcome.NotInstalled, "Access denied", true));
        updater.Startup();
        Assert.True(updater.Waiting?.Pinned);
        Assert.Equal(0, DotnetToolUpdater.Read<ToolUpdateAttempt>(Path.Combine(root, "attempt.json"))?.Retries);
        Assert.Null(record.Load("1.0.0", install));
    }

    [Fact]
    public async Task Holder_wait_keeps_work_enabled_reports_changes_and_drains_when_empty()
    {
        var updater = new SessionUpdater { Count = 2 };
        await using var h = new RunnerHarness(extensions: e => e with { SessionUpdater = updater });
        h.Engine.RequestUpdate(new() { Version = "3.0.0" });
        Assert.False(h.Engine.GetStatus().Draining);
        Assert.False(h.Engine.UpdateReady.IsCompleted);
        var waiting = await h.Service.NextUpdateProgressAsync();
        Assert.Equal((RunnerUpdateStage.WaitingForSessions, 2), (waiting.Stage, waiting.BlockedBy!.Count));
        updater.Count = 1;
        h.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, (await h.Service.NextUpdateProgressAsync()).BlockedBy?.Count);
        updater.Count = 0;
        h.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal("3.0.0", (await h.Engine.UpdateReady).Version);
        Assert.True(h.Engine.GetStatus().Draining);
        Assert.Equal(RunnerUpdateStage.Waiting, (await h.Service.NextUpdateProgressAsync()).Stage);
        updater.Count = 1;
        h.Engine.ResumeSessionWait(new() { Version = "3.0.0" });
        Assert.False(h.Engine.GetStatus().Draining);
        Assert.False(h.Engine.UpdateReady.IsCompleted);
        Assert.Equal(1, (await h.Service.NextUpdateProgressAsync()).BlockedBy?.Count);
        h.Engine.Drain(new());
        updater.Count = 0;
        h.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(h.Engine.GetStatus().Draining);
        // A holder may leave between InstallAsync's final check and restarting the wait.
        h.Engine.ResumeSessionWait(new() { Version = "3.0.0" });
        Assert.False(h.Engine.UpdateReady.IsCompletedSuccessfully);
        h.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(h.Engine.UpdateReady.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false, "1.0.0", false)]
    [InlineData(true, "1.0.0", true)]
    [InlineData(true, "2.0.0", false)]
    [InlineData(false, "3.0.0", true)]
    public async Task Pins_wait_for_equality_and_only_pins_allow_downgrades(bool pinned, string target, bool waits)
    {
        var updater = new SessionUpdater { Count = 1 };
        await using var h = new RunnerHarness(extensions: e => e with { SessionUpdater = updater });
        h.Engine.RequestUpdate(new() { Version = target, Pinned = pinned });
        var progress = await h.Service.NextUpdateProgressAsync();
        Assert.Equal(waits ? RunnerUpdateStage.WaitingForSessions : RunnerUpdateStage.Ended, progress.Stage);
        Assert.False(h.Engine.GetStatus().Draining);
        if (waits)
        {
            h.Engine.RequestUpdate(new() { Version = "2.0.0", Pinned = true });
            Assert.Equal(RunnerUpdateStage.Ended, (await h.Service.NextUpdateProgressAsync()).Stage);
            Assert.Null(updater.Waiting);
        }
    }

    [Theory]
    [InlineData(false, false, false, false, false, false, "None")]
    [InlineData(true, false, false, false, false, false, "Apply")]
    [InlineData(false, true, false, false, false, false, "CheckInterrupted")]
    [InlineData(false, true, true, false, false, false, "CountStart")]
    [InlineData(true, true, true, true, false, false, "None")]
    [InlineData(false, true, true, false, true, false, "None")]
    [InlineData(false, true, false, false, false, true, "None")]
    public void Start_decision_respects_attempt_lifecycle(bool pending, bool applying, bool updated, bool terminal, bool confirmed, bool retry, string expected)
    {
        var attempt = new ToolUpdateAttempt("id", "1.0.0", "2.0.0", false, ToolPath, MbusPath, DotnetPath, 0, confirmed, 0)
            { Terminal = terminal, RetryPending = retry };
        var result = updated ? new ToolUpdateResult("id", RunnerUpdateOutcome.Updated, null, false) : null;
        Assert.Equal(expected, DotnetToolUpdater.StartDecision(attempt, result, pending, applying).ToString());
        Assert.Equal(applying ? ToolStartAction.CheckInterrupted : pending ? ToolStartAction.Apply : ToolStartAction.None,
            DotnetToolUpdater.StartDecision(attempt with { Terminal = false, Confirmed = false, RetryPending = false },
                new("old", RunnerUpdateOutcome.Updated, null, false), pending && !applying, applying));
    }

    [Fact]
    public async Task Stale_install_wait_does_not_replace_a_new_policy_request()
    {
        var updater = new SessionUpdater { Count = 1 };
        await using var h = new RunnerHarness(extensions: e => e with { SessionUpdater = updater });
        var old = new UpdateRequest { Version = "3.0.0" };
        var next = new UpdateRequest { Version = "4.0.0" };
        h.Engine.RequestUpdate(old);
        h.Engine.RequestUpdate(next);
        h.Engine.ResumeSessionWait(old);
        Assert.True(h.Engine.IsCurrentUpdate(next));
        Assert.False(h.Engine.IsCurrentUpdate(old));
        updater.Count = 0;
        h.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(next.Version, (await h.Engine.UpdateReady).Version);
    }

    [Theory]
    [InlineData("1.0.0", true, 3, "Done")]
    [InlineData("1.0.0", false, 0, "Done")]
    [InlineData("2.0.0", true, 3, "Repair")]
    [InlineData(null, true, 3, "Repair")]
    [InlineData("3.0.0", true, 3, "Manual")]
    [InlineData("2.0.0", false, 0, "Manual")]
    public void Recovery_start_decision_preserves_manual_versions(string? running, bool installed, int starts, string expected)
    {
        var attempt = new ToolUpdateAttempt("id", "1.0.0", "2.0.0", false, ToolPath, MbusPath, DotnetPath, starts, false, 0)
            { RecoveryTries = 1, RecoveryInstalled = installed };
        Assert.Equal(expected, DotnetToolUpdater.RecoveryDecision(attempt, running).ToString());
    }

    [Fact]
    public async Task Resumed_wait_excludes_own_tasks_and_reconciles_live_and_saved_policy()
    {
        var updater = new SessionUpdater { Count = 1, FinalCount = 2 };
        await using var h = new RunnerHarness(extensions: e => e with { SessionUpdater = updater });
        h.Engine.ResumeSavedUpdate(new() { Version = "4.0.0", Pinned = true });
        Assert.False(h.Engine.UpdateReady.IsCompleted);
        Assert.Null(updater.Waiting);
        h.Engine.ApplyPolicy(new() { OwnerSubjectId = "user", AcceptsWorkFrom = new() { Mode = MessageBus.Contracts.Machines.AcceptsWorkFromMode.Workspace }, Releases = new() { Channel = "stable", PinnedVersion = "3.0.0" } });
        Assert.Equal("3.0.0", updater.Waiting?.Version);
        Assert.False(h.Engine.UpdateReady.IsCompleted);
        h.Engine.ApplyPolicy(new() { OwnerSubjectId = "user", AcceptsWorkFrom = new() { Mode = MessageBus.Contracts.Machines.AcceptsWorkFromMode.Workspace }, Releases = new() { Channel = "preview", PinnedVersion = "5.0.0" } });
        Assert.Equal("5.0.0", updater.Waiting?.Version);
        Assert.Equal("preview", updater.Waiting?.Channel);
        h.Engine.ResumeSessionWait(updater.Waiting!);
        updater.Count = 0;
        h.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal("5.0.0", (await h.Engine.UpdateReady).Version);
        h.Engine.ApplyPolicy(new() { OwnerSubjectId = "user", AcceptsWorkFrom = new() { Mode = MessageBus.Contracts.Machines.AcceptsWorkFromMode.Workspace }, Releases = new() { Channel = "stable", PinnedVersion = "2.0.0" } });
        Assert.Null(updater.Waiting);
        Assert.False(h.Engine.GetStatus().Draining);
    }

    [Theory]
    [InlineData(RunnerUpdateOutcome.NotInstalled)]
    [InlineData(RunnerUpdateOutcome.RolledBack)]
    public void Failed_results_never_count_starts_and_recovery_is_bounded(RunnerUpdateOutcome outcome)
    {
        var attempt = new ToolUpdateAttempt("id", "1.0.0", "2.0.0", false, ToolPath, MbusPath, DotnetPath, 2, false, 0);
        var result = new ToolUpdateResult("id", outcome, "failure", false);
        Assert.Equal(ToolStartAction.None, DotnetToolUpdater.StartDecision(attempt, result, false, true));
        Assert.Equal(ToolStartAction.Recover, DotnetToolUpdater.StartDecision(attempt with { RecoveryTries = 1 }, result, false, true));
        Assert.Equal(ToolStartAction.None, DotnetToolUpdater.StartDecision(attempt with { RecoveryTries = 3 }, result, false, true));
        Assert.Equal(ToolStartAction.None, DotnetToolUpdater.StartDecision(attempt with { Terminal = true }, result, true, true));
    }

    [Fact]
    public async Task Server_4426_waits_for_holders_without_accepting_new_work()
    {
        var updater = new SessionUpdater { Count = 1 };
        await using var h = new RunnerHarness(extensions: e => e with { SessionUpdater = updater });
        var waiting = RunnerUpdateSetup.UpdateWhenIdleAsync(h.Engine, TextWriter.Null, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        Assert.True(h.Engine.GetStatus().Draining);
        Assert.Equal(1, (await h.Service.NextUpdateProgressAsync()).BlockedBy?.Count);
        updater.Count = 0;
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await waiting.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RunnerUpdates.Latest, (await h.Engine.UpdateReady).Version);
    }

    [Fact]
    public async Task Timer_scan_failure_keeps_waiting_and_retries_on_the_next_tick()
    {
        var updater = new SessionUpdater { Count = 1 };
        await using var h = new RunnerHarness(extensions: e => e with { SessionUpdater = updater });
        h.Engine.RequestUpdate(new() { Version = "3.0.0" });
        updater.ThrowOnScan = true;
        h.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.False(h.Engine.UpdateReady.IsCompleted);
        updater.ThrowOnScan = false;
        updater.Count = 0;
        h.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal("3.0.0", (await h.Engine.UpdateReady).Version);
    }

    [Fact]
    public void Confirmation_closes_and_consumes_only_an_updated_attempt_once()
    {
        var directory = Temp("mbus-update-confirm-");
        var root = Path.Combine(directory, "update");
        var install = DotnetToolInstallTests.Install(ToolPath, "2.0.0");
        var updater = new DotnetToolUpdater(install, directory, DotnetPath, TextWriter.Null, RunnerUpdateRecord.In(directory),
            "2.0.0", TimeProvider.System);
        var path = Path.Combine(root, "attempt.json");
        DotnetToolUpdater.Write(path, new ToolUpdateAttempt("id", "1.0.0", "2.0.0", false, ToolPath, MbusPath, DotnetPath, 1, false, 0));
        updater.ConfirmConnected();
        Assert.False(DotnetToolUpdater.Read<ToolUpdateAttempt>(path)!.Confirmed);
        DotnetToolUpdater.Write(Path.Combine(root, "result.json"), new ToolUpdateResult("id", RunnerUpdateOutcome.Updated, null, false));
        updater.ConfirmConnected();
        Assert.True(DotnetToolUpdater.Read<ToolUpdateAttempt>(path)!.Terminal);
        Assert.False(File.Exists(Path.Combine(root, "result.json")));
        var before = File.ReadAllText(path);
        updater.ConfirmConnected();
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public async Task Preparation_rechecks_holders_before_handoff()
    {
        var directory = Temp("mbus-update-preparation-");
        var tools = Path.Combine(directory, "tools");
        Directory.CreateDirectory(tools);
        var shim = Path.Combine(tools, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus");
        File.WriteAllText(shim, "shim");
        var install = DotnetToolInstallTests.Install(tools);
        var entry = DotnetToolInstallTests.WriteRecoverableTool(install, "applier");
        var scans = 0;
        var table = ProcessTable.Of([new(Environment.ProcessId, "runner", 0, null, shim, 1, "user"),
            new(42, "late holder", 0, null, shim, 2, "user")]);
        var updater = new DotnetToolUpdater(install, directory, DotnetPath, TextWriter.Null, RunnerUpdateRecord.In(directory),
            "1.0.0", TimeProvider.System, () => ++scans == 1 ? ProcessTable.Empty : table, entryAssembly: entry);
        Assert.False(await updater.InstallAsync(new() { Version = "2.0.0" }, TestContext.Current.CancellationToken));
        Assert.Equal(2, scans);
        Assert.True(updater.WaitingAgain);
        Assert.Equal("2.0.0", updater.Waiting?.Version);
        Assert.False(File.Exists(Path.Combine(directory, "update", OperatingSystem.IsWindows() ? "pending.cmd" : "pending.sh")));
    }

    [Theory]
    [InlineData("unknown", "2.0.0", "package ID and version could not be verified")]
    [InlineData("missing", "2.0.0", "recovery package for MessageBus.Ai.Cli 1.0.0 is missing")]
    [InlineData("corrupt", "2.0.0", "recovery package for MessageBus.Ai.Cli 1.0.0 could not be verified")]
    [InlineData("changed-payload", "2.0.0", "does not match the stored checksum")]
    [InlineData("changed-sdk-payload", "2.0.0", "does not match the dotnet tool store's second archive")]
    [InlineData("missing-checksum", "2.0.0", "has no NuGet SHA-512 checksum")]
    [InlineData("intact", "2.0.0+dev.012abcd", "needs an exact, valid package version")]
    public async Task Preparation_preserves_the_install_when_the_release_or_recovery_cannot_be_verified(
        string recovery, string requested, string reason)
    {
        var directory = Temp("mbus-update-refusal-");
        var tools = Path.Combine(directory, "tools");
        var install = DotnetToolInstallTests.Install(tools);
        var entry = DotnetToolInstallTests.WriteRecoverableTool(install, "installed program", manifestChecksum: recovery == "changed-sdk-payload");
        File.WriteAllText(Path.Combine(tools, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus"), "shim");
        var package = Directory.GetFiles(tools, "messagebus.ai.cli.1.0.0.nupkg", SearchOption.AllDirectories).Single();
        if (recovery == "missing") File.Delete(package);
        if (recovery == "corrupt") File.WriteAllText(package, "not a package");
        if (recovery is "changed-payload" or "changed-sdk-payload") ChangeArchivePayload(package);
        if (recovery == "missing-checksum") File.Delete(package + ".sha512");
        if (recovery == "unknown") install = install with { Package = null };

        var updater = new DotnetToolUpdater(install, directory, DotnetPath, TextWriter.Null, RunnerUpdateRecord.In(directory),
            "1.0.0+dev.012abcd", TimeProvider.System, () => ProcessTable.Empty, entryAssembly: entry);
        Assert.False(await updater.InstallAsync(new() { Version = requested }, TestContext.Current.CancellationToken));
        Assert.Contains(reason, updater.LastResult?.Reason);
        Assert.False(Directory.Exists(Path.Combine(directory, "update")));
        Assert.Equal("installed program", File.ReadAllText(entry));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Detached_applier_refuses_a_saved_archive_with_changed_payload(bool recovery, bool service)
    {
        if (service && !OperatingSystem.IsWindows()) return;
        var directory = Temp("mbus-update-saved-checksum-");
        var tools = Path.Combine(directory, "tools");
        var install = DotnetToolInstallTests.Install(tools);
        var entry = DotnetToolInstallTests.WriteRecoverableTool(install, "installed program");
        File.WriteAllText(Path.Combine(tools, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus"), "shim");
        var dotnet = Path.Combine(directory, "unused-dotnet");
        var updater = new DotnetToolUpdater(install, directory, dotnet, TextWriter.Null,
            RunnerUpdateRecord.In(directory), "1.0.0", TimeProvider.System, () => ProcessTable.Empty, entryAssembly: entry)
            { MinimumServiceVersion = service ? "1.0.0" : null };
        Assert.True(await updater.InstallAsync(new() { Version = "2.0.0" }, TestContext.Current.CancellationToken));
        var root = Path.Combine(directory, "update");
        ChangeArchivePayload(Directory.GetFiles(Path.Combine(root, "recovery"), "*.nupkg").Single());
        var attemptPath = Path.Combine(root, "attempt.json");
        var attempt = DotnetToolUpdater.Read<ToolUpdateAttempt>(attemptPath)!;
        if (recovery)
        {
            // Simulate interrupted recovery with an unstartable shim.
            File.Delete(Path.Combine(root, service ? "pending.service" : OperatingSystem.IsWindows() ? "pending.cmd" : "pending.sh"));
            DotnetToolUpdater.Write(attemptPath, attempt with { RecoveryTries = 1 });
        }

        var exit = service
            ? await DotnetToolUpdater.RunServiceApplierAsync(root, tools, dotnet, () => ProcessTable.Empty, (executable, args, _, _) =>
            {
                Assert.True(recovery);
                Assert.Equal(install.Shim, executable);
                Assert.Equal(["--version"], args);
                return Task.FromResult<CommandResult?>(null);
            })
            : await DotnetToolUpdater.RunApplierAsync(recovery ? "start" : "apply", root);
        Assert.Equal(0, exit);
        var result = DotnetToolUpdater.Read<ToolUpdateResult>(Path.Combine(root, "result.json"))!;
        Assert.Equal(RunnerUpdateOutcome.NotInstalled, result.Outcome);
        Assert.Contains("does not match its recorded SHA-512 checksum", result.Reason);
        Assert.Equal("installed program", File.ReadAllText(entry));
        if (recovery) Assert.True(DotnetToolUpdater.Read<ToolUpdateAttempt>(attemptPath)!.Terminal);
    }

    private static void ChangeArchivePayload(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        using var entry = archive.GetEntry("tools/net10.0/any/mbus.dll")!.Open();
        entry.SetLength(0);
        using var writer = new StreamWriter(entry);
        writer.Write("changed package payload");
    }

    private sealed class SessionUpdater : ISessionRunnerUpdater
    {
        public int Count { get; set; }
        public int? FinalCount { get; set; }
        public bool ThrowOnScan { get; set; }
        public string CurrentVersion => "2.0.0";
        public bool WaitingAgain => false;
        public UpdateRequest? Waiting { get; private set; }
        public void KeepWaiting(UpdateRequest request) => Waiting = request;
        public void ClearWaiting() => Waiting = null;
        public void ConfirmConnected() { }
        public IReadOnlyList<ToolInstallHolder> Holders(bool final = false) => ThrowOnScan ? throw new IOException("snapshot unavailable") : Enumerable.Range(1, final ? FinalCount ?? Count : Count).Select(i => new ToolInstallHolder(i, "mbus")).ToArray();
        public Task<bool> InstallAsync(UpdateRequest request, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
