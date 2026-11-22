using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Machines;

namespace MessageBus.Cli.Runner.Sandbox;

// ACI's managed identity is attached to the container group. The runner alone
// stays in that network namespace. A task gets a new mount, PID and network
// namespace and just one Unix socket to its allow-list proxy.
internal sealed class AciSandbox
{
    internal const string IdentityHostVariable = "MBUS_AZURE_IDENTITY_HOST";
    internal const int ProxyPort = 47475;
    private const string SocketName = "proxy.sock";
    private const string Bwrap = "/usr/bin/bwrap";
    private const string Node = "/usr/local/bin/node";
    private const string FixtureHost = "mbus-probe.invalid";
    private const string ProcMountDenied = "bwrap: Can't mount proc on /proc: Operation not permitted";
    private readonly Dictionary<string, string?> _agents = new(StringComparer.Ordinal);
    private readonly AciEgress _egress;
    private bool _mountProc = true;
    private AciUserspaceSandbox? _userspace;
    private bool _userspaceForCodex;

    private AciSandbox(string identityHost) => _egress = new(identityHost);

    internal string? AgentProblem(AgentKind kind) => _userspace?.Problem ?? (_agents.TryGetValue(AgentKinds.Command(kind), out var problem)
        ? problem : "The harness has not passed the protected ACI launch check. Restart the runner after installing it.");

    internal bool EnforcesFilePolicy(string run) => _userspaceForCodex && _userspace?.EnforcesFilePolicy(run) == true;

    internal string? QuarantineProblem => _userspace?.Problem;

    internal void Prepare(string run, AgentKind? kind)
    {
        if (_userspaceForCodex && kind == AgentKind.Codex) _userspace?.Prepare(run);
    }

    internal Task EndAsync(string run) => _userspace?.EndAsync(run) ?? Task.CompletedTask;

    internal static AciSandbox? TryCreate(RunIsolationOptions options, out string problem, IProbeRunner? probeRunner = null)
    {
        problem = "Protected ACI runs require the Linux runner image running as UID 10001.";
        if (!OperatingSystem.IsLinux() || EffectiveUid() != 10001) return null;
        problem = "Protected ACI runs require OS isolation, limited network access and the run-tools service.";
        if (options.Requested != IsolationLevel.OsSandbox || !options.LimitNetwork || !options.RunTools) return null;
        problem = "Protected ACI runs require direct egress from the runner and cannot use an upstream proxy.";
        if (options.Upstream is not null || options.UpstreamProblem is not null) return null;
        problem = "The ACI bootstrap did not provide the identity storage hostname.";
        if (options.AciIdentityHost is not { Length: > 0 } identityHost) return null;
        problem = "The ACI image needs Node.js and non-setuid bubblewrap.";
        if (!File.Exists(Bwrap) || !File.Exists(Node)
            || (File.GetUnixFileMode(Bwrap) & (UnixFileMode.SetUser | UnixFileMode.SetGroup)) != 0) return null;
        problem = "Protected ACI runs require a work directory inside the runner's home, separate from its settings.";
        var work = MacSandbox.RealPath(options.WorkRoot);
        var home = MacSandbox.RealPath(options.HomeDirectory);
        var settings = MacSandbox.RealPath(options.SettingsDirectory ?? Path.Combine(home, ".mbus"));
        if (!Below(work, home) || home == "/" || Within(settings, work)
            || Overlaps(home, "/usr") || Overlaps(settings, "/usr") || Overlaps(settings, "/etc")
            || options.PrivatePaths.Select(MacSandbox.RealPath).Any(path => Overlaps(path, work) || Overlaps(path, "/usr") || Overlaps(path, "/etc"))) return null;

        var sandbox = new AciSandbox(identityHost);
        try
        {
            if (sandbox.Probe(options, probeRunner ?? new ProcessProbeRunner()) is { } failure)
            {
                problem = failure;
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException or Win32Exception)
        {
            problem = "The protected ACI launch check could not start. Check container namespace support and the runner's work directory.";
            return null;
        }
        problem = "";
        return sandbox;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUid();

    private static bool Below(string path, string root) => path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static bool Within(string path, string root) => path.Equals(root, StringComparison.Ordinal) || Below(path, root);

    private static bool Overlaps(string a, string b) => Within(a, b) || Within(b, a);

    internal static string CreateSocketDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("mbus-aci-").FullName;
        if (directory.StartsWith("/tmp/", StringComparison.Ordinal) && directory.Length < 80) return directory;
        Directory.Delete(directory);
        throw new IOException("Protected ACI runs need a short temporary socket path under /tmp.");
    }

    internal RunNetwork StartNetwork(string directory, HostAllowList allow, TextWriter log, string runName) =>
        RunNetwork.StartAci(Path.Combine(directory, SocketName), allow, log, runName, _egress.ConnectAsync);

    internal ProcessLaunch Wrap(ProcessLaunch launch, string run, string sockets, bool canEditFiles = true, bool useUserspace = false)
    {
        var home = Path.Combine(run, "home");
        var environment = new Dictionary<string, string?>(launch.Environment, StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["CLAUDE_CONFIG_DIR"] = null,
            ["CODEX_HOME"] = null,
            ["XDG_CONFIG_HOME"] = null,
            ["XDG_DATA_HOME"] = null,
            ["XDG_CACHE_HOME"] = null,
            ["XDG_STATE_HOME"] = null,
            ["XDG_RUNTIME_DIR"] = null,
        };
        if (useUserspace && _userspaceForCodex && _userspace is not null)
            return _userspace.Wrap(launch with { Environment = environment }, run, sockets, Path.Combine(run, "work"), canEditFiles);
        return launch with
        {
            FileName = Bwrap,
            // The image's toolchain is read-only. Never mount the outer root, a host
            // device tree, shared temporary directories, or any runner home files.
            Arguments = [
                "--unshare-user", "--unshare-pid", "--unshare-net", "--unshare-ipc", "--unshare-uts", "--cap-drop", "ALL", "--new-session",
                .. (_mountProc ? new[] { "--proc", "/proc" } : ["--tmpfs", "/proc", "--remount-ro", "/proc"]),
                "--dev", "/dev", "--tmpfs", "/tmp", "--tmpfs", "/var/tmp", "--tmpfs", "/dev/shm",
                "--ro-bind", "/usr", "/usr", "--symlink", "usr/bin", "/bin", "--symlink", "usr/sbin", "/sbin",
                "--symlink", "usr/lib", "/lib", "--symlink", "usr/lib64", "/lib64",
                "--ro-bind", "/etc/ssl/certs", "/etc/ssl/certs", "--ro-bind", "/etc/passwd", "/etc/passwd", "--ro-bind", "/etc/group", "/etc/group",
                "--bind", run, run, "--ro-bind", sockets, sockets, "--chdir", launch.WorkingDirectory,
                "--", Node, "-e", Relay, "--", Path.Combine(sockets, SocketName), ProxyPort.ToString(CultureInfo.InvariantCulture),
                launch.FileName, .. launch.Arguments,
            ],
            WorkingDirectory = run,
            RawArguments = null,
            Environment = environment,
        };
    }

    private const string Relay = """
        const net = require('node:net');
        const { spawn } = require('node:child_process');
        const [path, port, file, ...args] = process.argv.slice(1);
        const listener = net.createServer({ allowHalfOpen: true }, socket => {
          const peer = net.createConnection({ path, allowHalfOpen: true });
          socket.on('error', () => peer.destroy());
          peer.on('error', () => socket.destroy());
          socket.pipe(peer).pipe(socket);
        });
        listener.once('error', () => { console.error('Could not start the protected ACI network relay.'); process.exit(1); });
        listener.listen(Number(port), '127.0.0.1', () => {
          const child = spawn(file, args, { stdio: 'inherit' });
          child.once('error', () => { console.error('Could not start the protected ACI task process.'); process.exit(1); });
          child.once('exit', (code) => process.exit(code ?? 1));
          for (const signal of ['SIGTERM', 'SIGINT']) process.on(signal, () => child.kill(signal));
        });
        """;

    private string? Probe(RunIsolationOptions options, IProbeRunner runner)
    {
        var root = Directory.CreateDirectory(Path.Combine(options.WorkRoot, $".aci-probe-{Guid.NewGuid():N}")).FullName;
        string? sockets = null;
        string? privateFixture = null;
        using var stop = new CancellationTokenSource();
        var outside = new TcpListener(IPAddress.Loopback, 0);
        Task? response = null;
        try
        {
            sockets = CreateSocketDirectory();
            privateFixture = Directory.CreateTempSubdirectory("mbus-aci-private-").FullName;
            var run = Directory.CreateDirectory(Path.Combine(root, "run")).FullName;
            Directory.CreateDirectory(Path.Combine(run, "home"));
            var other = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
            var machineKey = Path.Combine(privateFixture, "machine-key");
            var otherFile = Path.Combine(other, "other-run");
            File.WriteAllText(machineKey, "dummy key");
            File.WriteAllText(otherFile, "dummy work");

            outside.Start();
            var port = ((IPEndPoint)outside.LocalEndpoint).Port;
            response = ReplyAsync(outside, stop.Token);
            using var network = RunNetwork.StartAci(Path.Combine(sockets, SocketName), new HostAllowList([FixtureHost]), TextWriter.Null, "probe",
                async (_, _, ct) =>
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                });

            var environment = RunEnvironmentScrubber.Build(Environment.GetEnvironmentVariables(), [],
                new Dictionary<string, string?> { ["PATH"] = "/usr/local/bin:/usr/bin:/bin", ["LC_ALL"] = "C" });
            // Recovery precedes readiness even if this host now supports native
            // procfs. Backend selection remains a separate per-Codex fallback.
            if (File.Exists("/usr/local/lib/mbus/gvisor/runsc"))
            {
                _userspace = AciUserspaceSandbox.TryCreate(options, out var runtimeProblem);
                if (_userspace is null) return runtimeProblem;
            }
            ProbeOutcome RunUsing(string file, string[] args, bool userspace = false)
            {
                var launch = new ProcessLaunch { FileName = file, Arguments = args, WorkingDirectory = run, Environment = environment };
                return runner.Run(new ProbeCommand("protected ACI launch", Wrap(launch, run, sockets, useUserspace: userspace), launch, Sandboxed: true));
            }
            ProbeOutcome Run(string file, params string[] args) => RunUsing(file, args);
            ProbeOutcome RunAgent(SandboxProbeAgent agent, params string[] args) => RunUsing(agent.Executable, args,
                userspace: agent.Name == "codex" && _userspaceForCodex);

            ProbeOutcome Check(string pid, string net, string markerPid, bool userspace = false) =>
                RunUsing(Node, ["-e", Fixture, "--", machineKey, otherFile, pid, net, markerPid,
                    port.ToString(CultureInfo.InvariantCulture), ProxyPort.ToString(CultureInfo.InvariantCulture), FixtureHost], userspace);
            var check = Check(new FileInfo("/proc/self/ns/pid").LinkTarget!, new FileInfo("/proc/self/ns/net").LinkTarget!, "");
            if (check.ExitCode == 1 && check.Output.TrimEnd().Equals(ProcMountDenied, StringComparison.Ordinal))
            {
                // Some ACI hosts allow namespaces but deny a fresh procfs mount.
                // Keep all namespaces and give the task a private, empty /proc.
                _mountProc = false;
                _userspaceForCodex = _userspace is not null;
                check = CheckWithOutsideProcess(pid => Check("", "", pid));
            }
            if (check.ExitCode != 0 || !check.Output.Contains("aci-fixture-ok", StringComparison.Ordinal))
                return "The protected ACI file, process and network launch check failed. Task execution requires non-root namespace support.";

            foreach (var agent in options.ProbeAgents)
            {
                string? reason = null;
                if (_userspaceForCodex && agent.Name == "codex")
                {
                    var userspaceCheck = CheckWithOutsideProcess(marker =>
                    {
                        environment["MBUS_ACI_PROBE_MARKER"] = marker;
                        return Check("userspace", "", "", userspace: true);
                    }, namedMarker: true);
                    if (userspaceCheck.ExitCode != 0 || !userspaceCheck.Output.Contains("aci-fixture-ok", StringComparison.Ordinal))
                        reason = "Codex did not pass the protected private-procfs boundary check.";
                }
                if (reason is null && RunAgent(agent, "--version").ExitCode != 0)
                    reason = "The harness did not start inside the protected ACI sandbox.";
                if (reason is null && !_mountProc && RunAgent(agent, "--help").ExitCode != 0)
                    reason = "The harness did not start with an empty /proc inside the protected ACI sandbox.";
                if (reason is null && _userspaceForCodex && agent.Name == "codex")
                {
                    File.WriteAllText(Path.Combine(run, "work", "aci-probe-source"), "original");
                    foreach (var canEdit in new[] { true, false })
                    {
                        var launch = new ProcessLaunch { FileName = Node, Arguments = ["/usr/local/lib/mbus/aci-codex-probe.mjs", agent.Executable, run, canEdit ? "write" : "read"],
                            WorkingDirectory = Path.Combine(run, "work"), Environment = environment };
                        var outcome = runner.Run(new ProbeCommand("protected Codex command", Wrap(launch, run, sockets, canEdit, useUserspace: true), launch, Sandboxed: true));
                        if (outcome.ExitCode != 0 || !outcome.Output.Contains("aci-codex-ok", StringComparison.Ordinal))
                            reason = "Codex did not complete the protected offline command check for both file profiles.";
                    }
                }
                else if (reason is null && agent.Name == "codex"
                    && Run(agent.Executable, "sandbox", "--", "/bin/true").ExitCode != 0
                    && Run(agent.Executable, "sandbox", "linux", "--", "/bin/true").ExitCode != 0)
                    reason = "Codex could not start its command sandbox inside the protected ACI sandbox.";
                _agents[agent.Name] = reason;
            }
            return null;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (_userspace is not null)
            {
                try { _userspace.EndAsync(Path.Combine(root, "run")).GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cleanupFailure = ex; }
            }
            stop.Cancel();
            outside.Stop();
            if (response is not null)
            {
                try { response.GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            }
            if (cleanupFailure is null)
            {
                RunWorkspace.TryDelete(root);
                if (sockets is not null) RunWorkspace.TryDelete(sockets);
            }
            if (privateFixture is not null) RunWorkspace.TryDelete(privateFixture);
            if (cleanupFailure is not null) throw new IOException("Protected ACI startup cleanup is quarantined; its runtime journal and mount sources are preserved.", cleanupFailure);
        }
    }

    private static ProbeOutcome CheckWithOutsideProcess(Func<string, ProbeOutcome> check, bool namedMarker = false)
    {
        // Only the dummy subprocess is used to check PID visibility. Its input
        // pipe keeps it alive until the check returns.
        var markerName = "aci-" + Guid.NewGuid().ToString("N")[..10];
        var start = new ProcessStartInfo(namedMarker ? Node : "/usr/bin/cat")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = namedMarker,
        };
        if (namedMarker)
        {
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("process.title=process.argv[1];console.log('ready');process.stdin.resume();");
            start.ArgumentList.Add(markerName);
        }
        start.Environment.Clear();
        using var marker = Process.Start(start) ?? throw new IOException("Could not start the protected ACI fixture process.");
        try
        {
            if (namedMarker && marker.StandardOutput.ReadLine() != "ready") return new(1, "The outside marker did not start.");
            var result = check(namedMarker ? markerName : marker.Id.ToString(CultureInfo.InvariantCulture));
            return marker.HasExited ? new ProbeOutcome(1, "The outside fixture process stopped.") : result;
        }
        finally
        {
            marker.StandardInput.Close();
            marker.WaitForExit();
        }
    }

    private static async Task ReplyAsync(TcpListener listener, CancellationToken ct)
    {
        using var client = await listener.AcceptSocketAsync(ct).ConfigureAwait(false);
        await using var stream = new NetworkStream(client);
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { Length: > 0 }) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\npassed"u8.ToArray(), ct).ConfigureAwait(false);
    }

    private const string Fixture = """
        const fs = require('node:fs');
        const { connect } = require('node:net');
        const { get } = require('node:http');
        const [key, other, pidNs, netNs, markerPid, outside, proxy, host] = process.argv.slice(1);
        function outsideProcessIsHidden() {
          if (pidNs === 'userspace') {
            const status = fs.readFileSync('/proc/self/status', 'utf8');
            if (!['CapEff', 'CapPrm', 'CapInh', 'CapAmb', 'CapBnd'].every(name => new RegExp(name + ':\\s+0+\\n').test(status))) return false;
            try { fs.renameSync(process.cwd() + '/work', process.cwd() + '/replaced-work'); return false; }
            catch (error) { if (error.code !== 'EBUSY') return false; }
            const marker = process.env.MBUS_ACI_PROBE_MARKER;
            if (!marker) return false;
            return fs.readdirSync('/proc').filter(p => /^\d+$/.test(p)).every(p => {
              try { return !fs.readFileSync('/proc/' + p + '/cmdline', 'utf8').includes(marker); } catch { return true; }
            });
          }
          if (!markerPid) {
            return fs.readlinkSync('/proc/self/ns/pid') !== pidNs && fs.readlinkSync('/proc/self/ns/net') !== netNs;
          }
          if (fs.readdirSync('/proc').length !== 0 || Number(markerPid) <= process.pid) return false;
          try {
            process.kill(Number(markerPid), 0); // Only check visibility. No signal is sent.
          } catch (error) {
            return error.code === 'ESRCH';
          }
          return false;
        }
        (async () => {
          fs.writeFileSync('own-file', 'mine');
          if (fs.readFileSync('own-file', 'utf8') !== 'mine' || fs.existsSync(key) || fs.existsSync(other)
            || process.getuid() !== 10001 || !outsideProcessIsHidden()) process.exit(1);
          await new Promise((resolve, reject) => {
            const client = connect({ host: '127.0.0.1', port: Number(outside) });
            client.once('connect', () => { client.destroy(); reject(new Error('direct outside connection')); });
            client.once('error', resolve);
          });
          const body = await new Promise((resolve, reject) => {
            get({ hostname: '127.0.0.1', port: Number(proxy), path: `http://${host}:443/check` }, response => {
              let body = '';
              response.setEncoding('utf8');
              response.on('data', chunk => { body += chunk; });
              response.on('error', reject);
              response.on('end', () => resolve(body));
            }).on('error', reject);
          });
          if (body !== 'passed') process.exit(1);
          console.log('aci-fixture-ok');
        })().catch(() => process.exit(1));
        """;
}
