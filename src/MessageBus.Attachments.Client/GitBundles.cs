namespace MessageBus.Attachments.Client;

/// <summary>A git bundle written by <see cref="GitBundles.CreateAsync"/>.</summary>
/// <param name="Path">The bundle file.</param>
/// <param name="Branch">The branch it carries (<c>refs/heads/{Branch}</c> inside the bundle).</param>
/// <param name="HeadCommit">The branch's commit when bundled.</param>
/// <param name="BaseCommit">The resolved exclusion revision; null for a full bundle.</param>
public sealed record GitBundle(string Path, string Branch, string HeadCommit, string? BaseCommit)
{
    /// <summary>The prerequisite commit IDs advertised by a Git bundle.</summary>
    public IReadOnlyList<string> Prerequisites { get; init; } = [];
}

/// <summary>A bundle's header (<see cref="GitBundles.ReadHeader"/>).</summary>
/// <param name="Prerequisites">Commits the receiver must already have; empty for a bundle of the whole history.</param>
/// <param name="Refs">The refs it carries (<c>refs/heads/…</c>) and their commits.</param>
public sealed record BundleHeader(IReadOnlyList<string> Prerequisites, IReadOnlyDictionary<string, string> Refs);

/// <summary>
/// Git bundles and patches through the git command line. A bundle carries one branch: from a base
/// commit the receiver already has (smaller), or its whole history (for a receiver starting empty).
/// </summary>
public static class GitBundles
{
    /// <summary>Media type of a git bundle.</summary>
    public const string BundleContentType = "application/x-git-bundle";

    /// <summary>Media type of a <c>git format-patch</c> mailbox.</summary>
    public const string PatchContentType = "text/x-patch";

    /// <summary>
    /// Writes <paramref name="branch"/> (default: the checked-out branch) of <paramref name="repository"/>
    /// to a bundle at <paramref name="outputPath"/>: the commits after <paramref name="baseCommit"/>, or the
    /// whole history when it is null, empty, or local-only in a repository with a remote.
    /// </summary>
    /// <param name="repository">The repository (or a worktree of it).</param>
    /// <param name="outputPath">Where to write the bundle.</param>
    /// <param name="branch">A local or remote-tracking branch to send; default the current one.</param>
    /// <param name="baseCommit">A commit the receiver has (any revision); null or empty for the whole history.</param>
    /// <param name="ct">Cancels.</param>
    public static async Task<GitBundle> CreateAsync(string repository, string outputPath, string? branch = null, string? baseCommit = null,
        CancellationToken ct = default)
    {
        var source = branch is not null && IsBranchRef(branch) ? branch : await ResolveBranchAsync(repository, branch, ct);
        branch = BundleBranch(source);
        var head = await Git.CommitIdAsync(repository, source, ct);
        var @base = string.IsNullOrEmpty(baseCommit) ? null : await Git.CommitIdAsync(repository, baseCommit, ct);
        if (@base == head) throw new GitException($"Nothing to send: {branch} has no commits after {baseCommit}.");

        if (@base is not null && !string.IsNullOrWhiteSpace(await Git.RunAsync(repository, ["remote"], ct))
            && string.IsNullOrWhiteSpace(await Git.RunAsync(repository, ["for-each-ref", "--contains", @base, "refs/remotes"], ct)))
            @base = null;

        List<string> args = ["bundle", "create", "--quiet", Path.GetFullPath(outputPath), source];
        if (@base is not null) args.Add($"^{@base}");
        await Git.RunAsync(repository, args, ct);
        var target = $"refs/heads/{branch}";
        var header = ReadHeader(outputPath, source, out var refStart, out var refEnd);
        if (!header.Refs.TryGetValue(source, out var advertisedHead))
            throw new GitException("The bundle did not contain the branch to send.");

        if (source != target)
        {
            // Copy the header and pack as bytes, changing only the advertised ref line.
            var renamed = outputPath + ".renamed";
            try
            {
                await using (var input = File.OpenRead(outputPath))
                await using (var output = File.Create(renamed))
                {
                    var buffer = new byte[8192];
                    while (input.Position < refStart)
                    {
                        var count = (int)Math.Min(buffer.Length, refStart - input.Position);
                        await input.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"{advertisedHead} {target}\n"), ct);
                    input.Position = refEnd;
                    await input.CopyToAsync(output, ct);
                }
                File.Move(renamed, outputPath, overwrite: true);
            }
            finally { File.Delete(renamed); }
        }
        return new GitBundle(outputPath, branch, advertisedHead, @base) { Prerequisites = header.Prerequisites };
    }

    /// <summary>
    /// Checks the bundle against <paramref name="repository"/> (made with <c>git init</c> if the folder
    /// isn't one yet), then fetches its <paramref name="branch"/> into <paramref name="into"/> (default
    /// <c>refs/remotes/mbus/{branch}</c>; a plain name means <c>refs/heads/{name}</c>). Returns the commit id fetched.
    /// </summary>
    /// <param name="bundlePath">The bundle (opened and hash-checked already).</param>
    /// <param name="repository">The receiving repository.</param>
    /// <param name="branch">The branch inside the bundle (<see cref="Contracts.Messaging.AttachmentRef.Branch"/>).</param>
    /// <param name="into">The ref to fetch into.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="GitException">Not a bundle, or the repository lacks the commit it starts from.</exception>
    public static async Task<string> FetchAsync(string bundlePath, string repository, string branch, string? into = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(repository);
        if (!IsRepository(repository)) await Git.RunAsync(repository, ["init", "--quiet"], ct);
        var bundle = Path.GetFullPath(bundlePath);

        var target = into is null ? $"refs/remotes/mbus/{branch}" : into.StartsWith("refs/", StringComparison.Ordinal) ? into : $"refs/heads/{into}";
        await ApplyAsync(bundle, repository, [$"+refs/heads/{branch}:{target}"],
            (args, token) => Git.RunAsync(repository, args, token), ct);
        return await Git.CommitIdAsync(repository, target, ct);
    }

    /// <summary>Applies a bundle in an initialized repository, recovering missing prerequisites from its remotes.</summary>
    /// <param name="bundle">The bundle file.</param>
    /// <param name="repository">Repository description for errors.</param>
    /// <param name="refSpecs">Refs to fetch from the bundle.</param>
    /// <param name="run">Runs git in the receiving repository and throws <see cref="GitException"/> on failure.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <param name="writeFetchHead">Whether the bundle fetch writes FETCH_HEAD.</param>
    public static async Task ApplyAsync(string bundle, string repository, IReadOnlyList<string> refSpecs,
        Func<IReadOnlyList<string>, CancellationToken, Task<string>> run, CancellationToken ct, bool writeFetchHead = true)
    {
        bundle = Path.GetFullPath(bundle);
        var missing = await MissingPrerequisitesAsync(ReadHeader(bundle), run, ct);
        if (missing.Count > 0)
        {
            var remotes = (await run(["remote"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var remote in remotes.OrderBy(r => r == "origin" ? 0 : 1))
            {
                foreach (var commit in missing)
                    await TryRunAsync(run, ["fetch", "--quiet", "--no-tags", "--no-recurse-submodules", "--no-write-fetch-head", "--", remote, commit], ct);

                missing = await MissingAsync(missing, run, ct);
                if (missing.Count > 0 && (await run(["rev-parse", "--is-shallow-repository"], ct)).Trim() == "true")
                {
                    await TryRunAsync(run, ["fetch", "--quiet", "--no-tags", "--no-recurse-submodules", "--no-write-fetch-head", "--unshallow", "--", remote], ct);
                    missing = await MissingAsync(missing, run, ct);
                }
                if (missing.Count == 0) break;
            }
            if (missing.Count > 0)
                throw new GitException($"The bundle needs commits missing from repository '{repository}': {string.Join(", ", missing)}. Fetch them or ask the sender to resend with base: \"\" for the whole history.", input: "repository");
        }
        var args = new List<string> { "fetch", "--quiet", "--no-tags", "--no-recurse-submodules" };
        if (!writeFetchHead) args.Add("--no-write-fetch-head");
        args.AddRange(["--", bundle, .. refSpecs]);
        await run(args, ct);
    }

    /// <summary>Returns the bundle's prerequisite commits absent from the receiving repository.</summary>
    public static Task<List<string>> MissingPrerequisitesAsync(BundleHeader header,
        Func<IReadOnlyList<string>, CancellationToken, Task<string>> run, CancellationToken ct) =>
        MissingAsync(header.Prerequisites, run, ct);

    private static async Task<List<string>> MissingAsync(IReadOnlyList<string> commits,
        Func<IReadOnlyList<string>, CancellationToken, Task<string>> run, CancellationToken ct)
    {
        var missing = new List<string>();
        foreach (var commit in commits)
            if (!await TryRunAsync(run, ["cat-file", "-e", commit + "^{commit}"], ct))
                missing.Add(commit);
        return missing;
    }

    private static async Task<bool> TryRunAsync(Func<IReadOnlyList<string>, CancellationToken, Task<string>> run,
        IReadOnlyList<string> args, CancellationToken ct)
    {
        try { await run(args, ct); return true; }
        catch (GitException e) when (e.RawOutput) { return false; }
    }

    /// <summary>
    /// Writes the commits of <paramref name="branch"/> after <paramref name="baseCommit"/> as a
    /// <c>git format-patch</c> mailbox (binary-safe), for <see cref="ApplyPatchAsync"/>.
    /// </summary>
    /// <param name="repository">The repository.</param>
    /// <param name="outputPath">Where to write the patch.</param>
    /// <param name="baseCommit">The commit the receiver has.</param>
    /// <param name="branch">The branch; default the current one.</param>
    /// <param name="ct">Cancels.</param>
    public static async Task<GitBundle> CreatePatchAsync(string repository, string outputPath, string baseCommit, string? branch = null,
        CancellationToken ct = default)
    {
        var source = branch is not null && IsBranchRef(branch) ? branch : await ResolveBranchAsync(repository, branch, ct);
        branch = BundleBranch(source);
        var head = await Git.CommitIdAsync(repository, source, ct);
        var @base = await Git.CommitIdAsync(repository, baseCommit, ct);
        var patch = await Git.RunAsync(repository, ["format-patch", "--binary", "--stdout", $"{@base}..{head}"], ct);
        await File.WriteAllTextAsync(outputPath, patch, ct);
        return new GitBundle(outputPath, branch, head, @base);
    }

    /// <summary>Applies a patch from <see cref="CreatePatchAsync"/> to the checked-out branch with <c>git am --3way</c>.</summary>
    /// <param name="patchPath">The patch.</param>
    /// <param name="repository">The working tree to apply it in.</param>
    /// <param name="ct">Cancels.</param>
    public static Task ApplyPatchAsync(string patchPath, string repository, CancellationToken ct = default) =>
        Git.RunAsync(repository, ["am", "--3way", "--quiet", Path.GetFullPath(patchPath)], ct);

    /// <summary>The branch's short name: <paramref name="branch"/> without <c>refs/heads/</c>, or the checked-out one when null.</summary>
    /// <exception cref="GitException">Not a valid branch name, or HEAD isn't on a branch.</exception>
    public static async Task<string> BranchAsync(string repository, string? branch, CancellationToken ct = default)
    {
        if (branch is null)
        {
            try
            {
                return (await Git.RunAsync(repository, ["symbolic-ref", "--short", "HEAD"], ct)).Trim();
            }
            catch (GitException e) when (e.RawOutput && e.StandardError?.Contains("not a symbolic ref", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new GitException("HEAD isn't on a branch. Say which branch to send.");
            }
        }
        if (branch.StartsWith("refs/heads/", StringComparison.Ordinal)) branch = branch["refs/heads/".Length..];
        try { await Git.RunAsync(repository, ["check-ref-format", "--branch", branch], ct); }
        catch (GitException e) when (e.StandardError?.Contains("is not a valid branch name", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new GitException("That is not a valid branch name.", input: "branch");
        }
        return branch;
    }

    /// <summary>Resolves a local branch, an explicit remote-tracking ref, or an mbus/ branch.</summary>
    public static async Task<string> ResolveBranchAsync(string repository, string? branch, CancellationToken ct = default)
    {
        await Git.RunAsync(repository, ["rev-parse", "--git-dir"], ct);
        if (branch is null) return "refs/heads/" + await BranchAsync(repository, null, ct);
        var explicitRef = branch?.StartsWith("refs/", StringComparison.Ordinal) == true ? branch : null;
        branch = await BranchAsync(repository, branch, ct);
        branch = explicitRef ?? branch;
        var candidates = branch.StartsWith("refs/", StringComparison.Ordinal)
            ? new[] { branch } : branch.StartsWith("mbus/", StringComparison.Ordinal) ? new[] { $"refs/remotes/{branch}" } : new[] { $"refs/heads/{branch}" };
        foreach (var candidate in candidates)
        {
            if (candidate == "HEAD" || IsRemoteHead(candidate)) continue;
            if (!candidate.StartsWith("refs/heads/", StringComparison.Ordinal) && !candidate.StartsWith("refs/remotes/", StringComparison.Ordinal)) continue;
            if (candidate.StartsWith("refs/remotes/", StringComparison.Ordinal) && !candidate["refs/remotes/".Length..].Contains('/')) continue;
            try { await Git.CommitIdAsync(repository, candidate, ct); return candidate; }
            catch (GitException e) when (e.RawOutput) { }
        }
        throw new GitException("The branch was not found. Give an existing local or remote-tracking branch.", input: "branch");
    }

    private static bool IsRemoteHead(string source) => source.StartsWith("refs/remotes/", StringComparison.Ordinal) &&
        source["refs/remotes/".Length..].Split('/') is [_, "HEAD"];

    private static bool IsBranchRef(string source) =>
        !IsRemoteHead(source) &&
        (source.StartsWith("refs/heads/", StringComparison.Ordinal) ||
         source.StartsWith("refs/remotes/", StringComparison.Ordinal) && source["refs/remotes/".Length..].Contains('/'));

    /// <summary>The local branch name advertised when sending a ref.</summary>
    public static string BundleBranch(string source) => source.StartsWith("refs/remotes/", StringComparison.Ordinal)
        ? source["refs/remotes/".Length..].Split('/', 2)[1] : source["refs/heads/".Length..];

    /// <summary>Warns when the local branch is behind the copy fetched from a worker.</summary>
    public static async Task<string?> BehindFetchedNoticeAsync(string repository, string source, CancellationToken ct = default)
    {
        if (!source.StartsWith("refs/heads/", StringComparison.Ordinal)) return null;
        var branch = BundleBranch(source);
        try
        {
            var local = await Git.CommitIdAsync(repository, source, ct);
            var fetched = await Git.CommitIdAsync(repository, $"refs/remotes/mbus/{branch}", ct);
            if (local != fetched && await IsAncestorAsync(repository, local, fetched, ct))
                return $"Local branch {branch} is behind refs/remotes/mbus/{branch}. Send branch: mbus/{branch} or update the local branch to send the fetched code.";
        }
        catch (GitException) { }
        return null;
    }

    /// <summary>Guidance when a follow-up bundle does not include a commit returned by its parent.</summary>
    public static async Task<string?> ParentCommitNoticeAsync(string repository, string head, string parentCommit, string? branch, CancellationToken ct = default)
    {
        string returned;
        try { returned = await Git.CommitIdAsync(repository, parentCommit, ct); }
        catch (GitException)
        {
            return branch is null
                ? $"The parent's returned commit {parentCommit} isn't in this repository; fetch it from its remote (bundle tip {head})."
                : $"The parent's returned commit {parentCommit} isn't in this repository; fetch it first with fetch_attachment (bundle tip {head}).";
        }
        if (await IsAncestorAsync(repository, returned, head, ct)) return null;
        var guidance = branch is null ? "Fetch it from its remote and send that branch."
            : $"Fetch the parent's result with fetch_attachment and send that branch, or mbus/{branch}.";
        return $"Bundle tip {head} doesn't contain the parent's returned commit {returned}. {guidance}";
    }

    /// <summary>
    /// the original issue: the repository's first commit along <paramref name="revision"/>'s first parents. It names the repository whatever
    /// its branches: the sender keys what it sent by it.
    /// </summary>
    /// <param name="repository">The repository.</param>
    /// <param name="revision">A commit or branch in it.</param>
    /// <param name="ct">Cancels.</param>
    public static async Task<string> RootCommitAsync(string repository, string revision, CancellationToken ct = default) =>
        (await Git.RunAsync(repository, ["rev-list", "--first-parent", "--max-parents=0", "--end-of-options", revision], ct)).Trim().Split('\n')[0].Trim();

    /// <summary>
    /// the original issue: where a bundle of <paramref name="branch"/> for a receiver that was last sent <paramref name="lastSent"/> starts: that
    /// commit, or the branch's parent when the branch hasn't moved past it (a bundle needs at least one commit). Null for the
    /// whole history: nothing recorded, the commit isn't in this repository, or the branch is a single commit.
    /// </summary>
    /// <param name="repository">The repository.</param>
    /// <param name="branch">The branch to send (refs/heads/… or a short name).</param>
    /// <param name="lastSent">The newest commit sent to that receiver before, or null.</param>
    /// <param name="ct">Cancels.</param>
    public static async Task<string?> SuggestBaseAsync(string repository, string branch, string? lastSent, CancellationToken ct = default)
    {
        if (lastSent is null) return null;
        var head = await Git.CommitIdAsync(repository, branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : await ResolveBranchAsync(repository, branch, ct), ct);
        string sent;
        try
        {
            sent = await Git.CommitIdAsync(repository, lastSent, ct);
        }
        catch (GitException)
        {
            return null; // not in this repository (another clone sent it): the whole history
        }
        if (!await IsAncestorAsync(repository, head, sent, ct)) return sent;
        try
        {
            return await Git.CommitIdAsync(repository, head + "^", ct);
        }
        catch (GitException)
        {
            return null;
        }
    }

    private static async Task<bool> IsAncestorAsync(string repository, string ancestor, string of, CancellationToken ct)
    {
        try
        {
            await Git.RunAsync(repository, ["merge-base", "--is-ancestor", ancestor, of], ct);
            return true;
        }
        catch (GitException)
        {
            return false;
        }
    }

    /// <summary>
    /// What a bundle file says about itself (the original issue): the commits the receiver must already have and the refs it carries,
    /// read from its header without git.
    /// </summary>
    /// <param name="bundlePath">The bundle.</param>
    /// <exception cref="GitException">It isn't a git bundle.</exception>
    public static BundleHeader ReadHeader(string bundlePath) => ReadHeader(bundlePath, null, out _, out _);

    private static BundleHeader ReadHeader(string bundlePath, string? source, out long refStart, out long refEnd)
    {
        using var file = File.OpenRead(bundlePath);
        refStart = refEnd = 0;
        if (ReadLine(file) is not ("# v2 git bundle" or "# v3 git bundle"))
            throw new GitException("That isn't a git bundle.");

        var prerequisites = new List<string>();
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = file.Position;
        while (ReadLine(file) is { } line)
        {
            if (line.Length == 0) return new BundleHeader(prerequisites, refs);
            if (line[0] == '-')
            {
                if (!IsObjectId(line.AsSpan(1))) throw MalformedHeader();
                prerequisites.Add(line[1..]);
            }
            else if (line[0] != '@') // a v3 capability
            {
                var separator = line.IndexOf(' ');
                if (separator < 0 || separator == line.Length - 1 || !IsObjectId(line.AsSpan(0, separator)))
                    throw MalformedHeader();
                var name = line[(separator + 1)..];
                refs[name] = line[..separator];
                if (name == source)
                {
                    refStart = start;
                    refEnd = file.Position;
                }
            }
            start = file.Position;
        }
        throw new GitException("That git bundle's header is cut short.");

        static bool IsObjectId(ReadOnlySpan<char> id) =>
            id.Length is 40 or 64 && id.IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

        static GitException MalformedHeader() => new("That git bundle's header is malformed.");

        static string? ReadLine(Stream stream)
        {
            var bytes = new List<byte>();
            // Git may include a long commit subject after a prerequisite ID.
            var skipSubject = false;
            int b;
            while ((b = stream.ReadByte()) >= 0 && b != '\n')
            {
                if (skipSubject) continue;
                if (b == ' ' && bytes.Count > 0 && bytes[0] == '-')
                {
                    skipSubject = true;
                    continue;
                }
                bytes.Add((byte)b);
                if (bytes.Count > 4096) throw MalformedHeader();
            }
            return b < 0 ? null : System.Text.Encoding.UTF8.GetString([.. bytes]);
        }
    }

    /// <summary>A repository or worktree rooted here, including a bare repository.</summary>
    private static bool IsRepository(string directory) =>
        Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"))
        || (File.Exists(Path.Combine(directory, "HEAD")) && Directory.Exists(Path.Combine(directory, "objects")));
}
