using System.IO.Pipelines;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Service;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Cli.Update;
using MessageBus.Cli.Tests.Update;
using MessageBus.Contracts.Releases;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class WindowsServiceTests
{
    [Fact]
    public void Configures_service_sid_and_required_privileges_before_enabling_autostart()
    {
        var calls = new List<string>();
        WindowsServicePlan.Configure((level, value) => calls.Add($"config {level}={value}"), privilege => calls.Add(privilege),
            delays => calls.Add(string.Join(",", delays)), start => calls.Add($"start {start}"));
        Assert.Equal(["config 5=1", "SeChangeNotifyPrivilege", "15000,60000,0", "config 4=1", "start 2", "config 3=1"], calls);
    }

    [Theory]
    [InlineData(null, @"NT SERVICE\mbus-runner")]
    [InlineData(@"EXAMPLE\runner$", @"EXAMPLE\runner$")]
    public async Task Print_previews_service_identity_without_using_personal_settings_or_task_scheduler(string? account, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestMachine();
        home.RunnerServices = () => throw new InvalidOperationException("Must not access the logon task.");
        home.WriteHomeFile(".mbus/config.json", "not a valid profile");
        var args = new List<string> { "runner", "install", "--service", "--print" };
        if (account is not null) args.AddRange(["--account", account]);
        var result = await home.RunAsync(args.ToArray());
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains(expected, result.Output);
        Assert.Contains("delayed automatic", result.Output);
        Assert.Equal("not a valid profile", File.ReadAllText(Path.Combine(home.MbusFolder, "config.json")));
    }

    [Theory]
    [InlineData("LocalSystem")]
    [InlineData(@"NT AUTHORITY\LocalService")]
    [InlineData(@"NT AUTHORITY\NetworkService")]
    [InlineData(@"EXAMPLE\ordinary-user")]
    [InlineData(@"EXAMPLE\runner$ -option")]
    public void Refuses_accounts_outside_the_managed_account_form(string account) =>
        Assert.Throws<CliException>(() => WindowsServicePlan.Create(account));

    [Fact]
    public void Protected_state_grants_write_to_service_subdirectories_but_keeps_the_roots_admin_owned()
    {
        if (OperatingSystem.IsWindows()) CheckWindowsAcls();
    }

    [SupportedOSPlatform("windows")]
    private static void CheckWindowsAcls()
    {
        var host = WindowsServicePaths.Security(writable: false);
        var state = WindowsServicePaths.Security(writable: true);
        const FileSystemRights replaceRoot = FileSystemRights.Delete | FileSystemRights.TakeOwnership | FileSystemRights.ChangePermissions
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.WriteAttributes;
        foreach (var acl in new[] { host, state })
        {
            Assert.True(acl.AreAccessRulesProtected);
            Assert.Equal(WindowsServicePaths.Administrators, acl.GetOwner(typeof(SecurityIdentifier)));
            Assert.All(Rules(acl), rule => Assert.Contains(rule.IdentityReference,
                new[] { WindowsServicePaths.Administrators, WindowsServicePaths.ServiceSid }));
            Assert.All(Rules(acl).Where(rule => rule.IdentityReference == WindowsServicePaths.ServiceSid
                && !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)), rule => Assert.Equal(0, (int)(rule.FileSystemRights & replaceRoot)));
        }
        var hostRule = Assert.Single(Rules(host), rule => rule.IdentityReference == WindowsServicePaths.ServiceSid);
        Assert.Equal(0, (int)(hostRule.FileSystemRights & FileSystemRights.Write));
        Assert.Contains(Rules(state), rule => rule.IdentityReference == WindowsServicePaths.ServiceSid
            && rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) && rule.FileSystemRights.HasFlag(FileSystemRights.Modify));

        static IEnumerable<FileSystemAccessRule> Rules(DirectorySecurity acl) =>
            acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>();
    }

    [Fact]
    public async Task Control_protocol_exchanges_a_protected_request()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ct = TestContext.Current.CancellationToken;
        var toServer = new Pipe();
        var toClient = new Pipe();
        using var server = new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        using var client = new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream());
        var request = new ServiceRequest("enroll", new("https://service.example", "test-runner-credential"));
        var receipt = WindowsServiceControl.ReadRequestAsync(server, ct);
        await WindowsServiceControl.WriteRequestAsync(client, request, ct);
        Assert.Equal(request, await receipt);
    }

    private sealed class DuplexStream(Stream input, Stream output) : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => input.ReadAsync(buffer, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) => output.WriteAsync(buffer, ct);
        public override Task FlushAsync(CancellationToken ct) => output.FlushAsync(ct);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { input.Dispose(); output.Dispose(); }
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task Worker_shutdown_during_update_does_not_apply_or_start_a_replacement()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeStarts = 0;
        var running = WindowsServiceWorkerLifecycle.RunAsync(_ => { beforeStarts++; return Task.CompletedTask; }, async ct =>
        {
            childStarted.SetResult();
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => drained.SetResult());
            await drained.Task;
            return RunnerCommand.UpdateExitCode;
        }, new FakeTimeProvider(), stop.Token);
        await childStarted.Task;
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, beforeStarts);
    }

    [Fact]
    public async Task An_unhealthy_update_loop_stops_for_scm_recovery()
    {
        var calls = new List<string>();
        var error = await Assert.ThrowsAsync<CliException>(() => WindowsServiceWorkerLifecycle.RunAsync(
            _ => { calls.Add("preflight"); return Task.CompletedTask; },
            _ => { calls.Add("worker exited 75"); return Task.FromResult(RunnerCommand.UpdateExitCode); },
            new FakeTimeProvider(), TestContext.Current.CancellationToken));
        Assert.Equal(Enumerable.Repeat(new[] { "preflight", "worker exited 75" }, 4).SelectMany(pair => pair), calls);
        Assert.Contains("restart limit", error.Message);
    }

    [Fact]
    public async Task Supervisor_applies_update_after_exit_and_restores_the_old_worker_if_unconfirmed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestMachine();
        var ct = TestContext.Current.CancellationToken;
        var tools = home.Bin;
        var settings = home.MbusFolder;
        var shim = Path.Combine(tools, "mbus.exe");
        var dotnet = Path.Combine(tools, "test-dotnet.exe");
        File.WriteAllText(shim, "fixture shim");
        var install = DotnetToolInstallTests.Install(tools);
        var entry = DotnetToolInstallTests.WriteRecoverableTool(install, "fixture program");
        var staged = Path.Combine(install.Package!.StoreEntry, "messagebus.ai.cli", "1.0.0", "messagebus.ai.cli.1.0.0.nupkg");
        var digest = File.ReadAllText(staged + ".sha512");
        var store = DotnetToolPackage.StorePath(tools, DotnetToolInstall.PackageId);
        var version = install.Package.Version;
        var time = new FakeTimeProvider();
        var updater = new DotnetToolUpdater(install, settings, dotnet, TextWriter.Null, RunnerUpdateRecord.In(settings),
            "9.0.0+unrelated", time, () => ProcessTable.Empty, source: "ignored-fixture-feed", entryAssembly: entry)
            { MinimumServiceVersion = "1.0.0" };
        var update = Path.Combine(settings, "update");
        Task<CommandResult?> Process(string executable, IReadOnlyList<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment)
        {
            if (executable == shim)
            {
                Assert.Equal(["--version"], args);
                return Task.FromResult<CommandResult?>(new CommandResult(0, "9.0.0+unrelated", ""));
            }
            Assert.Equal(dotnet, executable);
            Assert.Equal(new[] { "tool", "update", "--tool-path", tools, DotnetToolInstall.PackageId, "--version" }, args.Take(6));
            Directory.Move(Path.Combine(store, version), Path.Combine(store, args[6]));
            version = args[6];
            Assert.Equal(Path.Combine(update, version == "1.0.0" ? "Recovery.Config" : "NuGet.Config"), args[8]);
            using var saved = File.OpenRead(Path.Combine(update, "recovery", "MessageBus.Ai.Cli.1.0.0.nupkg"));
            Assert.Equal(digest, DotnetToolPackage.Checksum(saved));
            return Task.FromResult<CommandResult?>(new CommandResult(0, "", ""));
        }
        var runs = 0;
        await Assert.ThrowsAsync<CliException>(() => WindowsServiceWorkerLifecycle.RunAsync(async _ =>
        {
            if (runs == 0) return;
            Assert.Equal(0, await DotnetToolUpdater.RunServiceApplierAsync(update, tools, dotnet, () => ProcessTable.Empty, Process));
        }, async _ =>
        {
            runs++;
            if (runs == 1)
            {
                Assert.True(await updater.InstallAsync(new() { Version = "2.0.0" }, ct), updater.LastResult?.Reason);
                Assert.Equal("1.0.0", version);
                Assert.False(File.Exists(Path.Combine(update, "pending.cmd")));
                Assert.Equal(DotnetToolUpdater.NuGetConfig(DotnetToolUpdater.NuGetOrg, requireSignature: true),
                    File.ReadAllText(Path.Combine(update, "NuGet.Config")));
            }
            else if (runs <= 3)
            {
                Assert.Equal("2.0.0", version);
            }
            else
            {
                Assert.Equal("1.0.0", version);
                Assert.Equal(RunnerUpdateOutcome.RolledBack, DotnetToolUpdater.Read<ToolUpdateResult>(Path.Combine(update, "result.json"))?.Outcome);
                return 1;
            }
            return RunnerCommand.UpdateExitCode;
        }, time, ct));
        Assert.Equal(4, runs);
    }
}
