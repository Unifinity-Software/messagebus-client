using MessageBus.Attachments.Client;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MessageBus.Cli.Runner;

/// <summary>
/// One bare mirror per repository in the work folder's <c>.mirrors</c> (the original issue), so a clone-mode run copies the repository
/// from this machine and fetches only what is new, instead of cloning it in full every time.
/// </summary>
/// <remarks>
/// <para>Only the runner itself reads and writes the mirrors, never a run: a run's working copy is copied out of the
/// mirror (<c>git clone --no-hardlinks</c>, so it shares no files with it) before the run's own git fetches from the real
/// remote with the run's credential, and the mirror takes in what that fetch brought (a local fetch from the working
/// copy) before the agent starts. So the mirror needs no credential of its own, and an agent can't change what later
/// runs get. On macOS the run sandbox already keeps runs out of the rest of the work folder.</para>
/// <para>Not a <c>git worktree</c>: a worktree keeps its index, refs and new objects in the shared repository, outside
/// the run's folder, where one run could change another's.</para>
/// </remarks>
public sealed class RepoMirrors(string root, TimeProvider? time = null)
{
    /// <summary>File in each mirror holding when a run last used it (the original issue), for <see cref="PruneAsync"/>.</summary>
    internal const string LastUsedFile = "mbus-last-used";

    /// <summary>File in each copy of code sent with tasks holding when each of its per-task refs came (the original issue), for <see cref="PruneAsync"/>.</summary>
    internal const string CodeRefTimesFile = "mbus-code-refs";

    /// <summary>How long a copy of code sent with tasks keeps a task's ref (<c>refs/mbus/&lt;commit&gt;</c>) after it came (the original issue).</summary>
    public static readonly TimeSpan CodeRefsKeptFor = TimeSpan.FromDays(14);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The folder holding the mirrors.</summary>
    public string Root { get; } = root;

    /// <summary>The mirrors of the runs in <paramref name="workRoot"/>.</summary>
    public static RepoMirrors For(string workRoot, TimeProvider? time = null) => new(Path.Combine(workRoot, ".mirrors"), time);

    /// <summary>
    /// Housekeeping (the original issue): removes each mirror no run has used for <paramref name="unusedFor"/>, and lets git pack the
    /// rest when they need it (<c>git gc --auto</c>, which does nothing until enough has piled up). Before that, a copy of
    /// code sent with tasks drops the per-task refs older than <see cref="CodeRefsKeptFor"/> (the original issue), and only those. A
    /// mirror a run is updating right now is left for next time. Never throws for a mirror it can't tidy.
    /// </summary>
    /// <param name="git">git, or null to only remove unused mirrors.</param>
    /// <param name="unusedFor">How long a mirror may go unused before it is removed.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>The mirrors removed (their folder names).</returns>
    public async Task<IReadOnlyList<string>> PruneAsync(string? git, TimeSpan unusedFor, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }
        var removed = new List<string>();
        foreach (var mirror in Directory.EnumerateDirectories(Root, "*.git"))
        {
            var gate = Locks.GetOrAdd(mirror, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            try
            {
                if (_time.GetUtcNow() - LastUsed(mirror) > unusedFor)
                {
                    RunWorkspace.TryDelete(mirror);
                    removed.Add(Path.GetFileName(mirror));
                }
                else if (git is not null)
                {
                    if (Path.GetFileName(mirror).StartsWith("code-", StringComparison.Ordinal))
                    {
                        await DropOldCodeRefsAsync(git, mirror, cancellationToken).ConfigureAwait(false);
                    }
                    await GitAsync(git, Root, ["-C", mirror, "gc", "--auto", "--quiet"], cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                gate.Release();
            }
        }
        return removed;
    }

    /// <summary>When a run last used <paramref name="mirror"/>: its <see cref="LastUsedFile"/>, else when the folder last changed.</summary>
    internal static DateTimeOffset LastUsed(string mirror)
    {
        var file = Path.Combine(mirror, LastUsedFile);
        if (File.Exists(file) && DateTimeOffset.TryParse(File.ReadAllText(file).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var at))
        {
            return at;
        }
        return Directory.GetLastWriteTimeUtc(mirror);
    }

    /// <summary>
    /// the original issue: deletes the copy's per-task refs that came more than <see cref="CodeRefsKeptFor"/> ago (a ref from before their
    /// times were recorded counts from now), never any other ref. Call holding the copy's lock.
    /// </summary>
    private async Task DropOldCodeRefsAsync(string git, string copy, CancellationToken cancellationToken)
    {
        var listed = await OutputAsync(git, ["-C", copy, "for-each-ref", "--format=%(refname)", CodeRef("")], cancellationToken).ConfigureAwait(false);
        if (listed is null)
        {
            return;
        }
        var now = _time.GetUtcNow();
        var received = ReadCodeRefTimes(copy);
        var kept = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var old = new List<string>();
        foreach (var name in listed.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!name.StartsWith(CodeRef(""), StringComparison.Ordinal))
            {
                continue;
            }
            var commit = name[CodeRef("").Length..];
            var at = received.TryGetValue(commit, out var came) ? came : now;
            if (now - at > CodeRefsKeptFor) old.Add(name);
            else kept[commit] = at;
        }
        if (old.Count > 0
            && await GitAsync(git, Root, ["-C", copy, "update-ref", "--stdin"], cancellationToken, string.Concat(old.Select(r => $"delete {r}\n"))).ConfigureAwait(false) is not null)
        {
            return; // all left as they were, for the next pass
        }
        WriteCodeRefTimes(copy, kept);
    }

    /// <summary>Records that the bundle's branches came into <paramref name="copy"/> (as <see cref="CodeRef"/>s) at <paramref name="at"/>.</summary>
    private static void MarkCodeRefsReceived(string copy, Attachments.Client.BundleHeader header, DateTimeOffset at)
    {
        try
        {
            var times = ReadCodeRefTimes(copy);
            foreach (var commit in header.Refs.Where(r => r.Key.StartsWith("refs/heads/", StringComparison.Ordinal)).Select(r => r.Value))
            {
                times[commit] = at;
            }
            WriteCodeRefTimes(copy, times);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth failing the task over: the ref then counts from the next housekeeping pass.
        }
    }

    private static Dictionary<string, DateTimeOffset> ReadCodeRefTimes(string copy)
    {
        var times = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var file = Path.Combine(copy, CodeRefTimesFile);
        if (!File.Exists(file))
        {
            return times;
        }
        foreach (var line in File.ReadAllLines(file))
        {
            if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var commit, var text]
                && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
            {
                times[commit] = at;
            }
        }
        return times;
    }

    private static void WriteCodeRefTimes(string copy, IReadOnlyDictionary<string, DateTimeOffset> times) =>
        File.WriteAllLines(Path.Combine(copy, CodeRefTimesFile),
            times.Select(t => t.Key + " " + t.Value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Records that a run used <paramref name="mirror"/> at <paramref name="at"/>.</summary>
    internal static void MarkUsed(string mirror, DateTimeOffset at) =>
        File.WriteAllText(Path.Combine(mirror, LastUsedFile), at.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The mirror's folder for <paramref name="url"/>: readable host and path, then a hash of the whole URL.</summary>
    public string PathFor(string url)
    {
        var readable = new string([.. url.Replace("://", "-", StringComparison.Ordinal).Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-')]).Trim('-', '.');
        if (readable.EndsWith("-git", StringComparison.OrdinalIgnoreCase)) readable = readable[..^4];
        if (readable.Length > 60) readable = readable[^60..].TrimStart('-', '.');
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())))[..12];
        return Path.Combine(Root, $"{readable}-{hash}.git");
    }

    /// <summary>Whether there is a mirror for <paramref name="url"/> to start from.</summary>
    public bool Has(string url) => File.Exists(Path.Combine(PathFor(url), "HEAD"));

    /// <summary>
    /// Takes in what a run's fresh working copy fetched from <paramref name="url"/> (its remote-tracking branches and
    /// tags, and which branch is the default), making the mirror the first time. Call before the agent starts.
    /// Never throws: a mirror that can't be updated is removed and made again by a later run.
    /// </summary>
    public async Task UpdateFromAsync(string git, string url, string workTree, CancellationToken cancellationToken)
    {
        var mirror = PathFor(url);
        var gate = Locks.GetOrAdd(mirror, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Root);
            var error = File.Exists(Path.Combine(mirror, "HEAD")) ? null : await GitAsync(git, Root, ["init", "--bare", "--quiet", mirror], cancellationToken).ConfigureAwait(false);
            error ??= await GitAsync(git, Root,
                ["-C", mirror, "fetch", RunnerGit.LocalUploadPack, "--quiet", "--prune", "--no-write-fetch-head", workTree,
                 "+refs/remotes/origin/*:refs/heads/*", "^refs/remotes/origin/HEAD", "+refs/tags/*:refs/tags/*"], cancellationToken).ConfigureAwait(false);
            if (error is null && DefaultBranch(workTree) is { } branch)
            {
                error = await GitAsync(git, Root, ["-C", mirror, "symbolic-ref", "HEAD", "refs/heads/" + branch], cancellationToken).ConfigureAwait(false);
            }
            if (error is not null)
            {
                RunWorkspace.TryDelete(mirror);
            }
            else
            {
                MarkUsed(mirror, _time.GetUtcNow());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunWorkspace.TryDelete(mirror);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---- code sent with tasks (the original issue) ----
    //
    // A bundle-mode run's code goes into a bare copy per workspace and repository (named by the repository's first commit),
    // next to the clone-mode mirrors and pruned with them. Each commit a task was sent is kept as refs/mbus/<commit>, so it
    // stays reachable, and a task's working copy fetches only its own commit: never another task's or sender's branches.
    // Housekeeping drops each of those refs CodeRefsKeptFor after it came (the original issue; the times are in CodeRefTimesFile).

    /// <summary>The copy of the repository whose first commit is <paramref name="rootCommit"/>, for <paramref name="workspaceId"/>'s tasks.</summary>
    public string CodePathFor(string workspaceId, string rootCommit) => Path.Combine(Root, $"{CodePrefix(workspaceId)}{rootCommit}.git");

    private static string CodePrefix(string workspaceId) =>
        "code-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceId)))[..12] + "-";

    /// <summary>The ref a copy keeps a task's commit under.</summary>
    internal static string CodeRef(string commit) => "refs/mbus/" + commit;

    /// <summary>
    /// The workspace's copy that already has every commit <paramref name="bundle"/> starts after (most recently used first),
    /// or null: then the run needs the whole history from the sender.
    /// </summary>
    public async Task<string?> FindCodeAsync(string git, string workspaceId, string bundle, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Root))
        {
            return null;
        }
        var header = GitBundles.ReadHeader(bundle);
        foreach (var copy in Directory.EnumerateDirectories(Root, CodePrefix(workspaceId) + "*.git").OrderByDescending(LastUsed))
        {
            if ((await GitBundles.MissingPrerequisitesAsync(header, BundleGit(git, copy), cancellationToken).ConfigureAwait(false)).Count == 0)
            {
                return copy;
            }
        }
        return null;
    }

    /// <summary>
    /// Takes in a bundle of a branch's whole history: into the workspace's copy of that repository, made the first time. Returns
    /// the copy, or null with why it couldn't (the run then can't use a copy).
    /// </summary>
    public async Task<(string? Copy, string? Error)> TakeInHistoryAsync(string git, string workspaceId, string bundle, Attachments.Client.BundleHeader header,
        CancellationToken cancellationToken)
    {
        if (header.Refs.Where(r => r.Key.StartsWith("refs/heads/", StringComparison.Ordinal)).Select(r => r.Value).FirstOrDefault() is not { } head)
        {
            return (null, "The code sent with the task has no branch.");
        }
        Directory.CreateDirectory(Root);
        // Fetched into a new copy first: only its commits say which repository it is.
        var incoming = Path.Combine(Root, "incoming-" + Guid.NewGuid().ToString("N")[..12] + ".git");
        try
        {
            var error = await GitAsync(git, Root, ["init", "--bare", "--quiet", incoming], cancellationToken).ConfigureAwait(false)
                        ?? await ApplyBundleAsync(git, incoming, bundle, header, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return (null, RunnerGit.DescribePath(error, incoming, "this machine's working copy"));
            }
            var root = (await OutputAsync(git, ["-C", incoming, "rev-list", "--first-parent", "--max-parents=0", head], cancellationToken).ConfigureAwait(false))
                ?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (root is null || root.Length < 40 || !root.All(char.IsAsciiHexDigit))
            {
                return (null, "Couldn't read the first commit of the code sent with the task.");
            }
            var copy = CodePathFor(workspaceId, root);
            var gate = Locks.GetOrAdd(copy, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(Path.Combine(copy, "HEAD")))
                {
                    error = await GitAsync(git, Root, ["-C", copy, "fetch", RunnerGit.LocalUploadPack, "--quiet", "--no-tags", "--no-write-fetch-head", incoming, "+refs/mbus/*:refs/mbus/*"], cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Directory.Move(incoming, copy);
                }
                if (error is not null)
                {
                    error = RunnerGit.DescribePath(error, copy, "this machine's repository copy");
                    return (null, RunnerGit.DescribePath(error, incoming, "this machine's working copy"));
                }
                MarkCodeRefsReceived(copy, header, _time.GetUtcNow());
                MarkUsed(copy, _time.GetUtcNow());
                return (copy, null);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "Couldn't access this machine's copy of the repository or the task's bundle.");
        }
        finally
        {
            RunWorkspace.TryDelete(incoming);
        }
    }

    /// <summary>
    /// Takes <paramref name="bundle"/> (whose earlier commits <paramref name="copy"/> has) into the copy, then makes
    /// <paramref name="workTree"/> a new repository holding only <paramref name="head"/> and its history, as
    /// <c>refs/remotes/origin/{branch}</c> with <c>origin</c> the bundle. Null when ready (check it out next), else why not.
    /// </summary>
    public async Task<string?> CheckOutCodeAsync(string git, string copy, string bundle, Attachments.Client.BundleHeader header, string branch, string head,
        string workTree, CancellationToken cancellationToken)
    {
        var gate = Locks.GetOrAdd(copy, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path.Combine(copy, "HEAD")))
            {
                return "The runner's copy of the repository is gone.";
            }
            var file = Path.GetFullPath(bundle);
            // A new repository fetching one commit from the copy: its own objects, no shared files, nothing else of the copy's.
            var error = await ApplyBundleAsync(git, copy, file, header, cancellationToken).ConfigureAwait(false)
                        ?? await GitAsync(git, Root, ["init", "--quiet", workTree], cancellationToken).ConfigureAwait(false)
                        ?? await GitAsync(git, Root, ["-C", workTree, "remote", "add", "origin", file], cancellationToken).ConfigureAwait(false)
                        ?? await GitAsync(git, Root, ["-C", workTree, "fetch", RunnerGit.LocalUploadPack, "--quiet", "--no-tags", "--no-write-fetch-head", Path.GetFullPath(copy),
                            $"+{CodeRef(head)}:refs/remotes/origin/{branch}"], cancellationToken).ConfigureAwait(false);
            if (error is null)
            {
                MarkCodeRefsReceived(copy, header, _time.GetUtcNow());
                MarkUsed(copy, _time.GetUtcNow());
            }
            if (error is null) return null;
            error = RunnerGit.DescribePath(error, workTree, "this machine's working copy");
            error = RunnerGit.DescribePath(error, copy, "this machine's repository copy");
            return RunnerGit.DescribePath(error, file, "the task's bundle");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Couldn't access this machine's copy of the repository or the task's bundle.";
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string?> ApplyBundleAsync(string git, string repository, string bundle, BundleHeader header, CancellationToken ct)
    {
        try
        {
            await GitBundles.ApplyAsync(bundle, "this machine's working copy", [.. RefSpecs(header)], BundleGit(git, repository, bundle), writeFetchHead: false, ct).ConfigureAwait(false);
            return null;
        }
        catch (GitException ex)
        {
            return ex.Message;
        }
    }

    private static Func<IReadOnlyList<string>, CancellationToken, Task<string>> BundleGit(string git, string repository, string? bundle = null) =>
        async (args, ct) =>
        {
            var (output, error) = await RunGitAsync(git, Path.GetTempPath(), ["-C", repository, .. args], ct, captureOutput: true).ConfigureAwait(false);
            if (error is null) return output;
            error = RunnerGit.DescribePath(error, repository, "this machine's working copy");
            if (bundle is not null)
                error = RunnerGit.DescribePath(error, Path.GetFullPath(bundle), "the task's bundle");
            throw new GitException(error, rawOutput: true);
        };

    /// <summary>Each branch in the bundle kept under its commit.</summary>
    private static IEnumerable<string> RefSpecs(Attachments.Client.BundleHeader header) =>
        header.Refs.Where(r => r.Key.StartsWith("refs/heads/", StringComparison.Ordinal)).Select(r => $"+{r.Key}:{CodeRef(r.Value)}").Distinct();

    private static async Task<string?> OutputAsync(string git, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var (output, error) = await RunGitAsync(git, Path.GetTempPath(), args, cancellationToken, captureOutput: true).ConfigureAwait(false);
        return error is null ? output : null;
    }

    /// <summary>The remote's default branch as a clone recorded it (<c>refs/remotes/origin/HEAD</c>), or null.</summary>
    internal static string? DefaultBranch(string workTree)
    {
        const string prefix = "ref: refs/remotes/origin/";
        var file = Path.Combine(workTree, ".git", "refs", "remotes", "origin", "HEAD");
        var text = File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        return text is not null && text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length ? text[prefix.Length..] : null;
    }

    /// <summary>The branch a clone's HEAD names (<c>ref: refs/heads/…</c>), or null when it is detached.</summary>
    internal static string? CurrentBranch(string workTree)
    {
        const string prefix = "ref: refs/heads/";
        var file = Path.Combine(workTree, ".git", "HEAD");
        var text = File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        return text is not null && text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length ? text[prefix.Length..] : null;
    }

    private static async Task<string?> GitAsync(string git, string directory, IReadOnlyList<string> args, CancellationToken cancellationToken, string? input = null) =>
        (await RunGitAsync(git, directory, args, cancellationToken, input: input).ConfigureAwait(false)).Error;

    private static async Task<(string Output, string? Error)> RunGitAsync(string git, string directory,
        IReadOnlyList<string> args, CancellationToken cancellationToken, bool captureOutput = false, string? input = null)
    {
        var lines = new List<string>();
        using var process = AgentProcess.Start(new ProcessLaunch
        {
            FileName = git,
            // Mirror object paths can exceed MAX_PATH on Windows. No inherited hooks or fsmonitor outside the sandbox.
            Arguments = RunnerGit.Arguments(args),
            WorkingDirectory = directory,
            Environment = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never" },
            StandardInput = input,
        }, captureOutput ? line =>
        {
            lock (lines) lines.Add(line);
        } : null, tailLines: 20);
        using var kill = cancellationToken.Register(process.Kill);
        var exit = await process.WaitForExitAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (lines)
        {
            return exit == 0 ? (string.Join('\n', lines), null)
                : ("", $"git {args[0]} failed (exit {exit}): {string.Join(" ", process.Tail()).Trim()}");
        }
    }
}
