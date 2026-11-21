using System.ComponentModel;
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
    private readonly Dictionary<string, string?> _agents = new(StringComparer.Ordinal);
    private readonly AciEgress _egress;

    private AciSandbox(string identityHost) => _egress = new(identityHost);

    internal string? AgentProblem(AgentKind kind) => _agents.TryGetValue(AgentKinds.Command(kind), out var problem)
        ? problem : "The harness has not passed the protected ACI launch check. Restart the runner after installing it.";

    internal static AciSandbox? TryCreate(RunIsolationOptions options, out string problem)
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
            if (sandbox.Probe(options) is { } failure)
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

    internal static ProcessLaunch Wrap(ProcessLaunch launch, string run, string sockets)
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
        return launch with
        {
            FileName = Bwrap,
            // The image's toolchain is read-only. Never mount the outer root, a host
            // device tree, shared temporary directories, or any runner home files.
            Arguments = [
                "--unshare-user", "--unshare-pid", "--unshare-net", "--unshare-ipc", "--unshare-uts", "--cap-drop", "ALL", "--new-session",
                "--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp", "--tmpfs", "/var/tmp", "--tmpfs", "/dev/shm",
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

    private string? Probe(RunIsolationOptions options)
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

            var runner = new ProcessProbeRunner();
            var environment = RunEnvironmentScrubber.Build(Environment.GetEnvironmentVariables(), [],
                new Dictionary<string, string?> { ["PATH"] = "/usr/local/bin:/usr/bin:/bin" });
            ProbeOutcome Run(string file, params string[] args)
            {
                var launch = new ProcessLaunch { FileName = file, Arguments = args, WorkingDirectory = run, Environment = environment };
                return runner.Run(new ProbeCommand("protected ACI launch", Wrap(launch, run, sockets), launch, Sandboxed: true));
            }

            var pid = new FileInfo("/proc/self/ns/pid").LinkTarget!;
            var net = new FileInfo("/proc/self/ns/net").LinkTarget!;
            var check = Run(Node, "-e", Fixture, "--", machineKey, otherFile, pid, net,
                port.ToString(CultureInfo.InvariantCulture), ProxyPort.ToString(CultureInfo.InvariantCulture), FixtureHost);
            if (check.ExitCode != 0 || !check.Output.Contains("aci-fixture-ok", StringComparison.Ordinal))
                return "The protected ACI file, process and network launch check failed. Task execution requires non-root namespace support.";

            foreach (var agent in options.ProbeAgents)
            {
                string? reason = Run(agent.Executable, "--version").ExitCode == 0 ? null : "The harness did not start inside the protected ACI sandbox.";
                if (reason is null && agent.Name == "codex"
                    && Run(agent.Executable, "sandbox", "--", "/bin/true").ExitCode != 0
                    && Run(agent.Executable, "sandbox", "linux", "--", "/bin/true").ExitCode != 0)
                    reason = "Codex could not start its command sandbox inside the protected ACI sandbox.";
                _agents[agent.Name] = reason;
            }
            return null;
        }
        finally
        {
            stop.Cancel();
            outside.Stop();
            if (response is not null)
            {
                try { response.GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            }
            RunWorkspace.TryDelete(root);
            if (sockets is not null) RunWorkspace.TryDelete(sockets);
            if (privateFixture is not null) RunWorkspace.TryDelete(privateFixture);
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
        const [key, other, pidNs, netNs, outside, proxy, host] = process.argv.slice(1);
        (async () => {
          fs.writeFileSync('own-file', 'mine');
          if (fs.readFileSync('own-file', 'utf8') !== 'mine' || fs.existsSync(key) || fs.existsSync(other)
            || process.getuid() !== 10001 || fs.readlinkSync('/proc/self/ns/pid') === pidNs
            || fs.readlinkSync('/proc/self/ns/net') === netNs) process.exit(1);
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
