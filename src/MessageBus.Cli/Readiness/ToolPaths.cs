using MessageBus.Cli.Runner;

namespace MessageBus.Cli.Readiness;

/// <summary>
/// Folders where people install toolchains for themselves (no admin rights): the .NET install
/// script's <c>~/.dotnet</c>, <c>~/.local/bin</c>, a Node.js tarball in <c>~/.local/node/bin</c>,
/// Homebrew, and so on. A runner started over SSH or by launchd has a short PATH without them, so
/// the runner adds the ones that exist to every run's PATH (after what is already there), and the
/// machine scan looks for tools in them too (the original issue).
/// </summary>
public static class ToolPaths
{
    /// <summary>The folders checked, whether or not they exist, in the order they are added.</summary>
    /// <param name="home">The user's home folder.</param>
    /// <param name="env">Environment lookup (for <c>APPDATA</c> and <c>ProgramFiles</c> on Windows).</param>
    public static IReadOnlyList<string> Candidates(string home, Func<string, string?> env)
    {
        var dirs = new List<string>
        {
            DotnetRoot(home),
            Path.Combine(home, ".dotnet", "tools"),
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".local", "node", "bin"),
            Path.Combine(home, ".npm-global", "bin"),
            Path.Combine(home, ".volta", "bin"),
            Path.Combine(home, ".bun", "bin"),
            Path.Combine(home, ".cargo", "bin"),
            Path.Combine(home, "go", "bin"),
        };
        if (OperatingSystem.IsMacOS())
        {
            dirs.AddRange(["/opt/homebrew/bin", "/opt/homebrew/sbin", "/usr/local/bin"]);
            dirs.Add("/Applications/Blender.app/Contents/MacOS");
        }
        else if (OperatingSystem.IsLinux())
        {
            dirs.AddRange(["/home/linuxbrew/.linuxbrew/bin", "/usr/local/bin"]);
        }
        else if (OperatingSystem.IsWindows())
        {
            if (env("APPDATA") is { Length: > 0 } appData)
            {
                dirs.Add(Path.Combine(appData, "npm"));
            }
            dirs.AddRange(WindowsBlenderDirectories(env));
        }
        return dirs;
    }

    internal static IReadOnlyList<string> WindowsBlenderDirectories(Func<string, string?> env)
    {
        if (env("ProgramFiles") is not { Length: > 0 } programFiles)
        {
            return [];
        }

        const string prefix = "Blender ";
        try
        {
            var root = Path.Combine(programFiles, "Blender Foundation");
            return [.. Directory.EnumerateDirectories(root, prefix + "*")
                .Select(dir => (Directory: dir, Name: Path.GetFileName(dir)))
                .Where(entry => entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(entry.Directory, "blender.exe")))
                .Select(entry => (entry.Directory, Version: Version.TryParse(entry.Name.AsSpan(prefix.Length), out var version) ? version : null))
                .Where(entry => entry.Version is not null)
                .OrderByDescending(entry => entry.Version)
                .Select(entry => entry.Directory)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The candidates that exist on this machine.</summary>
    public static IReadOnlyList<string> Existing(string home, Func<string, string?> env) =>
        [.. Candidates(home, env).Where(Directory.Exists)];

    /// <summary>
    /// What a run's environment gets: <c>PATH</c> with the existing folders that aren't on it yet added at
    /// the end, and <c>DOTNET_ROOT</c> when the <c>dotnet</c> a run finds is the per-user one in
    /// <c>~/.dotnet</c> (it can't find its SDKs from there otherwise). Empty when nothing needs adding.
    /// </summary>
    /// <param name="home">The user's home folder.</param>
    /// <param name="env">The runner's own environment.</param>
    public static IReadOnlyDictionary<string, string> RunVariables(string home, Func<string, string?> env)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (env("PATH") is not { Length: > 0 } path)
        {
            // Nothing to add to (a host that doesn't pass its environment on); leave PATH as inherited.
            return result;
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var onPath = new HashSet<string>(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => Path.TrimEndingDirectorySeparator(d.Trim('"'))), comparison);
        var added = Existing(home, env).Where(d => !onPath.Contains(Path.TrimEndingDirectorySeparator(d))).ToList();
        if (added.Count > 0)
        {
            result["PATH"] = string.Join(Path.PathSeparator, [path.TrimEnd(Path.PathSeparator), .. added]);
        }

        var dotnetRoot = DotnetRoot(home);
        if (env("DOTNET_ROOT") is not { Length: > 0 } && File.Exists(Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")))
        {
            var found = CommandLocator.Find("dotnet", name => result.TryGetValue(name, out var value) ? value : env(name), []);
            if (found is not null && comparison.Equals(Path.GetDirectoryName(found), Path.TrimEndingDirectorySeparator(dotnetRoot)))
            {
                result["DOTNET_ROOT"] = dotnetRoot;
            }
        }
        return result;
    }

    private static string DotnetRoot(string home) => Path.Combine(home, ".dotnet");
}

/// <summary>Gives every process of a run the per-user tool folders (<see cref="ToolPaths.RunVariables"/>), then the run's own sandbox.</summary>
/// <param name="inner">The run isolation (the original issue).</param>
/// <param name="variables">From <see cref="ToolPaths.RunVariables"/>.</param>
public sealed class ToolPathSandbox(IRunSandbox inner, IReadOnlyDictionary<string, string> variables,
    Func<IReadOnlyDictionary<string, string>>? refresh = null) : IRunSandbox
{
    string? IRunSandbox.AgentRefusal(Contracts.Machines.AgentKind kind) => inner.AgentRefusal(kind);

    bool IRunSandbox.UsesPrivateHome => inner.UsesPrivateHome;

    string? IRunSandbox.QuarantineProblem => inner.QuarantineProblem;

    bool IRunSandbox.EnforcesAgentFilePolicy(RunWorkspace workspace, Contracts.Roles.PermissionProfile profile) =>
        inner.EnforcesAgentFilePolicy(workspace, profile);

    /// <inheritdoc />
    public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, Contracts.Roles.PermissionProfile profile)
    {
        var current = refresh?.Invoke() ?? variables;
        if (current.Count == 0)
        {
            return inner.Wrap(launch, workspace, profile);
        }
        var environment = new Dictionary<string, string?>(launch.Environment, StringComparer.Ordinal);
        foreach (var (name, value) in current)
        {
            // A launch that sets one itself keeps its own.
            environment.TryAdd(name, value);
        }
        return inner.Wrap(launch with { Environment = environment }, workspace, profile);
    }

    /// <inheritdoc />
    public Task PrepareAsync(RunWorkspace workspace, Contracts.Runner.TaskDelivery delivery, Contracts.Roles.PermissionProfile profile, CancellationToken cancellationToken) =>
        inner.PrepareAsync(workspace, delivery, profile, cancellationToken);

    /// <inheritdoc />
    public void Started(RunWorkspace workspace, int processId) => inner.Started(workspace, processId);

    /// <inheritdoc />
    public void Ended(RunWorkspace workspace) => inner.Ended(workspace);

    /// <inheritdoc />
    public Task EndedAsync(RunWorkspace workspace) => inner.EndedAsync(workspace);

    /// <inheritdoc />
    public Contracts.Machines.RunIsolationReport? Report => inner.Report;

    /// <inheritdoc />
    public bool ConfinesAgents => inner.ConfinesAgents;

    /// <inheritdoc />
    public bool IsolationEnabled => inner.IsolationEnabled;
}
