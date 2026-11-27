namespace MessageBus.Cli.Runner;

/// <summary>
/// Commits reachable before the agent starts. A successful settle alone is not proof that code was
/// returned: the local settle record intentionally omits uploaded attachments. Keep new commits
/// locally unless their return can be confirmed, rather than deleting the only copy.
/// </summary>
internal sealed class RunCommits
{
    private readonly string? _git;
    private readonly RunWorkspace _workspace;
    private HashSet<string>? _before;

    public RunCommits(string? git, RunWorkspace workspace)
    {
        _git = git;
        _workspace = workspace;
    }

    private bool HasRepository => Directory.Exists(Path.Combine(_workspace.WorkTree, ".git")) || File.Exists(Path.Combine(_workspace.WorkTree, ".git"));

    // Packaging runs as the runner after the agent's sandbox has ended. Never follow an
    // agent's gitdir, alternates, or links into another repository on this machine.
    internal static bool IsLocalRepository(RunWorkspace workspace)
    {
        var git = Path.Combine(workspace.WorkTree, ".git");
        if (!Directory.Exists(git) || new DirectoryInfo(workspace.Root).LinkTarget is not null
            || new DirectoryInfo(workspace.WorkTree).LinkTarget is not null
            || File.Exists(Path.Combine(git, "commondir")) || File.Exists(Path.Combine(git, "objects", "info", "alternates"))) return false;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(git));
        while (pending.TryPop(out var directory))
        {
            if (directory.LinkTarget is not null) return false;
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                if (item.LinkTarget is not null) return false;
                if (item is DirectoryInfo child) pending.Push(child);
            }
        }
        return true;
    }

    private bool LocalRepository => _workspace.LocalRepositoryAfterExit ?? IsLocalRepository(_workspace);

    internal static string[] GitArguments(RunWorkspace workspace, params string[] args) =>
        RunnerGit.Arguments(["--git-dir=" + Path.Combine(workspace.WorkTree, ".git"), "--work-tree=" + workspace.WorkTree,
            "-c", "protocol.allow=never", "-c", "core.sshCommand=", .. args]);

    internal static Dictionary<string, string?> GitEnvironment(RunWorkspace workspace) => new()
    {
        ["GIT_CEILING_DIRECTORIES"] = workspace.Root,
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_NO_LAZY_FETCH"] = "1",
        // A protocol-specific repo setting can override protocol.allow. This whitelist cannot.
        ["GIT_ALLOW_PROTOCOL"] = "",
    };

    // Read only this config, without includes. Fail closed on malformed or unsupported configs.
    internal static async Task<bool> HasSafeConfigAsync(string git, RunWorkspace workspace)
    {
        var start = new System.Diagnostics.ProcessStartInfo(git)
        {
            WorkingDirectory = workspace.WorkTree, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var arg in GitArguments(workspace, "config", "--no-includes", "--null", "--file", Path.Combine(workspace.WorkTree, ".git", "config"), "--list")) start.ArgumentList.Add(arg);
        foreach (var entry in GitEnvironment(workspace)) start.Environment[entry.Key] = entry.Value;
        MessageBus.Cli.Agents.CodexAuthentication.RemoveKeys(start.Environment);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        await error.ConfigureAwait(false);
        if (process.ExitCode != 0) return false;
        foreach (var entry in (await output.ConfigureAwait(false)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // --null separates entries with NUL and keys from values with LF. Refuse
            // multiline names/values rather than let a remote subsection hide a promisor key.
            var separator = entry.IndexOf('\n');
            if (separator >= 0 && entry.IndexOf('\n', separator + 1) >= 0) return false;
            var key = (separator < 0 ? entry : entry[..separator]).ToLowerInvariant();
            if (key.StartsWith("extensions.", StringComparison.Ordinal)
                || key.StartsWith("include.", StringComparison.Ordinal)
                || key.StartsWith("includeif.", StringComparison.Ordinal)
                || (key.StartsWith("remote.", StringComparison.Ordinal) && (key.EndsWith(".promisor", StringComparison.Ordinal) || entry.EndsWith(".promisor", StringComparison.OrdinalIgnoreCase)))
                || (key == "core.repositoryformatversion" && (separator < 0 || entry[(separator + 1)..].Trim() != "0"))) return false;
        }
        return true;
    }

    public void InspectAfterExit() => _workspace.LocalRepositoryAfterExit = IsLocalRepository(_workspace);

    public string? BaseCommit { get; private set; }
    public async Task CaptureAsync()
    {
        _before = await ReadAsync().ConfigureAwait(false);
        BaseCommit = await RevisionAsync("rev-parse", "--verify", "HEAD").ConfigureAwait(false);
    }

    public Task<string?> BranchAsync() => RevisionAsync("symbolic-ref", "--quiet", "--short", "HEAD");

    private async Task<string?> RevisionAsync(params string[] args)
    {
        if (_git is null || !HasRepository || !LocalRepository || !await HasSafeConfigAsync(_git, _workspace).ConfigureAwait(false)) return null;
        using var process = AgentProcess.Start(new ProcessLaunch { FileName = _git, WorkingDirectory = _workspace.WorkTree, Arguments = GitArguments(_workspace, args), Environment = GitEnvironment(_workspace) }, tailLines: 2);
        return await process.WaitForExitAsync().ConfigureAwait(false) == 0 ? process.Tail().FirstOrDefault()?.Trim() : null;
    }

    public async Task<bool> MustKeepAsync()
    {
        // Empty checkouts may become repositories during the task. Resumed sessions are never ours to delete.
        if (_workspace.SessionFolder is not null || !HasRepository) return false;
        if (!LocalRepository || _git is null || !await HasSafeConfigAsync(_git, _workspace).ConfigureAwait(false)) return true;
        var after = await ReadAsync().ConfigureAwait(false);
        return after is null || _before is null || after.Except(_before).Any();
    }

    public async Task<bool> HasOtherNewAsync(string branch)
    {
        var other = await ReadAsync(branch).ConfigureAwait(false);
        return other is null || _before is null || other.Except(_before).Any();
    }

    private async Task<HashSet<string>?> ReadAsync(string? excludeBranch = null)
    {
        if (!HasRepository) return [];
        if (_git is null || !LocalRepository || !await HasSafeConfigAsync(_git, _workspace).ConfigureAwait(false)) return null;
        try
        {
            // Read objects only; do not run repository hooks, fsmonitor, or the user's pager.
            var commits = new HashSet<string>(StringComparer.Ordinal);
            using var process = AgentProcess.Start(new ProcessLaunch
            {
                FileName = _git,
                WorkingDirectory = _workspace.WorkTree,
                Arguments = GitArguments(_workspace, ["--no-pager", "-c", "core.fsmonitor=false", "rev-list", "--branches", "--tags", "HEAD", .. excludeBranch is null ? Array.Empty<string>() : new[] { "^refs/heads/" + excludeBranch }]),
                Environment = GitEnvironment(_workspace),
            }, line => { if (line.Length is 40 or 64 && line.All(char.IsAsciiHexDigit)) commits.Add(line); }, tailLines: 0);
            var exit = await process.WaitForExitAsync().ConfigureAwait(false);
            return exit == 0 ? commits : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
