using System.Diagnostics;
using System.Xml.Linq;
using MessageBus.Cli.Runner.Service;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

/// <summary>Executes the actual launch agent's shell command without asking launchd to restart anything.</summary>
public sealed class MacRunnerSupervisorTests
{
    [Fact]
    public async Task Real_launchd_recovers_failures_without_respawning_its_job()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("MBUS_TEST_LAUNCHD") == "1",
            "Opt in with MBUS_TEST_LAUNCHD=1 on a Mac with a signed-in GUI session; registers only a disposable test job.");
        using var fixture = new SupervisorFixture();
        fixture.Write("worker.sh", """
            n=0
            [ ! -f "$TEST_ROOT/attempt" ] || n=$(cat "$TEST_ROOT/attempt")
            n=$((n + 1))
            printf '%s' "$n" > "$TEST_ROOT/attempt"
            printf '%s\n' "$PPID" >> "$TEST_ROOT/events"
            case "$n" in
                1) exit 1 ;;
                2) exit 75 ;;
                *) : > "$TEST_ROOT/done"; exit 0 ;;
            esac
            """);
        var uid = (await NativeAsync("/usr/bin/id", ["-u"])).Output.Trim();
        var domain = "gui/" + uid;
        var label = "ai.messagebus.recovery-test." + Guid.NewGuid().ToString("N");
        var job = domain + "/" + label;
        var plan = fixture.Plan(new Dictionary<string, string> { ["TEST_ROOT"] = fixture.Root, ["PATH"] = "/usr/bin:/bin" });
        var plist = XDocument.Parse(plan.Definition);
        plist.Root!.Element("dict")!.Elements("key").Single(e => e.Value == "Label").NextNode!.ReplaceWith(new XElement("string", label));
        var definition = fixture.Root + "/test.plist";
        await File.WriteAllTextAsync(definition, plist.ToString(), TestContext.Current.CancellationToken);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(fixture.Root, "done");
        watcher.Created += (_, _) => finished.TrySetResult();
        watcher.EnableRaisingEvents = true;
        try
        {
            var loaded = await NativeAsync("/bin/launchctl", ["bootstrap", domain, definition]);
            Assert.True(loaded.ExitCode == 0, loaded.Output);
            var started = await NativeAsync("/bin/launchctl", ["kickstart", job]);
            Assert.True(started.ExitCode == 0, started.Output);
            if (File.Exists(fixture.Root + "/done")) finished.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(40), TestContext.Current.CancellationToken);
            Assert.Equal(3, fixture.Events.Length);
            Assert.Single(fixture.Events.Distinct()); // every worker had the same living supervisor
            var status = await NativeAsync("/bin/launchctl", ["print", job]);
            Assert.Equal(0, status.ExitCode);
            Assert.Contains("runs = 1", status.Output); // launchd never had to respawn the job
            Assert.Contains("code 1; restarting", await File.ReadAllTextAsync(fixture.Root + "/logs/runner-errors.log", TestContext.Current.CancellationToken));
            Assert.Contains("code 75; restarting", await File.ReadAllTextAsync(fixture.Root + "/logs/runner-errors.log", TestContext.Current.CancellationToken));
        }
        finally
        {
            // Never leave even a failed acceptance run's job loaded; it has no production identity or credentials.
            await NativeAsync("/bin/launchctl", ["bootout", job]);
            var unloaded = await NativeAsync("/bin/launchctl", ["print", job]);
            Assert.NotEqual(0, unloaded.ExitCode);
        }
    }

    private static async Task<(int ExitCode, string Output)> NativeAsync(string command, IReadOnlyList<string> args)
    {
        using var process = Process.Start(new ProcessStartInfo(command, args)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output + await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Failures_and_updates_restart_inside_one_supervisor_and_reapply_the_update_hook()
    {
        using var fixture = new SupervisorFixture();
        fixture.Write("worker.sh", """
            n=0
            [ ! -f "$TEST_ROOT/attempt" ] || n=$(cat "$TEST_ROOT/attempt")
            n=$((n + 1))
            printf '%s' "$n" > "$TEST_ROOT/attempt"
            printf 'worker:%s:%s\n' "$n" "$1" >> "$TEST_ROOT/events"
            case "$n" in
                1) exit 1 ;;
                2) exit 75 ;;
                *) exit 0 ;;
            esac
            """);
        fixture.Write("update/wrapper.sh", "printf 'update\n' >> \"$TEST_ROOT/events\"\n");
        fixture.WriteSleep("printf 'delay:%s\n' \"$1\" >> \"$TEST_ROOT/events\"\n");

        var result = await fixture.RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { "update", "worker:1:" + SupervisorFixture.LiteralArgument, "delay:5",
            "update", "worker:2:" + SupervisorFixture.LiteralArgument, "delay:5", "update",
            "worker:3:" + SupervisorFixture.LiteralArgument }, fixture.Events);
        Assert.Contains("code 1; restarting", result.Error);
        Assert.Contains("code 75; restarting", result.Error);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("updater")]
    [InlineData("runner")]
    [InlineData("delay")]
    public async Task A_saved_stop_request_prevents_another_launch_in_every_supervisor_phase(string phase)
    {
        using var fixture = new SupervisorFixture();
        const string stop = ": > \"$TEST_ROOT/runner.stop\"\n";
        fixture.Write("worker.sh", "printf 'worker\n' >> \"$TEST_ROOT/events\"\n" + (phase == "runner" ? stop : "") + "exit 1\n");
        fixture.Write("update/wrapper.sh", "printf 'update\n' >> \"$TEST_ROOT/events\"\n" + (phase == "updater" ? stop : ""));
        fixture.WriteSleep("printf 'delay\n' >> \"$TEST_ROOT/events\"\n" + stop);
        if (phase == "before") fixture.Write("runner.stop", "");

        var result = await fixture.RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(phase switch
        {
            "before" => Array.Empty<string>(),
            "updater" => ["update"],
            "runner" => ["update", "worker"],
            _ => ["update", "worker", "delay"],
        }, fixture.Events);
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("updater")]
    [InlineData("delay")]
    public async Task Termination_stops_the_active_child_and_never_restarts_it(string phase)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix signal forwarding runs with the native /bin/sh.");
        using var fixture = new SupervisorFixture();
        // A FIFO blocks without a timed sleep or a polling loop. Signal the supervisor only after the
        // child has installed its TERM handler; the trap records its exit before the supervisor exits.
        const string block = """
            trap 'printf "terminated\n" >> "$TEST_ROOT/events"; exit 0' TERM
            exec 3<> "$TEST_ROOT/gate"
            printf 'ready\n'
            read line <&3
            """;
        using (var fifo = Process.Start(new ProcessStartInfo("mkfifo", [fixture.Root + "/gate"]) { UseShellExecute = false })!)
            await fifo.WaitForExitAsync(TestContext.Current.CancellationToken);
        fixture.Write("worker.sh", phase == "worker" ? block : "exit 1\n");
        if (phase == "updater") fixture.Write("update/wrapper.sh", block);
        fixture.WriteSleep(phase == "delay" ? block : "exit 0\n");
        using var supervisor = fixture.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            Assert.Equal("ready", await supervisor.StandardOutput.ReadLineAsync(deadline.Token));
            using var signal = Process.Start(new ProcessStartInfo("/bin/kill", ["-TERM", supervisor.Id.ToString()]) { UseShellExecute = false })!;
            await signal.WaitForExitAsync(deadline.Token);
            await supervisor.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, supervisor.ExitCode);
            Assert.Equal(new[] { "terminated" }, fixture.Events);
        }
        finally
        {
            if (!supervisor.HasExited) supervisor.Kill(entireProcessTree: true);
            await supervisor.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class SupervisorFixture : IDisposable
    {
        public const string LiteralArgument = "a 'quoted' argument; $(do-not-execute)";
        // Exercise quoting in both the settings path and the command's positional arguments.
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "mbus-supervisor-" + Guid.NewGuid().ToString("N"), "settings 'with spaces' $()");
        public string Root => _directory.Replace('\\', '/');
        public string[] Events => File.Exists(Path.Combine(_directory, "events")) ? File.ReadAllLines(Path.Combine(_directory, "events")) : [];
        public SupervisorFixture()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "The macOS supervisor requires a native POSIX shell; exercised on macOS and Linux.");
            Directory.CreateDirectory(_directory);
        }

        public void Write(string name, string content)
        {
            var path = Path.Combine(_directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.ReplaceLineEndings("\n") + "\n");
        }

        public void WriteSleep(string content)
        {
            Write("bin/sleep", "#!/bin/sh\n" + content);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(_directory, "bin", "sleep"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public RunnerServicePlan Plan(IReadOnlyDictionary<string, string>? environment = null)
        {
            Directory.CreateDirectory(Path.Combine(_directory, "logs"));
            return RunnerServiceDefinitions.Plan(new ServiceManager(ServicePlatform.MacOs, "dev", 501, (_, _) => throw new InvalidOperationException()),
                ["/bin/sh", Root + "/worker.sh", LiteralArgument], [], Root, Root, environment ?? new Dictionary<string, string>());
        }

        public Process Start()
        {
            var plan = Plan();
            var dict = XDocument.Parse(plan.Definition).Root!.Element("dict")!.Elements().ToList();
            var args = dict[dict.FindIndex(e => e.Name == "key" && e.Value == "ProgramArguments") + 1].Elements("string").Select(e => e.Value).Skip(1);
            // The fake worker receives the real command suffix too (runner --log-file), as a real mbus would.
            var start = new ProcessStartInfo("/bin/sh", args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["TEST_ROOT"] = Root;
            start.Environment["PATH"] = Root + "/bin:" + Environment.GetEnvironmentVariable("PATH");
            return Process.Start(start)!;
        }

        public async Task<(int ExitCode, string Error)> RunAsync()
        {
            using var process = Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                await output;
                return (process.ExitCode, await error);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
        }

        public void Dispose()
        {
            var parent = Directory.GetParent(_directory)!.FullName;
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }
}
