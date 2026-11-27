using MessageBus.Attachments.Client;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>
/// Downloads and decrypts a delivery's git bundle to a local file.
/// </summary>
public interface IBundleSource
{
    /// <summary>Reads and decrypts the latest result reply on an earlier step's thread, or null when unavailable.</summary>
    Task<ReceivedMessage?> ReadStepResultAsync(TaskDelivery delivery, StepInput input, CancellationToken cancellationToken) =>
        Task.FromResult<ReceivedMessage?>(null);

    /// <summary>Downloads and decrypts a result attachment to the supplied path, or returns null when unavailable.</summary>
    Task<string?> FetchStepAttachmentAsync(AttachmentRef attachment, string path, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>A decrypted copy of <paramref name="bundle"/> under <paramref name="runDirectory"/>, or null when this source can't get it.</summary>
    Task<string?> FetchAsync(TaskDelivery delivery, AttachmentRef bundle, string runDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// the original issue: <paramref name="bundle"/> starts after commits this machine doesn't have. Asks the task's sender for its branch's
    /// whole history (<paramref name="ask"/> says so in the run's progress) and waits up to <paramref name="wait"/>: a
    /// decrypted copy under <paramref name="runDirectory"/>, or null when it didn't come.
    /// </summary>
    Task<string?> RequestHistoryAsync(TaskDelivery delivery, AttachmentRef bundle, string runDirectory, Func<ProgressRequest, CancellationToken, Task> ask,
        TimeSpan wait, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

/// <summary>
/// One run's directory under the work root: <c>work/</c> is the agent's worktree, next to it the
/// prompt, the sealed delivery, the agent's settle record and the log. Nothing outside it is shared
/// between runs, so two runs never see each other's files.
/// <para>
/// the original issue: the folder is deleted when the run ends (only the log stays, when the role keeps local logs, until housekeeping
/// deletes it once it is old: the original issue). While the run is
/// in progress it holds <see cref="LockFileName"/> open, so housekeeping (<see cref="Sweep"/>), in this runner or another
/// one sharing the work folder, leaves it alone. A run whose work never reached the service is held for
/// <see cref="HoldFor"/> (<see cref="HoldFileName"/>), then swept like any other.
/// </para>
/// </summary>
public sealed partial class RunWorkspace : IDisposable
{
    /// <summary>Held open by the run while it is in progress.</summary>
    public const string LockFileName = "run.lock";

    /// <summary>When a held run's folder may be deleted (<see cref="Hold"/>).</summary>
    public const string HoldFileName = "keep-until";

    /// <summary>How long the folder of a run whose work never reached the service is kept.</summary>
    public static readonly TimeSpan HoldFor = TimeSpan.FromHours(24);

    private const string LogFileName = "agent.log";

    /// <summary>This process's runs in progress (a second guard beside the lock file, which a run's agent could remove).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> InProgress =
        new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    private FileStream? _lock;

    internal Action<string>? GitLog { get; set; }
    internal bool RepositoryRefused { get; private set; }
    internal string? RepositoryCredentialNote { get; set; }
    // Prepared by isolation before checkout, also used by runner-owned Git outside an OS wrapper.
    internal IReadOnlyDictionary<string, string?> GitEnvironment { get; set; } = new Dictionary<string, string?>();
    internal Uri? LocalModelEndpoint { get; set; }
    internal string? PrivateSocketDirectory { get; set; }

    internal bool SandboxCleanupIncomplete { get; set; }

    internal PreparedStepInputs? PreparedInputs { get; set; }

    private const string WorkingCopyDescription = "this machine's working copy";

    internal async Task<string?> ImportStepBranchAsync(string git, TaskDelivery delivery, string file, BundleHeader header, string branch,
        Func<ProcessLaunch, ProcessLaunch> wrap, CancellationToken ct)
    {
        Directory.CreateDirectory(WorkTree);
        var repoRef = RunnerGit.RepositoryRef(delivery.Envelope.Refs);
        var repository = WorkingCopyDescription;
        string? repositoryError = null;
        var initialized = Path.Exists(Path.Combine(WorkTree, ".git"));
        // Only replace an empty checkout. Existing branches may belong to earlier steps.
        if (repoRef is not null && header.Prerequisites.Count > 0
            && (!initialized || await GitAsync(git, ["-C", WorkTree, "rev-parse", "--verify", "HEAD"], wrap, ct).ConfigureAwait(false) is not null
                && (await GitBundles.MissingPrerequisitesAsync(header, BundleGit(git, file, wrap), ct).ConfigureAwait(false)).Count > 0))
        {
            repositoryError = await CheckOutRepositoryAsync(git, repoRef, wrap, ct).ConfigureAwait(false);
            initialized = repositoryError is null;
            if (initialized)
                repository = $"repository '{RepositoryUrl(repoRef)}'";
            else
                TryDelete(WorkTree);
        }
        if (!initialized && await GitAsync(git, ["init", "--quiet", WorkTree], wrap, ct).ConfigureAwait(false) is { } initError)
            return initError;

        var reference = "refs/remotes/mbus-input/" + Guid.NewGuid().ToString("N");
        var error = await GitAsync(git, ["-C", WorkTree, "check-ref-format", "refs/heads/" + branch], wrap, ct).ConfigureAwait(false)
            ?? await ApplyBundleAsync(git, file, repository, branch, reference, wrap, ct).ConfigureAwait(false)
            ?? await GitAsync(git, ["-C", WorkTree, "checkout", "--quiet", "-B", branch, reference], wrap, ct).ConfigureAwait(false);
        return error is null || repositoryError is null ? error : repositoryError + " " + error;
    }

    private async Task<string?> ApplyBundleAsync(string git, string file, string repository, string branch, string destination,
        Func<ProcessLaunch, ProcessLaunch> wrap, CancellationToken ct)
    {
        try
        {
            await GitBundles.ApplyAsync(file, repository, [$"+refs/heads/{branch}:{destination}"],
                BundleGit(git, file, wrap), writeFetchHead: true, ct).ConfigureAwait(false);
            return null;
        }
        catch (GitException ex)
        {
            return ex.Message;
        }
        catch (IOException)
        {
            return "Couldn't read the task's bundle.";
        }
    }

    private Func<IReadOnlyList<string>, CancellationToken, Task<string>> BundleGit(string git, string file,
        Func<ProcessLaunch, ProcessLaunch> wrap) => async (args, ct) =>
    {
        var transport = args.FirstOrDefault() == "fetch" && args.Contains("--")
            && args.SkipWhile(a => a != "--").Skip(1).FirstOrDefault() != Path.GetFullPath(file)
            ? RemoteGit(wrap) : wrap;
        var (output, error) = await RunGitAsync(git, ["-C", WorkTree, .. args], transport, ct).ConfigureAwait(false);
        if (error is not null)
            throw new GitException(RunnerGit.DescribePath(error, Path.GetFullPath(file), "the task's bundle"), rawOutput: true);
        return output;
    };

    // Chosen from the trusted adapter, before checkout, never from task content.
    internal Contracts.Machines.AgentKind? AgentKind { get; set; }

    private RunWorkspace(string root, string? sessionFolder = null)
    {
        Root = Path.GetFullPath(root);
        SessionFolder = sessionFolder;
    }

    /// <summary>The run's directory.</summary>
    public string Root { get; }

    internal bool? LocalRepositoryAfterExit { get; set; }

    internal string? VerifiedReturnRevision { get; set; }

    /// <summary>
    /// For a resumed session (the original issue): the folder the session works in, which is the agent's working directory instead of
    /// a worktree of the run's own. The run only reads it; nothing the runner cleans up is ever removed from it.
    /// </summary>
    public string? SessionFolder { get; }

    /// <summary>The agent's working directory.</summary>
    public string WorkTree => SessionFolder ?? Path.Combine(Root, "work");

    /// <summary>The prompt, as given to the agent.</summary>
    public string PromptFile => Path.Combine(Root, "prompt.md");

    /// <summary>The delivery as received (body still sealed), for the local MCP server.</summary>
    public string DeliveryFile => Path.Combine(Root, "delivery.json");

    /// <summary>Where the local MCP server records the agent's <c>settle</c> call.</summary>
    public string SettleFile => Path.Combine(Root, "settle.json");

    /// <summary>An answer kept from a settle refused for an attachment (the original issue; <c>KeptAnswer.KeptFile</c>), beside <see cref="SettleFile"/>.</summary>
    public string KeptSettleFile => Path.Combine(Root, "settle-kept.json");

    /// <summary>The kept output.</summary>
    public string LogFile => Path.Combine(Root, LogFileName);

    /// <summary>A fresh run directory for <paramref name="deliveryId"/>, marked in progress until <see cref="Dispose"/>; an earlier run's folder is never reused.</summary>
    /// <param name="workRoot">The runner's work folder.</param>
    /// <param name="deliveryId">The delivery.</param>
    /// <param name="attempt">Its attempt.</param>
    /// <param name="sessionFolder">A resumed session's own folder (the original issue), the agent's working directory; null for a worktree in the run.</param>
    public static RunWorkspace Create(string workRoot, string deliveryId, int attempt, string? sessionFolder = null)
    {
        var name = new string([.. deliveryId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_')]) + "-" + attempt;
        var workspace = new RunWorkspace(Path.Combine(workRoot, name), sessionFolder);
        if (Directory.Exists(workspace.Root))
        {
            workspace = new RunWorkspace(workspace.Root + "-" + Guid.NewGuid().ToString("N"), sessionFolder);
        }
        InProgress[workspace.Root] = 0;
        try
        {
            Directory.CreateDirectory(workspace.Root);
            // Locked under another name, then renamed: the lock file is never there unlocked, even for a moment (on macOS
            // and Linux .NET locks a file just after creating it), so a sweep can't take a starting run for a finished one.
            var starting = Path.Combine(workspace.Root, LockFileName + ".new");
            workspace._lock = new FileStream(starting, FileMode.CreateNew, FileAccess.ReadWrite, OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None);
            File.Move(starting, Path.Combine(workspace.Root, LockFileName));
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
        return workspace;
    }

    /// <summary>Keeps the folder (the only copy of work that never reached the service) until <paramref name="until"/>; housekeeping deletes it after that.</summary>
    public void Hold(DateTimeOffset until) =>
        File.WriteAllText(Path.Combine(Root, HoldFileName), until.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The run is no longer in progress: housekeeping may delete its folder (unless it is held).</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _lock, null)?.Dispose();
        InProgress.TryRemove(Root, out _);
    }

    /// <summary>
    /// Prepares <see cref="WorkTree"/> as the role's run environment says. In bundle mode without
    /// a bundle attachment, agent runs try the referenced repository before initialising an empty one.
    /// </summary>
    /// <param name="delivery">The task.</param>
    /// <param name="locate">Finds a command (git).</param>
    /// <param name="bundles">Gets the task's git bundle.</param>
    /// <param name="cancellationToken">Stops the checkout.</param>
    /// <param name="wrap">The run's sandbox (the original issue): git runs with the run's environment and repository credential.</param>
    /// <param name="ask">the original issue: says in the run's progress what it waits for from the sender; null when it can't ask.</param>
    /// <param name="historyWait">the original issue: how long to wait for the repository's earlier history from the sender.</param>
    /// <returns>Null when ready, else what went wrong.</returns>
    public async Task<string?> CheckOutAsync(TaskDelivery delivery, Func<string, string?> locate, IBundleSource? bundles, CancellationToken cancellationToken,
        Func<ProcessLaunch, ProcessLaunch>? wrap = null, Func<ProgressRequest, CancellationToken, Task>? ask = null, TimeSpan? historyWait = null)
    {
        wrap ??= launch => launch;
        var mode = delivery.RunEnvironment.Checkout;
        if (mode == CheckoutMode.Empty)
        {
            Directory.CreateDirectory(WorkTree);
            return null;
        }

        var git = locate("git");
        if (git is null)
        {
            return "git isn't installed on this machine, and this role checks out a repository.";
        }

        if (mode == CheckoutMode.Bundle)
        {
            var bundle = delivery.Envelope.Attachments.FirstOrDefault(a => a.Format == AttachmentFormat.GitBundle);
            var file = bundle is null || bundles is null ? null : await bundles.FetchAsync(delivery, bundle, Root, cancellationToken).ConfigureAwait(false);
            if (file is null)
            {
                if (bundle is not null && delivery.RunnerAction is not null)
                    return "The workflow action could not check out the task's code bundle.";

                if (bundle is null && delivery.RunnerAction is null
                    && RunnerGit.RepositoryRef(delivery.Envelope.Refs) is { } repositoryRef)
                {
                    var error = await CheckOutRepositoryAsync(git, repositoryRef, wrap, cancellationToken).ConfigureAwait(false);
                    if (error is null)
                        return null;
                    if (RepositoryRefused) return error;

                    if (!TryDelete(WorkTree))
                        return $"Couldn't remove the incomplete repository checkout. {error}";

                    GitLog?.Invoke($"Repository checkout failed. Starting with an empty repository. {error}");
                }

                // A bundle the runner couldn't get is still available to the agent through the mbus tools.
                return await GitAsync(git, ["init", "--quiet", WorkTree], wrap, cancellationToken).ConfigureAwait(false);
            }
            return await CheckOutBundleAsync(git, delivery, bundle!, file, bundles!, wrap, ask, historyWait ?? DefaultHistoryWait, cancellationToken).ConfigureAwait(false);
        }

        // Clone: the first ref that names a repository, with the run's own repository credential when
        // a sandbox gives it one (the original issue).
        var repoRef = RunnerGit.RepositoryRef(delivery.Envelope.Refs);
        if (repoRef is null)
        {
            return "The task names no repository to check out.";
        }
        return await CheckOutRepositoryAsync(git, repoRef, wrap, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> CheckOutRepositoryAsync(string git, Ref repoRef,
        Func<ProcessLaunch, ProcessLaunch> wrap, CancellationToken cancellationToken)
    {
        var url = RepositoryUrl(repoRef)!;
        if (!RunnerGit.IsRemoteRepository(url))
        {
            RepositoryRefused = true;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Contains(':')
                ? "Repository addresses must not contain passwords."
                : "The repository address must use HTTPS or SSH. Local folders and file addresses aren't allowed.";
        }
        var mirrors = RepoMirrors.For(Path.GetDirectoryName(Root)!);
        var error = mirrors.Has(url) ? await CopyFromMirrorAsync(git, mirrors, url, wrap, cancellationToken).ConfigureAwait(false) : "no mirror";
        if (error is not null)
        {
            TryDelete(WorkTree);
            error = await GitAsync(git, ["clone", "--quiet", "--", url, WorkTree], RemoteGit(wrap), cancellationToken).ConfigureAwait(false);
        }
        if (error is null && repoRef.Sha is { Length: > 0 } sha)
        {
            error = await GitAsync(git, ["-C", WorkTree, "checkout", "--quiet", sha], wrap, cancellationToken).ConfigureAwait(false);
        }
        if (error is null)
        {
            // the original issue: before the agent starts, the mirror takes in what this run fetched, for the next run.
            await mirrors.UpdateFromAsync(git, url, WorkTree, cancellationToken).ConfigureAwait(false);
        }
        return error is null || RepositoryCredentialNote is null ? error : error + " " + RepositoryCredentialNote;
    }

    /// <summary>How long a run waits for the repository's earlier history from the sender when no wait is given.</summary>
    public static readonly TimeSpan DefaultHistoryWait = TimeSpan.FromMinutes(30);

    /// <summary>
    /// the original issue: the task's code from its bundle, through this machine's copy of the repository (<see cref="RepoMirrors"/>): a
    /// bundle of the whole history goes into the copy for the next task; one that starts after earlier commits needs a copy
    /// that has them, or the whole history from the sender (asked for and waited on). The working copy is a repository of
    /// its own holding only the task's branch and its history, whatever else the copy holds.
    /// </summary>
    private async Task<string?> CheckOutBundleAsync(string git, TaskDelivery delivery, AttachmentRef bundle, string file, IBundleSource bundles,
        Func<ProcessLaunch, ProcessLaunch> wrap, Func<ProgressRequest, CancellationToken, Task>? ask, TimeSpan historyWait, CancellationToken cancellationToken)
    {
        Attachments.Client.BundleHeader header;
        try
        {
            header = Attachments.Client.GitBundles.ReadHeader(file);
        }
        catch (Exception ex) when (ex is GitException or IOException)
        {
            return ex is GitException ? "The code sent with the task isn't a git bundle: " + ex.Message : "Couldn't read the code sent with the task.";
        }
        var branch = bundle.Branch is { Length: > 0 } named ? named
            : header.Refs.Keys.FirstOrDefault(r => r.StartsWith("refs/heads/", StringComparison.Ordinal))?["refs/heads/".Length..];
        if (branch is null || !header.Refs.TryGetValue("refs/heads/" + branch, out var head))
        {
            return $"The code sent with the task doesn't hold its branch{(branch is null ? "" : " " + branch)}.";
        }
        var mirrors = RepoMirrors.For(Path.GetDirectoryName(Root)!);
        var workspace = delivery.Envelope.WorkspaceId;
        string? copy;
        if (header.Prerequisites.Count == 0)
        {
            // The whole history: kept for the next task. Without a copy the run starts from the bundle alone, as before.
            copy = (await mirrors.TakeInHistoryAsync(git, workspace, file, header, cancellationToken).ConfigureAwait(false)).Copy;
            if (copy is null)
            {
                var reference = $"refs/remotes/origin/{branch}";
                return await GitAsync(git, ["init", "--quiet", WorkTree], wrap, cancellationToken).ConfigureAwait(false)
                    ?? await GitAsync(git, ["-C", WorkTree, "remote", "add", "origin", Path.GetFullPath(file)], wrap, cancellationToken).ConfigureAwait(false)
                    ?? await ApplyBundleAsync(git, file, WorkingCopyDescription, branch, reference, wrap, cancellationToken).ConfigureAwait(false)
                    ?? await GitAsync(git, ["-C", WorkTree, "checkout", "--quiet", "-b", branch, "--track", "origin/" + branch], wrap, cancellationToken).ConfigureAwait(false);
            }
        }
        else if ((copy = await mirrors.FindCodeAsync(git, workspace, file, cancellationToken).ConfigureAwait(false)) is null)
        {
            string? repositoryError = null;
            if (RunnerGit.RepositoryRef(delivery.Envelope.Refs) is { } repoRef)
            {
                var repository = RepositoryUrl(repoRef)!;
                const string reference = "refs/remotes/mbus/task";
                repositoryError = await CheckOutRepositoryAsync(git, repoRef, wrap, cancellationToken).ConfigureAwait(false)
                    ?? await ApplyBundleAsync(git, file, $"repository '{repository}'", branch, reference, wrap, cancellationToken).ConfigureAwait(false)
                    ?? await GitAsync(git, ["-C", WorkTree, "checkout", "--quiet", "-B", branch, reference], wrap, cancellationToken).ConfigureAwait(false);
                if (repositoryError is null)
                    return null;
                if (RepositoryRefused) return repositoryError;
                TryDelete(WorkTree);
            }

            var minutes = Math.Max(1, (int)Math.Round(historyWait.TotalMinutes));
            var history = ask is null ? null
                : await bundles.RequestHistoryAsync(delivery, bundle, Root, ask, historyWait, cancellationToken).ConfigureAwait(false);
            if (history is null)
            {
                return ask is null
                    ? repositoryError ?? $"The code sent with the task starts after commits missing from {WorkingCopyDescription}: {string.Join(", ", header.Prerequisites)}."
                    : (repositoryError is null ? "" : repositoryError + " ") + $"This machine has no copy of the repository's earlier history, and the sender's mbus didn't send it within {minutes} minutes "
                      + "(the session that sent the task may have closed). Send the task again from an open session.";
            }
            Attachments.Client.BundleHeader whole;
            try
            {
                whole = Attachments.Client.GitBundles.ReadHeader(history);
            }
            catch (Exception ex) when (ex is GitException or IOException)
            {
                return ex is GitException ? "The earlier history the sender sent isn't a git bundle: " + ex.Message : "Couldn't read the earlier history the sender sent.";
            }
            var (taken, error) = await mirrors.TakeInHistoryAsync(git, workspace, history, whole, cancellationToken).ConfigureAwait(false);
            if (taken is null)
            {
                return "Couldn't use the earlier history the sender sent: " + error;
            }
            copy = taken;
        }
        var failed = await mirrors.CheckOutCodeAsync(git, copy, file, header, branch, head, WorkTree, cancellationToken).ConfigureAwait(false);
        if (failed is not null)
        {
            TryDelete(WorkTree);
            return "Couldn't check out the code sent with the task: " + failed;
        }
        return await GitAsync(git, ["-C", WorkTree, "checkout", "--quiet", "-b", branch, "--track", "origin/" + branch], wrap, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The working copy from this machine's mirror (the original issue), then brought up to date from the real remote with the run's
    /// credential: only what is new comes over the network. Null when ready, else why it couldn't be (then it is cloned).
    /// </summary>
    private async Task<string?> CopyFromMirrorAsync(string git, RepoMirrors mirrors, string url, Func<ProcessLaunch, ProcessLaunch> wrap, CancellationToken cancellationToken)
    {
        // The runner copies its own mirror (no shared files, so the run can't change it); the run's git does the rest.
        var error = await GitAsync(git, ["clone", "--quiet", "--no-hardlinks", "--no-checkout", mirrors.PathFor(url), WorkTree], launch => launch, cancellationToken).ConfigureAwait(false)
                    ?? await GitAsync(git, ["-C", WorkTree, "remote", "set-url", "origin", url], wrap, cancellationToken).ConfigureAwait(false)
                    ?? await GitAsync(git, ["-C", WorkTree, "fetch", "--quiet", "--prune", "--tags", "origin"], RemoteGit(wrap), cancellationToken).ConfigureAwait(false);
        if (error is null && RepoMirrors.CurrentBranch(WorkTree) is { } branch)
        {
            error = await GitAsync(git, ["-C", WorkTree, "checkout", "--quiet", "-B", branch, "refs/remotes/origin/" + branch], wrap, cancellationToken).ConfigureAwait(false);
        }
        return error;
    }

    private static Func<ProcessLaunch, ProcessLaunch> RemoteGit(Func<ProcessLaunch, ProcessLaunch> wrap) => launch => wrap(launch with
    {
        // Also reject a local transport introduced by an inherited URL rewrite.
        Environment = new Dictionary<string, string?>(launch.Environment) { ["GIT_ALLOW_PROTOCOL"] = "https:ssh" },
    });

    /// <summary>
    /// Ends the run and deletes its folder: the worktree, the code bundle, git settings, prompt, caches and anything the
    /// agent left beside them. With <paramref name="keepLog"/> the log stays. Never anything a link points at, nor a resumed
    /// session's own folder.
    /// </summary>
    /// <returns>False when something couldn't be deleted yet (a file still open); housekeeping tries again.</returns>
    public bool CleanUp(bool keepLog)
    {
        Dispose();
        return Clear(Root, keepLog);
    }

    /// <summary>The run's own package caches (<see cref="Agents.CodexAdapter.RunFolders"/>), outside the worktree.</summary>
    public string CacheDirectory => Path.Combine(Root, Agents.CodexAdapter.RunFolders.CacheFolder);

    /// <summary>Deletes a run folder, or everything in it but its log.</summary>
    private static bool Clear(string root, bool keepLog)
    {
        if (!keepLog || !File.Exists(Path.Combine(root, LogFileName)))
        {
            return TryDelete(root);
        }
        try
        {
            var cleared = true;
            foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo directory)
                {
                    cleared &= TryDelete(directory.FullName);
                }
                else if (entry.Name != LogFileName)
                {
                    cleared &= TryDeleteFile(entry);
                }
            }
            return cleared;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteFile(FileSystemInfo file)
    {
        try
        {
            if (file.LinkTarget is null) file.Attributes = FileAttributes.Normal;
            file.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What a housekeeping pass (<see cref="Sweep"/>) did.</summary>
    /// <param name="Deleted">Run folders deleted (all but their logs).</param>
    /// <param name="Failed">Run folders it couldn't delete yet (a file still open); the next pass tries again.</param>
    /// <param name="Logs">Folders holding only an old log (the original issue), deleted with it.</param>
    public readonly record struct SweepResult(int Deleted, int Failed, int Logs = 0);

    /// <summary>
    /// the original issue: deletes what finished runs left in <paramref name="workRoot"/> (a crash, a restart, a delete that failed, an
    /// expired hold), keeping only their logs. It looks only at the work folder's own sub-folders named as
    /// <see cref="Create"/> names them, never at a link, <c>.mirrors</c> or anything else, and leaves a run in progress
    /// (its lock is held, here or by another runner using the same folder) and a held run before its time.
    /// the original issue: a folder holding only its log goes too, log and all, once the log is older than <paramref name="keepLogsFor"/>.
    /// </summary>
    /// <param name="workRoot">The runner's work folder.</param>
    /// <param name="now">The time, for holds, logs and folders from before the lock file.</param>
    /// <param name="keepLogsFor">How long a finished run's log is kept; null keeps logs.</param>
    public static SweepResult Sweep(string workRoot, DateTimeOffset now, TimeSpan? keepLogsFor = null)
    {
        var root = new DirectoryInfo(workRoot);
        if (!root.Exists)
        {
            return default;
        }
        var deleted = 0;
        var failed = 0;
        var logs = 0;
        foreach (var folder in root.EnumerateDirectories())
        {
            try
            {
                if (!IsFinishedRun(folder, now) || IsHeld(folder, now))
                {
                    continue;
                }
                if (OnlyItsLog(folder))
                {
                    if (keepLogsFor is { } keep && File.GetLastWriteTimeUtc(Path.Combine(folder.FullName, LogFileName)) <= (now - keep).UtcDateTime)
                    {
                        if (TryDelete(folder.FullName)) logs++;
                        else failed++;
                    }
                    continue;
                }
                if (Clear(folder.FullName, keepLog: true)) deleted++;
                else failed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }
        return new SweepResult(deleted, failed, logs);
    }

    /// <summary>A sub-folder <see cref="Create"/> made, for a run that is no longer in progress.</summary>
    private static bool IsFinishedRun(DirectoryInfo folder, DateTimeOffset now)
    {
        if (!RunFolderName().IsMatch(folder.Name) || folder.LinkTarget is not null || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || InProgress.ContainsKey(folder.FullName))
        {
            return false;
        }
        var lockFile = Path.Combine(folder.FullName, LockFileName);
        if (File.Exists(lockFile))
        {
            try
            {
                // Held by its run, in this process or another: in progress.
                using var free = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        // No lock file: from a runner before the original issue, a run that is just starting, or one whose agent removed the lock. It
        // counts once it is empty or holds a run's files, and hasn't changed for longer than any run lasts.
        var runFiles = new[] { "delivery.json", "prompt.md", LogFileName, "task.bundle", "work", LockFileName + ".new" };
        if (!runFiles.Any(name => Path.Exists(Path.Combine(folder.FullName, name))) && folder.EnumerateFileSystemInfos().Any())
        {
            return false;
        }
        return folder.LastWriteTimeUtc <= (now - HoldFor).UtcDateTime;
    }

    private static bool IsHeld(DirectoryInfo folder, DateTimeOffset now)
    {
        var file = new FileInfo(Path.Combine(folder.FullName, HoldFileName));
        if (!file.Exists)
        {
            return false;
        }
        var until = DateTimeOffset.TryParse(File.ReadAllText(file.FullName).Trim(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) + HoldFor;
        return now < until;
    }

    private static bool OnlyItsLog(DirectoryInfo folder) =>
        folder.EnumerateFileSystemInfos().Select(entry => entry is FileInfo { Name: LogFileName }).DefaultIfEmpty(false).All(log => log);

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_-]+-[0-9]+(-[0-9a-f]{32})?$")]
    private static partial System.Text.RegularExpressions.Regex RunFolderName();

    /// <summary>The clone URL a ref points at, or null.</summary>
    public static string? RepositoryUrl(Ref r)
    {
        if (r.Repo is { Length: > 0 } repo)
        {
            if (r.Kind.StartsWith("github.", StringComparison.Ordinal))
            {
                return $"https://github.com/{repo}.git";
            }
            if (r.Kind.StartsWith("ado.", StringComparison.Ordinal) && repo.Split('/') is [var org, var project, var name])
            {
                return $"https://dev.azure.com/{org}/{project}/_git/{name}";
            }
        }
        return r.Url is { Length: > 0 } url && (r.Kind is "git.repository" or "git.repo"
            || url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) ? url : null;
    }

    /// <summary>Deletes a folder tree, including git's read-only object files; leaves it when something still holds a file.</summary>
    public static bool TryDelete(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }
                var directory = new DirectoryInfo(path);
                // A link or junction (or any other reparse point) is removed itself; what it points at is never entered.
                if (directory.LinkTarget is null && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    foreach (var child in directory.EnumerateFileSystemInfos())
                    {
                        if (child is DirectoryInfo folder)
                        {
                            if (!TryDelete(folder.FullName)) return false;
                        }
                        else
                        {
                            if (child.LinkTarget is null) child.Attributes = FileAttributes.Normal;
                            child.Delete();
                        }
                    }
                }
                directory.Delete();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A process that just exited may still hold a handle for a moment on Windows.
            }
        }
        return false;
    }

    internal void RemoveOwnedIndexLock(bool existedBeforeCommand, DateTime commandFinishedUtc)
    {
        if (existedBeforeCommand || SessionFolder is not null) return;
        var git = new DirectoryInfo(Path.Combine(WorkTree, ".git"));
        var file = new FileInfo(Path.Combine(git.FullName, "index.lock"));
        if (!git.Exists || git.LinkTarget is not null || !file.Exists || file.LinkTarget is not null
            || file.LastWriteTimeUtc >= commandFinishedUtc) return;
        // The lock was absent before our successful command; nothing else runs in this private
        // folder yet, and hooks/fsmonitor are disabled. Never call after cancellation or after
        // the agent, setup command or tools start. The exclusive open adds protection on Windows;
        // it protects nothing against git on macOS/Linux, where sharing locks are advisory.
        try
        {
            using (new FileStream(file.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            file.Delete();
            GitLog?.Invoke("Removed a leftover git lock file from the checkout.");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<string?> GitAsync(string git, IReadOnlyList<string> args, Func<ProcessLaunch, ProcessLaunch> wrap, CancellationToken cancellationToken)
        => (await RunGitAsync(git, args, wrap, cancellationToken, captureOutput: false).ConfigureAwait(false)).Error;

    private async Task<(string Output, string? Error)> RunGitAsync(string git, IReadOnlyList<string> args,
        Func<ProcessLaunch, ProcessLaunch> wrap, CancellationToken cancellationToken, bool captureOutput = true)
    {
        // Only a lock absent before our command can be ours. Never touch a pre-existing lock,
        // a resumed session, or a gitdir outside this new, private working copy.
        var indexLock = Path.Combine(WorkTree, ".git", "index.lock");
        var existed = File.Exists(indexLock);
        var output = new List<string>();
        using var process = AgentProcess.Start(wrap(new ProcessLaunch
        {
            FileName = git,
            TrustedCheckout = true,
            // Inherited post-checkout hooks/fsmonitor can start git children that outlive checkout.
            Arguments = RunnerGit.Arguments(args),
            WorkingDirectory = Root,
            Environment = new Dictionary<string, string?>(GitEnvironment)
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "never",
            },
        }), captureOutput ? line =>
        {
            lock (output) output.Add(line);
        } : null, tailLines: 20);
        using var kill = cancellationToken.Register(process.Kill);
        var exit = await process.WaitForExitAsync().ConfigureAwait(false);
        // WaitForExitAsync above is deliberately uncancellable: cancellation kills the tree and
        // reaps git before handing the folder to anybody else. Cancelled runs skip cleanup.
        cancellationToken.ThrowIfCancellationRequested();
        if (exit == 0) RemoveOwnedIndexLock(existed, DateTime.UtcNow);
        lock (output)
        {
            if (exit == 0) return (string.Join('\n', output), null);
            var tail = RunnerGit.DescribePath(string.Join(" ", process.Tail()).Trim(), WorkTree, WorkingCopyDescription);
            tail = RunnerGit.DescribePath(tail, Root, "this machine's run folder");
            return ("", $"git {args[0]} failed (exit {exit}): {tail}");
        }
    }
}
