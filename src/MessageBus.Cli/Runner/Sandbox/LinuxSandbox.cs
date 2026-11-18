namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>What a run sees at a path inside its Linux sandbox.</summary>
internal enum LinuxAccess
{
    /// <summary>The real files, read and write.</summary>
    ReadWrite,
    /// <summary>The real files, read only.</summary>
    ReadOnly,
    /// <summary>Not the real files: an empty folder (or an empty file) in their place.</summary>
    Hidden,
}

/// <summary>One mount of a run's bubblewrap sandbox, in the order bubblewrap applies them (a later one covers an earlier one).</summary>
/// <param name="Access">What the run gets at <paramref name="Target"/> and under it.</param>
/// <param name="Target">The path inside the sandbox (the same path as outside).</param>
/// <param name="IsFile">For <see cref="LinuxAccess.Hidden"/>: the path is a file, so it is covered by an empty file, not an empty folder.</param>
internal sealed record LinuxMount(LinuxAccess Access, string Target, bool IsFile = false)
{
    /// <summary>The bubblewrap options for this mount.</summary>
    public IEnumerable<string> Options() => Access switch
    {
        // Devices need a device bind; the PID namespace needs its own /proc.
        LinuxAccess.ReadWrite when Target == "/dev" => ["--dev-bind", Target, Target],
        LinuxAccess.ReadWrite when Target == "/proc" => ["--proc", Target],
        LinuxAccess.ReadWrite => ["--bind", Target, Target],
        LinuxAccess.ReadOnly => ["--ro-bind", Target, Target],
        _ when IsFile => ["--ro-bind", "/dev/null", Target],
        _ => ["--tmpfs", Target],
    };
}

/// <summary>
/// The Linux sandbox for runs (the original issue), through bubblewrap (<c>bwrap</c>, which Flatpak and Codex use the same way):
/// unprivileged user and mount namespaces, no admin rights and nothing changed on the machine. The runner relies on
/// it only after its start-up check (<see cref="SandboxProbe"/>) has watched it work here; a distribution that
/// doesn't allow unprivileged user namespaces (Ubuntu 24.04's AppArmor setting, for one) fails the check, and runs
/// get basic isolation with the reason said.
/// </summary>
/// <remarks>
/// <para>The rules match the macOS sandbox's (<see cref="MacSandboxProfile"/>): the system is read only; the home
/// folder is replaced by an empty one with only its hidden folders and files put back (writable: <c>~/.claude</c>,
/// <c>~/.codex</c>, <c>~/.config</c>, <c>~/.local</c>, <c>~/.npm</c>, <c>~/.mbus</c> …), but not credential stores
/// (<c>.ssh</c>, <c>.aws</c>, <c>.config/gh</c> …), and shell start-up files read only; the work folder is
/// replaced by an empty one (no other runs), with this run's directory put back writable; the shared temp folders
/// and <c>/dev</c> stay writable.</para>
/// <para>The run also gets its own PID namespace (<c>--unshare-pid</c>): when the agent exits, everything it left
/// running ends with it. <c>--new-session</c> keeps it from typing into the runner's terminal. Not
/// <c>--die-with-parent</c>: Linux ties that to the thread that started bubblewrap, and .NET's thread pool retires
/// idle threads, which would end a long run halfway. The network isn't limited here (the original issue).</para>
/// </remarks>
internal sealed class LinuxSandbox : IOsSandbox
{
    private readonly string _bwrap;
    private readonly string _home;
    private readonly string _workRoot;
    private readonly IReadOnlyList<string> _readable;
    private readonly IReadOnlyList<string> _private;

    internal LinuxSandbox(string bwrap, string home, string workRoot, IReadOnlyList<string> readable, IReadOnlyList<string> privatePaths)
    {
        _bwrap = bwrap;
        _home = home;
        _workRoot = workRoot;
        _readable = readable;
        _private = privatePaths;
        NestingCheck = [bwrap, "--ro-bind", "/", "/", "--dev", "/dev", "/bin/sh", "-c", ":"];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> NestingCheck { get; }

    /// <summary>Whether a run may start its own sandbox inside this one (Codex does, for its commands).</summary>
    public bool NestingAllowed { get; private set; }

    /// <summary>Start-up check steps that were skipped because they fail outside the sandbox too.</summary>
    public IReadOnlyList<string> Notes { get; private set; } = [];

    /// <summary>The sandbox, once its start-up check passed under it here; null, and why, when it doesn't.</summary>
    public static LinuxSandbox? TryCreate(RunIsolationOptions options, string bwrap, IProbeRunner runner, out string? problem)
    {
        Directory.CreateDirectory(options.WorkRoot);
        var home = MacSandbox.RealPath(options.HomeDirectory);
        var workRoot = MacSandbox.RealPath(options.WorkRoot);
        var readable = new List<string>();
        var mbusDir = MacSandbox.RealPath(AppContext.BaseDirectory);
        if (SafeToAllow(mbusDir, home, workRoot))
        {
            readable.Add(mbusDir); // mbus itself: the agent starts `mbus mcp` from here.
        }
        var privatePaths = options.PrivatePaths.Select(MacSandbox.RealPath).ToList();
        if (options.RunTools && options.SettingsDirectory is { } settings)
        {
            // the original issue, as on macOS: mbus's settings and the machine's key; the runner serves the run's mbus tools itself.
            privatePaths.Add(MacSandbox.RealPath(settings));
        }
        var sandbox = new LinuxSandbox(bwrap, home, workRoot, readable, privatePaths);
        var probe = Path.Combine(workRoot, $".isolation-probe-{Environment.ProcessId}");
        try
        {
            RunWorkspace.TryDelete(probe);
            var result = SandboxProbe.Run(sandbox, options, runner, probe);
            sandbox.NestingAllowed = result.NestingAllowed;
            sandbox.Notes = result.Notes;
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

    /// <inheritdoc />
    /// <remarks>
    /// The network isn't limited by this sandbox: a network namespace of its own would also cut the run off from its
    /// proxy on this computer (the original issue), so the proxy's allow-list holds only for programs that use it.
    /// </remarks>
    public bool ChecksNetwork => false;

    /// <inheritdoc />
    public void WriteProfile(string runRoot, bool limitNetwork = false)
    {
        // bubblewrap takes its rules as arguments (Mounts), so nothing is written.
    }

    /// <inheritdoc />
    public ProcessLaunch Wrap(ProcessLaunch launch, string runRoot) => Wrap(launch, runRoot, null);

    /// <summary><paramref name="launch"/> in the run's sandbox; <paramref name="sessionFolder"/>: a resumed session's own folder (the original issue), read only.</summary>
    public ProcessLaunch Wrap(ProcessLaunch launch, string runRoot, string? sessionFolder) => launch with
    {
        FileName = _bwrap,
        Arguments =
        [
            "--new-session", "--unshare-pid",
            .. Mounts(MacSandbox.RealPath(runRoot), launch.FileName, sessionFolder is null ? null : MacSandbox.RealPath(sessionFolder)).SelectMany(m => m.Options()),
            "--chdir", launch.WorkingDirectory,
            "--", launch.FileName, .. launch.Arguments,
        ],
        RawArguments = null,
    };

    /// <summary>
    /// The run's mounts, in order, for a run in <paramref name="runDir"/> (a resolved path) starting <paramref name="program"/>;
    /// <paramref name="sessionFolder"/> (resolved): a resumed session's own folder, read only (the original issue).
    /// </summary>
    internal IReadOnlyList<LinuxMount> Mounts(string runDir, string program, string? sessionFolder = null)
    {
        var mounts = new List<LinuxMount>
        {
            new(LinuxAccess.ReadOnly, "/"),
            new(LinuxAccess.ReadWrite, "/dev"),
            new(LinuxAccess.ReadWrite, "/proc"),
        };
        // The shared temp folders, as on macOS (installed tools and test runs keep programs there); the run's own
        // TMPDIR is in its run directory.
        foreach (var shared in (string[])["/tmp", "/var/tmp", "/dev/shm"])
        {
            if (Directory.Exists(shared))
            {
                mounts.Add(new(LinuxAccess.ReadWrite, shared));
            }
        }
        if (_home.Length > 1 && Directory.Exists(_home))
        {
            mounts.Add(new(LinuxAccess.Hidden, _home));
            var credentials = MacSandboxProfile.CredentialStores.ToHashSet(StringComparer.Ordinal);
            var startup = MacSandboxProfile.StartupFiles.ToHashSet(StringComparer.Ordinal);
            foreach (var entry in SafeEntries(_home).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.') && !credentials.Contains(name))
                {
                    mounts.Add(new(startup.Contains(name) ? LinuxAccess.ReadOnly : LinuxAccess.ReadWrite, entry));
                }
                else if (MacSandboxProfile.HomeToolFolders.Contains(name))
                {
                    mounts.Add(new(LinuxAccess.ReadWrite, entry));
                }
            }
            // Inside the folders put back: start-up files read only, credential stores hidden (.config/gh …).
            foreach (var relative in MacSandboxProfile.StartupFiles.Where(p => p.Contains('/')))
            {
                var path = Join(_home, relative);
                if (Exists(path))
                {
                    mounts.Add(new(LinuxAccess.ReadOnly, path));
                }
            }
            foreach (var relative in MacSandboxProfile.CredentialStores.Where(p => p.Contains('/')))
            {
                Hide(mounts, Join(_home, relative));
            }
            // The program's own folder and mbus's, when they are in the home folder's own folders (~/bin, ~/tools …).
            var execDir = Path.GetDirectoryName(MacSandbox.RealPath(program));
            foreach (var dir in _readable.Append(execDir).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (Under(dir, _home) && dir.Length > _home.TrimEnd('/').Length + 1 && dir[_home.TrimEnd('/').Length + 1] != '.'
                    && SafeToAllow(dir, _home, _workRoot) && Directory.Exists(dir))
                {
                    mounts.Add(new(LinuxAccess.ReadOnly, dir));
                }
            }
        }
        if (Directory.Exists(_workRoot))
        {
            mounts.Add(new(LinuxAccess.Hidden, _workRoot));
        }
        if (sessionFolder is not null && SafeToAllow(sessionFolder, _home, _workRoot) && Directory.Exists(sessionFolder))
        {
            // Never the home folder or above it (its credential stores), nor the work folder (other runs).
            mounts.Add(new(LinuxAccess.ReadOnly, sessionFolder));
        }
        foreach (var path in _private)
        {
            Hide(mounts, path);
        }
        mounts.Add(new(LinuxAccess.ReadWrite, runDir));
        return mounts;
    }

    /// <summary>What <paramref name="mounts"/> give at <paramref name="path"/> (resolved): the last mount at or above it decides.</summary>
    internal static LinuxAccess AccessTo(IReadOnlyList<LinuxMount> mounts, string path) =>
        mounts.LastOrDefault(m => Under(path, m.Target))?.Access ?? LinuxAccess.Hidden;

    private static void Hide(List<LinuxMount> mounts, string path)
    {
        if (Directory.Exists(path))
        {
            mounts.Add(new(LinuxAccess.Hidden, path));
        }
        else if (File.Exists(path))
        {
            mounts.Add(new(LinuxAccess.Hidden, path, IsFile: true));
        }
    }

    private static IEnumerable<string> SafeEntries(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string Join(string folder, string relative) => folder.TrimEnd('/') + "/" + relative;

    /// <summary>A folder that may be opened up for reading: not the home folder or above it, not the work folder, above it or in it.</summary>
    private static bool SafeToAllow(string dir, string home, string workRoot) =>
        dir.Length > 1 && !Under(home, dir) && !Under(workRoot, dir) && !Under(dir, workRoot);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    private static bool Under(string path, string folder) =>
        folder == "/" || string.Equals(path, folder, StringComparison.Ordinal) || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);
}
