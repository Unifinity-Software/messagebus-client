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
    public void Protected_launch_passes_the_same_offline_probe_used_at_startup(bool kernelDeniesProcMount)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MBUS_TEST_ACI_SANDBOX") == "1",
            "Requires the non-root Linux runner image and namespace support.");
        var folder = Directory.CreateTempSubdirectory("mbus-aci-test-");
        try
        {
            var options = Options(folder.FullName) with
            {
                ProbeAgents = [new("fixture", "/usr/local/bin/node")],
            };
            IProbeRunner? probe = kernelDeniesProcMount ? new DenyFirstProcMount() : null;
            var sandbox = AciSandbox.TryCreate(options, out var why, probe);
            Assert.True(sandbox is not null, why);
            var launch = sandbox!.Wrap(new ProcessLaunch { FileName = "/bin/true", WorkingDirectory = folder.FullName },
                folder.FullName, folder.FullName);
            if (kernelDeniesProcMount)
            {
                Assert.DoesNotContain("--proc", launch.Arguments);
                Assert.Contains("--remount-ro", launch.Arguments);
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    // Report the kernel refusal for the first profile only. The fallback has
    // to run the real namespaces and pass the production marker checks.
    private sealed class DenyFirstProcMount : IProbeRunner
    {
        private readonly ProcessProbeRunner _real = new();
        private bool _denied;

        public ProbeOutcome Run(ProbeCommand command)
        {
            if (_denied) return _real.Run(command);
            Assert.Contains("--proc", command.Launch.Arguments);
            _denied = true;
            return new(1, "bwrap: Can't mount proc on /proc: Operation not permitted\n");
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
