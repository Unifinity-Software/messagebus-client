namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>
/// The macOS sandbox for runs, through <c>sandbox-exec</c> (Seatbelt). Apple marks the command
/// deprecated but ships it and uses it itself; Codex and Claude Code use it the same way. The runner
/// only relies on it after <see cref="TryCreate"/> has watched it work on this Mac (the rules hold,
/// and git and every agent still work inside it), and otherwise says why and runs without it. No
/// admin rights, nothing changed on the machine.
/// </summary>
/// <remarks>
/// What a sandboxed run can and can't reach is <see cref="MacSandboxProfile"/>. The network isn't
/// limited by host here (Seatbelt can't filter by host name): with the original issue a run may connect only to this computer,
/// where its proxy lets through its allow-list.
/// </remarks>
internal sealed class MacSandbox : IOsSandbox
{
    public const string SandboxExec = "/usr/bin/sandbox-exec";
    public const string ProfileFileName = "sandbox.sb";

    private readonly string _home;
    private readonly string _workRoot;
    private readonly IReadOnlyList<string> _writableSystem;
    private readonly IReadOnlyList<string> _readable;
    private readonly IReadOnlyList<string> _private;

    private MacSandbox(string home, string workRoot, IReadOnlyList<string> writableSystem, IReadOnlyList<string> readable, IReadOnlyList<string> privatePaths)
    {
        _home = home;
        _workRoot = workRoot;
        _writableSystem = writableSystem;
        _readable = readable;
        _private = privatePaths;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> NestingCheck { get; } = [SandboxExec, "-p", "(version 1)(allow default)", "/usr/bin/true"];

    /// <inheritdoc />
    public bool ChecksNetwork => true;

    /// <summary>Whether a run may start its own sandbox inside this one (Codex does, for its commands).</summary>
    public bool NestingAllowed { get; private set; }

    /// <summary>Start-up check steps that were skipped because they fail outside the sandbox too.</summary>
    public IReadOnlyList<string> Notes { get; private set; } = [];

    /// <summary>Whether the start-up check saw the sandbox block a connection off this computer (the original issue); false when it wasn't checked.</summary>
    public bool NetworkEnforced { get; private set; }

    /// <summary>
    /// The sandbox, once its start-up check (<see cref="SandboxProbe"/>) passed under it on this Mac.
    /// Null, and why (naming the step that failed), when it doesn't.
    /// </summary>
    /// <param name="options">The runner's isolation settings (work folder, home folder, git, agents).</param>
    /// <param name="runner">Runs the check's commands.</param>
    /// <param name="problem">Why the sandbox can't be used.</param>
    public static MacSandbox? TryCreate(RunIsolationOptions options, IProbeRunner runner, out string? problem)
    {
        Directory.CreateDirectory(options.WorkRoot);
        var home = RealPath(options.HomeDirectory);
        var workRoot = RealPath(options.WorkRoot);
        // Shared temp folders, and the per-user cache (C) and user (0) folders next to TMPDIR's T, used
        // by system frameworks. TMPDIR itself (T) isn't shared with runs: each run has its own.
        var writable = new List<string> { "/dev", "/private/tmp", "/private/var/tmp" };
        var userTemp = RealPath(Path.GetTempPath());
        if (Path.GetFileName(userTemp) == "T" && Path.GetDirectoryName(userTemp) is { } perUser && perUser.StartsWith("/private/var/folders/", StringComparison.Ordinal))
        {
            writable.Add(MacSandboxProfile.Join(perUser, "C"));
            writable.Add(MacSandboxProfile.Join(perUser, "0"));
        }
        var readable = new List<string>();
        var mbusDir = RealPath(AppContext.BaseDirectory);
        if (SafeToAllow(mbusDir, home, workRoot))
        {
            // mbus itself: the agent starts `mbus mcp` from here.
            readable.Add(mbusDir);
        }
        var privatePaths = options.PrivatePaths.Select(RealPath).ToList();
        if (options.RunTools && options.SettingsDirectory is { } settings)
        {
            // the original issue: mbus's settings and the machine's key; the runner serves the run's mbus tools itself.
            privatePaths.Add(RealPath(settings));
        }
        var sandbox = new MacSandbox(home, workRoot, writable, readable, privatePaths);

        var probe = Path.Combine(workRoot, $".isolation-probe-{Environment.ProcessId}");
        try
        {
            RunWorkspace.TryDelete(probe);
            var result = SandboxProbe.Run(sandbox, options, runner, probe);
            sandbox.NestingAllowed = result.NestingAllowed;
            sandbox.Notes = result.Notes;
            sandbox.NetworkEnforced = result.NetworkEnforced;
            problem = result.Problem;
            return problem is null ? sandbox : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            problem = $"its start-up check couldn't run ({ex.Message})";
            return null;
        }
        finally
        {
            RunWorkspace.TryDelete(probe);
        }
    }

    /// <summary>The rules for a run in <paramref name="runDir"/> (a resolved path); <paramref name="sessionFolder"/> (resolved) it may also read.</summary>
    internal MacSandboxProfile ProfileFor(string runDir, bool limitNetwork = false, string? sessionFolder = null) => new(new MacSandboxPaths
    {
        Home = _home,
        WorkRoot = _workRoot,
        RunDirectory = runDir,
        WritableSystem = _writableSystem,
        // Never the home folder or above it, nor the work folder: those would open up far more than the session's own files.
        Readable = sessionFolder is not null && SafeToAllow(sessionFolder, _home, _workRoot) ? [.. _readable, sessionFolder] : _readable,
        PrivatePaths = _private,
        LimitNetwork = limitNetwork,
    });

    /// <inheritdoc />
    public void WriteProfile(string runRoot, bool limitNetwork = false) => WriteProfile(runRoot, limitNetwork, null);

    /// <summary>
    /// Writes the run's profile into its run directory; <paramref name="limitNetwork"/>: connections only to this computer (the original issue);
    /// <paramref name="sessionFolder"/>: a resumed session's own folder (the original issue), which it may read but not change.
    /// </summary>
    public void WriteProfile(string runRoot, bool limitNetwork, string? sessionFolder) =>
        File.WriteAllText(Path.Combine(runRoot, ProfileFileName),
            ProfileFor(RealPath(runRoot), limitNetwork, sessionFolder is null ? null : RealPath(sessionFolder)).Render());

    /// <summary><paramref name="launch"/>, started under the run's profile.</summary>
    public ProcessLaunch Wrap(ProcessLaunch launch, string runRoot) => launch with
    {
        FileName = SandboxExec,
        Arguments =
        [
            "-f", Path.Combine(runRoot, ProfileFileName),
            "-D", MacSandboxProfile.ExecDirParameter + "=" + ExecDir(launch.FileName, RealPath(runRoot)),
            launch.FileName, .. launch.Arguments,
        ],
        RawArguments = null,
    };

    /// <summary>
    /// The program's own folder, which it may read even where the rules would stop it; the run
    /// directory when that folder isn't safe to open up.
    /// </summary>
    internal string ExecDir(string fileName, string runDir) =>
        Path.GetDirectoryName(RealPath(fileName)) is { } dir && SafeToAllow(dir, _home, _workRoot) ? dir : runDir;

    /// <summary>
    /// Whether <paramref name="launch"/> starts its own macOS sandbox (Codex's <c>--sandbox</c>
    /// modes, for the commands it runs), which can't start inside another one on Macs that don't allow
    /// nesting. Such a run keeps the agent's own sandbox, which also limits writes to the worktree.
    /// </summary>
    public static bool StartsItsOwnSandbox(ProcessLaunch launch)
    {
        var args = launch.Arguments;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--sandbox" && args[i + 1] != "danger-full-access")
            {
                return true;
            }
            // A resumed Codex session (the original issue) sets the same mode as a setting.
            if (args[i] == "-c" && args[i + 1].StartsWith("sandbox_mode=", StringComparison.Ordinal) && !args[i + 1].Contains("danger-full-access", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A folder whose contents may be opened up for reading: not the home folder or anything above
    /// it, and not the work folder (other runs), anything above it or anything in it.
    /// </summary>
    private static bool SafeToAllow(string dir, string home, string workRoot) =>
        dir.Length > 1 && !Within(home, dir) && !Within(workRoot, dir) && !Within(dir, workRoot);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    private static bool Within(string path, string folder) =>
        string.Equals(path, folder, StringComparison.Ordinal) || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>The path with every symbolic link resolved (Seatbelt matches resolved paths: /var is /private/var).</summary>
    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('/');
        var current = "/";
        foreach (var part in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            for (var hops = 0; hops < 32; hops++)
            {
                FileSystemInfo info = new FileInfo(next);
                if (info.LinkTarget is not { } target)
                {
                    break;
                }
                next = Path.GetFullPath(target, current);
            }
            current = next;
        }
        return current;
    }
}
