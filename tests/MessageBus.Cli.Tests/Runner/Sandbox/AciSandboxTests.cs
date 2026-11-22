using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Contracts.Machines;
using Xunit;

namespace MessageBus.Cli.Tests.Runner.Sandbox;

public sealed class AciSandboxTests
{
    [Fact]
    public void Aci_cannot_fall_back_to_an_unwrapped_launch()
    {
        var folder = Directory.CreateTempSubdirectory("mbus-aci-test-");
        try
        {
            var log = new StringWriter();
            var sandbox = RunIsolation.Create(Options(folder.FullName) with { Requested = IsolationLevel.Off }, log);
            using var work = RunWorkspace.Create(folder.FullName, "fixture", 1);
            Assert.NotNull(sandbox.AgentRefusal(AgentKind.ClaudeCode));
            Assert.Contains("ACI task execution is blocked", sandbox.Report!.Summary);
            Assert.Null(sandbox.Report.Network);
            Assert.Throws<IOException>(() => sandbox.Wrap(new ProcessLaunch { FileName = "fixture", WorkingDirectory = work.Root }, work, new() { Name = "fixture" }));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void An_unrecovered_owned_journal_keeps_quarantine_visible_when_startup_is_refused()
    {
        var folder = Directory.CreateTempSubdirectory("mbus-aci-quarantine-");
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(folder.FullName, ".mbus", "aci-runtime")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "launch-" + Guid.NewGuid().ToString("N")));
            IRunSandbox sandbox = RunIsolation.Create(Options(folder.FullName) with { Requested = IsolationLevel.Off }, TextWriter.Null);
            Assert.Contains("quarantined", sandbox.QuarantineProblem);
        }
        finally { folder.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("168.63.129.16", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::ffff:192.168.0.1", false)]
    [InlineData("8.8.8.8", true)]
    public void Aci_egress_filters_resolved_addresses(string address, bool allowed) =>
        Assert.Equal(allowed, AciEgress.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protected_launch_passes_the_same_offline_probe_used_at_startup(bool kernelDeniesProcMount)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MBUS_TEST_ACI_SANDBOX") == "1",
            "Requires the non-root Linux runner image and namespace support.");
        var folder = Directory.CreateTempSubdirectory("mbus-aci-test-");
        string? sockets = null;
        string? run = null;
        AciSandbox? sandbox = null;
        try
        {
            var options = Options(folder.FullName) with
            {
                ProbeAgents = [new("fixture", "/usr/local/bin/node"), new("codex", "/usr/local/bin/codex")],
            };
            var probe = new RecordedProbe(kernelDeniesProcMount);
            sandbox = AciSandbox.TryCreate(options, out var why, probe);
            Assert.True(sandbox is not null, why + "\n" + probe.Failures);
            if (kernelDeniesProcMount && File.Exists("/usr/local/lib/mbus/gvisor/runsc"))
                Assert.True(sandbox!.AgentProblem(AgentKind.Codex) is null, sandbox.AgentProblem(AgentKind.Codex) + "\n" + probe.Failures);
            sockets = AciSandbox.CreateSocketDirectory();
            run = Directory.CreateDirectory(Path.Combine(options.WorkRoot, "task")).FullName;
            var launch = sandbox!.Wrap(new ProcessLaunch { FileName = "/bin/true", WorkingDirectory = run }, run, sockets,
                useUserspace: kernelDeniesProcMount);
            var result = new ProcessProbeRunner().Run(new("protected command", launch, launch, Sandboxed: true));
            Assert.True(result.ExitCode == 0, result.Output);
            if (kernelDeniesProcMount)
            {
                // Other agents keep the existing empty-proc protected launcher,
                // even when the same runner can offer userspace Codex launches.
                var native = sandbox.Wrap(new ProcessLaunch { FileName = "/usr/local/bin/node", WorkingDirectory = run,
                    Arguments = ["-e", "process.exit(require('node:fs').readdirSync('/proc').length===0?0:1)"] }, run, sockets);
                var nativeResult = new ProcessProbeRunner().Run(new("native agent profile", native, native, Sandboxed: true));
                Assert.True(nativeResult.ExitCode == 0, nativeResult.Output);
            }
        }
        finally
        {
            if (sandbox is not null && run is not null) await sandbox.EndAsync(run);
            if (sockets is not null) Directory.Delete(sockets, recursive: true);
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Userspace_launches_cancel_without_releasing_owned_processes_or_unstarted_plans()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MBUS_TEST_ACI_SANDBOX") == "1"
            && File.Exists("/usr/local/lib/mbus/gvisor/runsc"), "Requires the protected amd64 runner image.");
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var folder = Directory.CreateTempSubdirectory("mbus-aci-lifecycle-");
        var roots = new List<string>();
        var sockets = AciSandbox.CreateSocketDirectory();
        AciSandbox? sandbox = null;
        try
        {
            var options = Options(folder.FullName) with { ProbeAgents = [new("claude", "/usr/local/bin/node")] };
            sandbox = AciSandbox.TryCreate(options, out var why, new RecordedProbe(denyFirst: true));
            Assert.True(sandbox is not null, why);
            string Run(string name)
            {
                var run = Directory.CreateDirectory(Path.Combine(options.WorkRoot, name)).FullName;
                Directory.CreateDirectory(Path.Combine(run, "home"));
                roots.Add(run);
                return run;
            }
            var prepared = Run("prepared");
            var neverStarted = sandbox!.Wrap(new ProcessLaunch { FileName = "/bin/true", WorkingDirectory = prepared }, prepared, sockets, useUserspace: true);
            await sandbox.EndAsync(prepared);
            // A delayed start cannot race recovery: cleanup claimed the prepared marker.
            using (var late = AgentProcess.Start(neverStarted)) Assert.NotEqual(0, await late.WaitForExitAsync());
            Assert.Null(sandbox.AgentProblem(AgentKind.ClaudeCode));

            var active = Run("active");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var process = AgentProcess.Start(sandbox.Wrap(new ProcessLaunch
            {
                FileName = "/usr/local/bin/node", Arguments = ["-e", "console.log('holding');setInterval(()=>{},1000)"], WorkingDirectory = active,
            }, active, sockets, useUserspace: true), line => { if (line == "holding") ready.TrySetResult(); });
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            var runtimeRoot = Path.Combine(folder.FullName, ".mbus", "aci-runtime");
            var owned = Assert.Single(Directory.GetDirectories(runtimeRoot, "launch-*"));
            using var journal = JsonDocument.Parse(File.ReadAllText(Path.Combine(owned, "journal.json")));
            var control = journal.RootElement.GetProperty("control").GetString()!;
            var peers = journal.RootElement.GetProperty("peers").EnumerateObject().Select(peer =>
                (Pid: peer.Value.GetProperty("pid").GetInt32(), Start: peer.Value.GetProperty("start").GetString())).ToArray();
            Assert.Equal(2, peers.Length);
            process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            using (var firstCleanup = AgentProcess.Start(new ProcessLaunch
            {
                FileName = "/usr/local/bin/node", Arguments = ["/usr/local/lib/mbus/aci-sandbox.mjs", "cleanup", owned], WorkingDirectory = active,
            })) Assert.Equal(0, await firstCleanup.WaitForExitAsync());
            // Recover the other crash boundary: safe was persisted, but the
            // already-owned gate directory had not yet been removed.
            Directory.CreateDirectory(control, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(control, "owned-gate-fixture"), "fixture");
            var gateRetry = JsonNode.Parse(File.ReadAllText(Path.Combine(owned, "journal.json")))!;
            gateRetry["control"] = control;
            gateRetry["controlCreated"] = true;
            File.WriteAllText(Path.Combine(owned, "journal.json"), gateRetry.ToJsonString());
            using (var retry = AgentProcess.Start(new ProcessLaunch
            {
                FileName = "/usr/local/bin/node", Arguments = ["/usr/local/lib/mbus/aci-sandbox.mjs", "cleanup", owned], WorkingDirectory = active,
            })) Assert.Equal(0, await retry.WaitForExitAsync());
            Assert.False(Directory.Exists(control));
            // Reproduce a crash after official delete but before the successful
            // cleanup result was persisted. The owned peers are genuinely gone.
            var persisted = JsonNode.Parse(File.ReadAllText(Path.Combine(owned, "journal.json")))!;
            persisted["safe"] = false;
            File.WriteAllText(Path.Combine(owned, "journal.json"), persisted.ToJsonString());
            await sandbox.EndAsync(active);
            foreach (var peer in peers)
            {
                var stat = $"/proc/{peer.Pid}/stat";
                if (!File.Exists(stat)) continue;
                var raw = File.ReadAllText(stat);
                var fields = raw[(raw.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.True(fields[0] == "Z" || fields[19] != peer.Start, "An owned live runtime peer survived cancellation.");
            }
            Assert.False(Directory.Exists(control));
            Assert.Empty(Directory.GetDirectories(runtimeRoot, "launch-*"));
            Assert.Null(sandbox.AgentProblem(AgentKind.ClaudeCode));
        }
        finally
        {
            if (sandbox is not null)
                foreach (var run in roots) await sandbox.EndAsync(run);
            Directory.Delete(sockets, recursive: true);
            folder.Delete(recursive: true);
        }
    }

    // Report the kernel refusal for the first profile only. The fallback has
    // to run the real namespaces and pass the production marker checks.
    private sealed class RecordedProbe(bool denyFirst) : IProbeRunner
    {
        private readonly ProcessProbeRunner _real = new();
        private bool _denied;
        internal string Failures { get; private set; } = "";

        public ProbeOutcome Run(ProbeCommand command)
        {
            if (denyFirst && !_denied)
            {
                Assert.Contains("--proc", command.Launch.Arguments);
                _denied = true;
                return new(1, "bwrap: Can't mount proc on /proc: Operation not permitted\n");
            }
            var result = _real.Run(command);
            if (result.ExitCode != 0) Failures += command.Step + ": " + result.Output + "\n";
            return result;
        }
    }

    private static RunIsolationOptions Options(string directory) => new()
    {
        RequireAciSandbox = true,
        HomeDirectory = directory,
        WorkRoot = Path.Combine(directory, "runs"),
        RunTools = true,
        LimitNetwork = true,
        AciIdentityHost = "runner-fixture.blob.core.windows.net",
    };
}
