using System.Collections.Concurrent;
using System.ComponentModel;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>How strongly runs are kept apart from the machine and from each other.</summary>
public enum IsolationLevel
{
    /// <summary>None: the agent inherits the runner's environment and git settings (the owner turned it off).</summary>
    Off,
    /// <summary>
    /// Every platform: the run's own worktree, temp folder and git settings, no inherited secrets,
    /// and (on Windows) everything it started ended when it's over.
    /// </summary>
    Basic,
    /// <summary><see cref="Basic"/>, plus an OS sandbox that keeps the run out of personal files and other runs (macOS; Linux with bubblewrap, the original issue).</summary>
    OsSandbox,
}

/// <summary>Environment variables for run isolation (set by the machine's owner).</summary>
public static class SandboxEnvVars
{
    /// <summary><c>os</c> (the default: the strongest this machine supports), <c>basic</c> or <c>off</c>.</summary>
    public const string Isolation = "MBUS_RUNNER_ISOLATION";

    /// <summary>More variables runs may inherit, comma-separated (for example <c>AWS_PROFILE,AWS_REGION</c> for Bedrock).</summary>
    public const string PassEnvironment = "MBUS_RUNNER_PASS_ENV";

    /// <summary>More folders runs may not read or write, separated like PATH (the macOS and Linux sandboxes only).</summary>
    public const string PrivatePaths = "MBUS_RUNNER_PRIVATE_PATHS";

    /// <summary><c>limit</c> (the default: each run reaches only its allow-list, the original issue) or <c>open</c> (runs reach any host).</summary>
    public const string Network = "MBUS_RUNNER_NETWORK";

    /// <summary>More hosts every run may reach, comma-separated (<c>*.example.com</c> for every name under a domain).</summary>
    public const string AllowHosts = "MBUS_RUNNER_ALLOW_HOSTS";
}

/// <summary>How run isolation is set up on this machine.</summary>
public sealed record RunIsolationOptions
{
    /// <summary>The selected machine key backend.</summary>
    public string? KeyStore { get; init; }

    /// <summary>The runner's work folder (one directory per run).</summary>
    public required string WorkRoot { get; init; }

    /// <summary>The machine owner's home folder.</summary>
    public required string HomeDirectory { get; init; }

    /// <summary>What the owner asked for; the runner falls back to what this machine can do.</summary>
    public IsolationLevel Requested { get; init; } = IsolationLevel.OsSandbox;

    /// <summary>Variables runs may inherit on top of the built-in allow-list.</summary>
    public IReadOnlyList<string> PassEnvironment { get; init; } = [];

    /// <summary>Folders runs may not read or write on top of the built-in ones (macOS sandbox only).</summary>
    public IReadOnlyList<string> PrivatePaths { get; init; } = [];

    /// <summary>Issues each run's repository credential.</summary>
    public IRepoCredentialSource Credentials { get; init; } = new NoRepoCredentials();

    /// <summary>
    /// Whether a run uses the machine owner's own git settings and sign-in instead of a per-run credential (the original issue): set
    /// by the runner from its work policy, true only for the owner's own projects when they chose it.
    /// </summary>
    public Func<TaskDelivery, bool>? UseOwnerGit { get; init; }

    /// <summary>Where each run's agent sends its own telemetry, with a credential for that run alone (the original issue).</summary>
    public IRunTelemetrySource Telemetry { get; init; } = new NoRunTelemetry();

    /// <summary>git, to read the owner's name and email for commits made in runs; null when git isn't installed.</summary>
    public string? Git { get; init; }

    /// <summary>This machine's name, retained for callers of the isolation options.</summary>
    public string MachineName { get; init; } = "runner";

    /// <summary>The agent CLIs this runner starts; the macOS sandbox's start-up check runs <c>--version</c> of each inside it (the original issue).</summary>
    public IReadOnlyList<SandboxProbeAgent> ProbeAgents { get; init; } = [];

    /// <summary>Variables every run gets on top (per-user tool folders on PATH, the original issue), so the start-up check runs commands as a task would.</summary>
    public IReadOnlyDictionary<string, string> ToolVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether the runner serves each run's mbus tools itself (the original issue), so runs need nothing of mbus's own settings.</summary>
    public bool RunTools { get; init; }

    /// <summary>mbus's settings folder (<c>~/.mbus</c> or <c>MBUS_HOME</c>): with <see cref="RunTools"/>, runs may not read it (macOS sandbox), only their own run folder in it.</summary>
    public string? SettingsDirectory { get; init; }

    /// <summary>Whether each run's network is limited to its allow-list (the original issue; <see cref="SandboxEnvVars.Network"/>).</summary>
    public bool LimitNetwork { get; init; }

    /// <summary>Hosts every run may reach (<see cref="RunNetwork.BaseHosts"/>); the task's repository and the role's hosts are added per run.</summary>
    public IReadOnlyList<string> NetworkHosts { get; init; } = [];

    /// <summary>The proxy this runner reaches the internet through (its own <c>HTTPS_PROXY</c> and so on, the original issue); null connects directly.</summary>
    public UpstreamProxy? Upstream { get; init; }

    /// <summary>
    /// The runner's one run-network proxy, on a port kept across restarts (the original issue, Windows): each run gets a credential on it
    /// instead of a proxy of its own, and runs whose role may reach any host go through it too, so every run sees the same
    /// port. Null: each run gets a proxy of its own.
    /// </summary>
    public RunNetworkProxy? SharedNetwork { get; init; }

    /// <summary>Why the runner's proxy setting can't be used, when it can't (for the start-up log).</summary>
    public string? UpstreamProblem { get; init; }

    /// <summary>Options from <see cref="SandboxEnvVars"/>.</summary>
    /// <exception cref="FormatException"><see cref="SandboxEnvVars.Isolation"/> has an unknown value.</exception>
    public static RunIsolationOptions FromEnvironment(Func<string, string?> env, string workRoot, string homeDirectory, string? git, string machineName) => new()
    {
        WorkRoot = workRoot,
        HomeDirectory = homeDirectory,
        Requested = RunnerEnvironment.NonEmpty(env(SandboxEnvVars.Isolation))?.ToLowerInvariant() switch
        {
            null or "os" => IsolationLevel.OsSandbox,
            "basic" => IsolationLevel.Basic,
            "off" => IsolationLevel.Off,
            var other => throw new FormatException($"{SandboxEnvVars.Isolation} is '{other}'; use os, basic or off."),
        },
        PassEnvironment = (RunnerEnvironment.NonEmpty(env(SandboxEnvVars.PassEnvironment)) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        PrivatePaths = (RunnerEnvironment.NonEmpty(env(SandboxEnvVars.PrivatePaths)) ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Git = git,
        MachineName = machineName,
        LimitNetwork = RunnerEnvironment.NonEmpty(env(SandboxEnvVars.Network))?.ToLowerInvariant() switch
        {
            null or "limit" => true,
            "open" => false,
            var other => throw new FormatException($"{SandboxEnvVars.Network} is '{other}'; use limit or open."),
        },
        Upstream = UpstreamProxy.FromEnvironment(env, out var upstreamProblem),
        UpstreamProblem = upstreamProblem,
    };
}

/// <summary>
/// Run isolation (the original issue): each run gets its own worktree (cleaned as the role says), temp folder and
/// git settings; inherits only an allow-list of the runner's environment plus its own MBUS_*
/// variables; gets a repository credential scoped to its task's repository that is withdrawn when
/// it ends; and has every process it started ended when it's over (a job object on Windows, the run's
/// marker variable on macOS and Linux, the original issue). On macOS (sandbox-exec) and Linux (bubblewrap) an OS sandbox
/// also keeps it out of personal files and other runs, when this machine shows it enforces that. What a
/// platform can't enforce is said once, at start-up (<see cref="Limitations"/>).
/// </summary>
public sealed class RunIsolation : IRunSandbox
{
    private readonly RunIsolationOptions _options;
    private readonly TextWriter _log;
    private readonly MacSandbox? _mac;
    private readonly LinuxSandbox? _linux;
    private readonly GitIdentity _identity;
    private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);
    private int _jobWarned;

    private RunIsolation(RunIsolationOptions options, TextWriter log, OsSandboxAttempt os, GitIdentity identity)
    {
        _options = options;
        _log = log;
        _mac = os.Mac;
        _linux = os.Linux;
        _identity = identity;
        Level = os.Mac is not null || os.Linux is not null ? IsolationLevel.OsSandbox : IsolationLevel.Basic;
        SandboxName = os.Name;
        SandboxProblem = os.Tried && Level != IsolationLevel.OsSandbox ? os.Problem : null;
        var limits = new List<string>();
        if (options.Requested == IsolationLevel.OsSandbox && Level != IsolationLevel.OsSandbox)
        {
            limits.Add(os.Tried
                ? "Files outside the worktree are limited only by the agent's own permission settings."
                : OperatingSystem.IsWindows()
                    ? "Windows can't keep a run out of your other files without admin rights, so files outside the worktree are limited only by the agent's own permission settings."
                    : "Runs on this OS aren't sandboxed, so files outside the worktree are limited only by the agent's own permission settings.");
        }
        if (os.Mac is { NestingAllowed: false })
        {
            // the original issue: Codex runs that edit files run inside this sandbox without their own (CodexAdapter).
            limits.Add("Codex runs that only read keep Codex's own sandbox instead, because this Mac doesn't allow one sandbox inside another.");
        }
        else if (os.Linux is { NestingAllowed: false })
        {
            limits.Add("Codex runs keep Codex's own sandbox instead, because this machine doesn't allow one sandbox inside another.");
        }
        else if (os.Mac is null && os.Linux is null && OperatingSystem.IsWindows() && options.Requested != IsolationLevel.Off)
        {
            // the original issue: Codex's own sandbox without admin rights (CodexAdapter); its network goes through the runner's proxy like every run's.
            limits.Add("Codex runs keep Codex's own sandbox, which needs no admin rights and lets them write only to the worktree and the run's caches; their network is limited as for every run here.");
        }
        limits.AddRange(os.Mac?.Notes ?? os.Linux?.Notes ?? []);
        if (!OperatingSystem.IsWindows() && os.Linux is null)
        {
            // the original issue: the run's marker variable finds them; the Linux sandbox's PID namespace leaves nothing to find.
            limits.Add("A program a run leaves running in the background is ended when the run finishes, unless it was started with its environment cleared.");
        }
        NetworkEnforced = options.LimitNetwork && os.Mac is { NetworkEnforced: true };
        limits.Add(!options.LimitNetwork ? NetworkOpen : NetworkEnforced ? NetworkLimitEnforced : NetworkLimitAdvisory);
        if (options.LimitNetwork && (options.Upstream is not null || options.UpstreamProblem is not null))
        {
            // the original issue: what the runner's own proxy means for runs.
            limits.Add(options.Upstream is { } upstream
                ? $"Network: allowed connections go through this runner's proxy ({upstream.Describe()}), except hosts in its NO_PROXY."
                : $"Network: {options.UpstreamProblem}.");
        }
        if (options.RunTools)
        {
            limits.Add(options.KeyStore == "file" ? MachineKeyLimit(os.Mac is not null || os.Linux is not null, "file") : os.Linux is not null
                ? "This machine's key: runs can't read mbus's settings folder, but a program that asks your desktop keyring for the key by name may still get it."
                : MachineKeyLimit(os.Mac is not null));
        }
        Limitations = limits;
    }

    /// <inheritdoc />
    /// <remarks>
    /// macOS only: a Mac can't start Codex's sandbox inside this one, which left Codex's own as the only layer, and that one
    /// stops builds (package caches, the network) and commits (it keeps .git read-only). The Linux sandbox keeps Codex's own.
    /// </remarks>
    public bool ConfinesAgents => _mac is not null;

    /// <summary>Whether the OS stops a run from going around its network allow-list (the macOS sandbox, when its start-up check saw it work).</summary>
    public bool NetworkEnforced { get; }

    /// <summary>The runner's network setting is open (the original issue).</summary>
    internal const string NetworkOpen =
        "Network: not limited (MBUS_RUNNER_NETWORK=open). A role's network setting limits only Claude Code's web tools and, where Codex keeps its own sandbox, "
        + "the commands Codex runs; on a Mac, Codex runs that edit files have no sandbox of their own, so nothing limits them. "
        + "Other commands an agent runs, such as curl or a package install, can reach any host.";

    /// <summary>macOS with its sandbox: the allow-list holds (the original issue).</summary>
    internal const string NetworkLimitEnforced =
        "Network: each run reaches only its allow-list (its repository, the model providers, package registries, this server and the role's hosts) "
        + "through a proxy on this computer, and the macOS sandbox blocks every other connection except to this computer itself. "
        + "Host names can still be looked up. A role set to any host isn't limited.";

    /// <summary>Windows, Linux, or macOS without its sandbox: the proxy's list holds only for programs that use it (the original issue).</summary>
    internal static string NetworkLimitAdvisory =>
        "Network: each run is pointed at a proxy on this computer that lets through only its allow-list (its repository, the model providers, "
        + "package registries, this server and the role's hosts), but "
        + (OperatingSystem.IsWindows() ? "Windows can't force a program through it without admin rights or firewall rules"
            : OperatingSystem.IsMacOS() ? "without the macOS sandbox nothing forces a program through it"
            : "this OS can't force a program through it without root")
        + ", so a program that ignores proxy settings can still reach any host. A role set to any host isn't limited.";

    /// <summary>What still reaches this machine's key from inside a run, when runs get their own credential (the original issue).</summary>
    internal static string MachineKeyLimit(bool sandboxed, string? keyStore = null) =>
        keyStore == "file"
            ? sandboxed ? "This machine's key: runs can't read mbus's settings folder, including its key file."
                : "This machine's key: a program a run starts runs as you and can read the key file in mbus's settings folder."
            :
        OperatingSystem.IsWindows()
            ? "This machine's key: runs aren't given it, but a program a run starts runs as you, and Windows protects the key for your account, not from your own programs."
            : OperatingSystem.IsMacOS() && Security.MacCodeSignature.IsDeveloperIdSigned
                // the original issue: the Keychain gives the key only to mbus, and mbus won't use it inside a run.
                ? "This machine's key: the Keychain gives it only to mbus, which won't use it inside a run; any other program asking for it makes macOS ask you first."
                : sandboxed
                    ? "This machine's key: runs can't read mbus's settings folder, but a program that asks your login Keychain for the key by name can still get it (agents need the Keychain for their own sign-in)."
                    : "This machine's key: runs aren't given it, but a program a run starts runs as you and can read mbus's settings folder.";

    /// <summary>What the role's Network setting does and doesn't limit with run isolation off (the original issue).</summary>
    internal const string NetworkLimit =
        "Network: not limited, because run isolation is off. A role's network setting limits only Claude Code's web tools "
        + "(WebFetch, WebSearch) and whether Codex's own sandbox lets the commands Codex runs use the network; other commands "
        + "an agent runs, such as curl or a package install, can reach any host.";

    /// <summary>What stops a run from pushing when its role can't push (the original issue).</summary>
    internal const string PushLimit =
        "Push: when a role can't push, each run's git settings send every push to an address git refuses and a hook refuses it, "
        + "pushes over SSH are refused before they reach this machine's SSH keys, and the run's repository credential is read-only.";

    /// <summary>What runs on this machine get.</summary>
    public IsolationLevel Level { get; }

    /// <summary>The OS sandbox this machine has or would have: "macOS sandbox", "Linux sandbox (bubblewrap)"; null where there is none.</summary>
    public string? SandboxName { get; }

    /// <summary>Why the OS sandbox isn't used here (names the start-up check step that failed); null when it is, or wasn't asked for.</summary>
    public string? SandboxProblem { get; }

    /// <summary>For the Runners page's Run isolation line (one short sentence).</summary>
    public string Description => Level == IsolationLevel.OsSandbox
        ? "OS sandbox: runs can't reach your personal files or other runs, and write only in their worktree."
        : "Own worktree and settings for each run, with no secrets from this machine.";

    /// <summary>For the Runners page's Credentials line (short sentences).</summary>
    public string CredentialsDescription => _options.RunTools
        ? _options.Credentials.Description + " mbus tools: a credential for the run's own task, which ends with the run; never this machine's key."
        : _options.Credentials.Description;

    /// <summary>Rules this machine can't enforce, each one sentence.</summary>
    public IReadOnlyList<string> Limitations { get; }

    /// <inheritdoc />
    public RunIsolationReport Report => new()
    {
        Summary = Description,
        Credentials = CredentialsDescription,
        Limitations = [.. (SandboxProblem is { } why ? new[] { $"The {SandboxName} isn't used: {why}." } : []), PushLimit, .. Limitations],
        // the original issue: the same in two words, for the Runners page's one-line summary.
        Level = Level == IsolationLevel.OsSandbox ? "os-sandbox" : "basic",
        Network = !_options.LimitNetwork ? "open" : NetworkEnforced ? "enforced" : "advisory",
    };

    /// <summary>
    /// The sandbox for <paramref name="options"/>: <see cref="NoSandbox"/> when turned off, else run
    /// isolation at the strongest level this machine showed it enforces. Logs what runs get.
    /// </summary>
    public static IRunSandbox Create(RunIsolationOptions options, TextWriter log) => Create(options, log, macProbe: null);

    /// <summary><see cref="Create(RunIsolationOptions, TextWriter)"/>; with <paramref name="macProbe"/>, the macOS sandbox's start-up check runs its commands through it (tests), on any OS.</summary>
    internal static IRunSandbox Create(RunIsolationOptions options, TextWriter log, IProbeRunner? macProbe) => Create(options, log, macProbe, linuxProbe: null);

    /// <summary>
    /// <see cref="Create(RunIsolationOptions, TextWriter)"/>; with <paramref name="linuxProbe"/> (and <paramref name="bubblewrap"/>),
    /// the Linux sandbox's start-up check runs its commands through it (tests), on any OS.
    /// </summary>
    internal static IRunSandbox Create(RunIsolationOptions options, TextWriter log, IProbeRunner? macProbe, IProbeRunner? linuxProbe, string? bubblewrap = null)
    {
        if (options.Requested == IsolationLevel.Off)
        {
            log.WriteLine("Run isolation: off. Runs inherit this machine's environment and git settings.");
            log.WriteLine($"  Push: a role that can't push is stopped only by the agent's own command rules. {NetworkLimit}");
            if (options.KeyStore == "file") log.WriteLine("Warning: " + MachineKeyLimit(false, "file"));
            return new NoSandbox();
        }
        var isolation = new RunIsolation(options, log, TryOsSandbox(options, macProbe, linuxProbe, bubblewrap), ReadIdentity(options));
        log.WriteLine(isolation.SandboxProblem is { } why
            ? $"Run isolation: basic, not the {isolation.SandboxName}: {why}. {isolation.Description}"
            : $"Run isolation: {isolation.Description}");
        log.WriteLine($"Credentials: {isolation.CredentialsDescription}");
        log.WriteLine($"  {PushLimit}");
        foreach (var limit in isolation.Limitations)
        {
            log.WriteLine($"  {limit}");
        }
        return isolation;
    }

    /// <summary>What trying this machine's OS sandbox gave.</summary>
    private sealed record OsSandboxAttempt(bool Tried, string? Name, MacSandbox? Mac, LinuxSandbox? Linux, string? Problem);

    private const string MacName = "macOS sandbox";
    private const string LinuxName = "Linux sandbox (bubblewrap)";

    private static OsSandboxAttempt TryOsSandbox(RunIsolationOptions options, IProbeRunner? macProbe, IProbeRunner? linuxProbe, string? bubblewrap)
    {
        if (options.Requested != IsolationLevel.OsSandbox)
        {
            return new(false, null, null, null, null);
        }
        string? problem;
        if (macProbe is not null || (linuxProbe is null && OperatingSystem.IsMacOS()))
        {
            if (macProbe is null && !File.Exists(MacSandbox.SandboxExec))
            {
                return new(true, MacName, null, null, "this Mac has no sandbox-exec");
            }
            var mac = MacSandbox.TryCreate(options, macProbe ?? new ProcessProbeRunner(), out problem);
            return new(true, MacName, mac, null, problem);
        }
        if (linuxProbe is not null || OperatingSystem.IsLinux())
        {
            // the original issue: bubblewrap, where it is installed and this machine lets it make namespaces (the start-up check shows it).
            var bwrap = bubblewrap ?? CommandLocator.Find("bwrap", Environment.GetEnvironmentVariable, ["/usr/bin", "/usr/local/bin"]);
            if (bwrap is null)
            {
                return new(true, LinuxName, null, null, "bubblewrap isn't installed (install the bubblewrap package to sandbox runs)");
            }
            var linux = LinuxSandbox.TryCreate(options, bwrap, linuxProbe ?? new ProcessProbeRunner(), out problem);
            return new(true, LinuxName, null, linux, problem);
        }
        return new(false, null, null, null, null);
    }

    /// <inheritdoc />
    public async Task PrepareAsync(RunWorkspace workspace, TaskDelivery delivery, PermissionProfile profile, CancellationToken cancellationToken)
    {
        var repository = delivery.Envelope.Refs
            .Select(RunWorkspace.RepositoryUrl)
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null)
            .FirstOrDefault(uri => uri is not null);
        var ownerGit = _options.UseOwnerGit?.Invoke(delivery) == true;
        var credential = repository is null || ownerGit ? null : await _options.Credentials.IssueAsync(new RepoCredentialRequest
        {
            DeliveryId = delivery.DeliveryId,
            WorkspaceId = delivery.Envelope.WorkspaceId,
            Repository = repository,
            CanPush = profile.CanPush,
        }, cancellationToken).ConfigureAwait(false);
        var telemetry = await _options.Telemetry.IssueAsync(delivery.DeliveryId, cancellationToken).ConfigureAwait(false);
        RunNetwork? network = null;
        if (_options.LimitNetwork && profile.Network != NetworkAccess.Open)
        {
            // the original issue: its own proxy, with the hosts every run may reach plus its repository and its role's hosts.
            IEnumerable<string> hosts = [.. _options.NetworkHosts, .. repository is null ? [] : RunNetwork.RepositoryHosts(repository), .. profile.AllowedHosts];
            network = _options.SharedNetwork is { } shared
                ? shared.Register(new HostAllowList(hosts), delivery.DeliveryId)
                : RunNetwork.Start(new HostAllowList(hosts), _log, delivery.DeliveryId, upstream: _options.Upstream);
        }
        else if (_options.LimitNetwork && _options.SharedNetwork is { } shared)
        {
            // the original issue: a role that may reach any host still goes through the one proxy.
            network = shared.Register(allow: null, delivery.DeliveryId);
        }
        try
        {
            if (network is not null) network.LocalModelEndpoint = workspace.LocalModelEndpoint;
            _runs[workspace.Root] = NewState(workspace, repository, credential, profile.CanPush, network, telemetry, ownerGit);
        }
        catch
        {
            // Nothing will end this run's place on the proxy otherwise.
            network?.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile)
    {
        var state = _runs.GetOrAdd(workspace.Root, _ => NewState(workspace, null, null, profile.CanPush, null));
        var environment = RunEnvironmentScrubber.Build(Environment.GetEnvironmentVariables(), _options.PassEnvironment,
            launch.Environment.Concat(state.Variables));
        var wrapped = launch with { Environment = environment };
        if (_mac is not null && (_mac.NestingAllowed || !MacSandbox.StartsItsOwnSandbox(launch)))
        {
            wrapped = _mac.Wrap(wrapped, workspace.Root);
        }
        else if (_linux is not null && (_linux.NestingAllowed || !MacSandbox.StartsItsOwnSandbox(launch)))
        {
            wrapped = _linux.Wrap(wrapped, workspace.Root, workspace.SessionFolder);
        }
        return wrapped;
    }

    /// <inheritdoc />
    public void Started(RunWorkspace workspace, int processId)
    {
        if (!OperatingSystem.IsWindows() || !_runs.TryGetValue(workspace.Root, out var state))
        {
            return;
        }
        try
        {
            lock (state)
            {
                state.Job ??= WindowsJob.Create();
                state.Job.Add(processId);
            }
        }
        catch (Win32Exception ex)
        {
            // Rare (a runner inside a job that forbids it); the process tree is still ended on cancel.
            if (Interlocked.Exchange(ref _jobWarned, 1) == 0)
            {
                _log.WriteLine($"Couldn't group a run's processes to end them together: {ex.Message}");
            }
        }
    }

    /// <inheritdoc />
    public void Ended(RunWorkspace workspace)
    {
        if (!_runs.TryRemove(workspace.Root, out var state))
        {
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            lock (state)
            {
                state.Job?.Terminate();
                state.Job?.Dispose();
                state.Job = null;
            }
        }
        else
        {
            // the original issue: what the run left running, wherever it went (a new session, a parent that exited).
            PosixRunProcesses.EndMarked(state.Marker);
        }
        state.Network?.Dispose();
        RunGitConfig.Withdraw(workspace.Root);
        RunWorkspace.TryDelete(state.TempDirectory);
    }

    private RunState NewState(RunWorkspace workspace, Uri? repository, RepoCredential? credential, bool canPush, RunNetwork? network,
        RunTelemetrySettings? telemetry = null, bool ownerGit = false)
    {
        var temp = Directory.CreateDirectory(Path.Combine(workspace.Root, "tmp")).FullName;
        var marker = PosixRunProcesses.NewMarker();
        // Git over SSH goes through the proxy too where the sandbox would block it otherwise (macOS).
        var sshProxy = network is { NeedsCredential: false } && NetworkEnforced ? $"127.0.0.1:{network.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : null;
        var variables = new Dictionary<string, string?>(RunGitConfig.Write(workspace.Root, _identity, repository, credential, canPush, sshProxy,
            ownerGitConfig: ownerGit ? Path.Combine(_options.HomeDirectory, ".gitconfig") : null))
        {
            ["TMPDIR"] = temp,
            ["TMP"] = temp,
            ["TEMP"] = temp,
            [PosixRunProcesses.MarkerVariable] = marker,
        };
        if (OperatingSystem.IsMacOS())
        {
            // the original issue: Chrome and Chromium on macOS keep their sockets in the per-user temp folder macOS gives them, which a
            // run's sandbox doesn't share (only TMPDIR, in the run, is its own); this points them at the run's.
            variables[ChromiumTempVariable] = temp;
        }
        if (OperatingSystem.IsMacOS() || _linux is not null)
        {
            // the original issue: persistent MSBuild/Roslyn processes retain the sandbox of the run that
            // started them. Another run building at the same time must not delegate file access to them.
            // Keep parallel builds; only the processes that survive a build are disabled.
            variables["MSBUILDDISABLENODEREUSE"] = "1";
            variables["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            variables["UseSharedCompilation"] = "false";
        }
        foreach (var (name, value) in network?.Variables ?? new Dictionary<string, string?>())
        {
            variables[name] = value;
        }
        foreach (var (name, value) in telemetry?.Variables(Environment.GetEnvironmentVariables()) ?? new Dictionary<string, string?>())
        {
            variables[name] = value;
        }
        _mac?.WriteProfile(workspace.Root, limitNetwork: network is not null, sessionFolder: workspace.SessionFolder);
        return new RunState(temp, variables, network, marker);
    }

    /// <summary>Where Chromium on macOS puts its temporary files instead of the per-user temp folder (the original issue).</summary>
    internal const string ChromiumTempVariable = "MAC_CHROMIUM_TMPDIR";

    private static GitIdentity ReadIdentity(RunIsolationOptions options)
    {
        string? Read(string key)
        {
            if (options.Git is null)
            {
                return null;
            }
            try
            {
                string? value = null;
                using var git = AgentProcess.Start(new ProcessLaunch
                {
                    FileName = options.Git,
                    Arguments = ["config", "--global", "--get", key],
                    WorkingDirectory = options.HomeDirectory,
                }, line => value ??= line.Trim(), tailLines: 1);
                return git.WaitForExitAsync().GetAwaiter().GetResult() == 0 && value is { Length: > 0 } ? value : null;
            }
            catch (Win32Exception)
            {
                return null;
            }
        }
        // Use the owner's configured identity, with neutral defaults for fields they haven't set.
        return new GitIdentity(Read("user.name") ?? "mbus runner", Read("user.email") ?? "runner@messagebus.invalid");
    }

    private sealed class RunState(string tempDirectory, IReadOnlyDictionary<string, string?> variables, RunNetwork? network, string marker)
    {
        public string TempDirectory { get; } = tempDirectory;

        /// <summary>The value of the run's <see cref="PosixRunProcesses.MarkerVariable"/>.</summary>
        public string Marker { get; } = marker;

        public IReadOnlyDictionary<string, string?> Variables { get; } = variables;

        public RunNetwork? Network { get; } = network;

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public WindowsJob? Job { get; set; }
    }
}
