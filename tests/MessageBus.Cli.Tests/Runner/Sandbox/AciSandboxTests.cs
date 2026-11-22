using System.Net;
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
            var launch = sandbox!.Wrap(new ProcessLaunch { FileName = "/bin/true", WorkingDirectory = run }, run, sockets);
            var result = new ProcessProbeRunner().Run(new("protected command", launch, launch, Sandboxed: true));
            Assert.True(result.ExitCode == 0, result.Output);
        }
        finally
        {
            if (sandbox is not null && run is not null) await sandbox.EndAsync(run);
            if (sockets is not null) Directory.Delete(sockets, recursive: true);
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
