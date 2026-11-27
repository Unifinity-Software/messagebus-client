using MessageBus.Attachments.Client;
using System.Text;
using System.Text.Json;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

internal sealed class NodeVerification(string remote, IVerificationProcess process, IRepoCredentialSource? credentials = null, string? privateRoot = null, TimeProvider? time = null)
{
    internal const string Recipe = "node-npm-ci";
    internal const string Enrollment = "node-npm-ci-v1";
    internal const string EnrollmentVariable = "MBUS_RUNNER_VERIFICATION";
    internal const string RemoteVariable = "MBUS_RUNNER_VERIFICATION_REMOTE";
    internal const string Branch = "mbus-verified";
    private const string BranchRef = "refs/heads/" + Branch;
    internal INodeVerificationReadiness? Readiness { get; init; }
    internal bool IsReady => Readiness?.IsReady == true;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan NpmTimeout = TimeSpan.FromMinutes(5);
    private static readonly string[] NpmOptions =
    [
        "--registry=" + NodeVerificationPolicy.Registry, "--userconfig=/tmp/npm-userconfig", "--globalconfig=/tmp/npm-globalconfig",
        "--cache=/tmp/npm-cache", "--script-shell=/bin/sh", "--ignore-scripts",
        "--no-audit", "--no-fund", "--no-update-notifier",
    ];

    internal static async Task<NodeVerification?> EnrollAsync(Func<string, string?> settings, bool linuxAgentIsolation,
        bool canReturnCommits, TimeProvider time, TextWriter log, IRepoCredentialSource credentials, string privateRoot, CancellationToken ct,
        INodeVerificationReadiness? readiness = null)
    {
        if (settings(EnrollmentVariable) is not { Length: > 0 } enrollment) return null;
        if (readiness?.IsReady != true)
        {
            await log.WriteLineAsync("Isolated Node verification is unavailable: shared protected-execution readiness is unsupported or unready.").ConfigureAwait(false);
            return null;
        }
        var approvedRemote = settings(RemoteVariable);
        var process = new VerificationProcess(time);
        try
        {
            if (enrollment == Enrollment && linuxAgentIsolation && canReturnCommits && IsHttpsRemote(approvedRemote)
                && await process.ProbeAsync(ct).ConfigureAwait(false))
            {
                await log.WriteLineAsync("Isolated Node verification is enrolled.").ConfigureAwait(false);
                return new NodeVerification(approvedRemote!, process, credentials, privateRoot, time) { Readiness = readiness };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await log.WriteLineAsync($"The isolated verification preflight failed ({ex.GetType().Name}).").ConfigureAwait(false);
        }
        await log.WriteLineAsync("Isolated Node verification is unavailable. Check the owner settings, agent isolation, and Linux toolchain.").ConfigureAwait(false);
        return null;
    }

    internal string? Refusal(TaskDelivery delivery)
    {
        if (!IsReady) return "Shared protected-execution readiness is unsupported, unready or no longer current.";
        if (delivery.Verification is not { } snapshot || delivery.Envelope.Verification is not { } request
            || snapshot.Recipe != Recipe || snapshot.Remote != remote || string.IsNullOrWhiteSpace(snapshot.Repository)
            || string.IsNullOrWhiteSpace(snapshot.Environment) || string.IsNullOrWhiteSpace(snapshot.Version)
            || string.IsNullOrWhiteSpace(snapshot.Digest) || string.IsNullOrWhiteSpace(snapshot.NodeVersion)
            || string.IsNullOrWhiteSpace(snapshot.NpmVersion)
            || !IsCommit(snapshot.Revision) || !IsCommit(snapshot.BaseRevision)
            || request.Environment != snapshot.Environment || request.RequiredChecks is null || snapshot.RequiredChecks is null
            || !request.RequiredChecks.SequenceEqual(snapshot.RequiredChecks)
            || snapshot.RequiredChecks.Count is < 1 or > 3
            || snapshot.RequiredChecks.Distinct(StringComparer.Ordinal).Count() != snapshot.RequiredChecks.Count
            || !snapshot.RequiredChecks.All(NodeVerificationPolicy.SupportsCheck))
            return "The required verification snapshot is missing or unsupported by this runner.";
        if (delivery.Envelope.Target is InboxTarget || !delivery.Envelope.Type.StartsWith("task.", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(delivery.Role) || delivery.RunEnvironment.PermissionProfile.CanPush
            || (delivery.RunEnvironment.KeepAgentTools?.Count ?? 0) != 0
            || delivery.Envelope.Attachments is not { Count: <= 1 } attachments
            || attachments.Any(attachment => attachment.Format != AttachmentFormat.GitBundle)
            || IsReview(delivery) == delivery.RunEnvironment.PermissionProfile.CanEditFiles)
            return "This verifier supports a single pinned checkout for an editing task or a read-only review.";
        return null;
    }

    internal static bool IsReview(TaskDelivery delivery) => delivery.Envelope.Type == "task.review"
        || delivery.Envelope.Type.StartsWith("task.review.", StringComparison.Ordinal);

    internal async Task<string?> PrepareAsync(string checkout, TaskDelivery delivery, RunWorkspace fresh, CancellationToken ct)
    {
        var snapshot = delivery.Verification!;
        Directory.CreateDirectory(checkout);
        if (!await FetchPinnedAsync(checkout, delivery, ct).ConfigureAwait(false))
            return "revision-unverified";
        var (prefix, reason) = await CheckoutProjectAsync(checkout, snapshot, snapshot.Revision, ct).ConfigureAwait(false);
        if (prefix is null) return reason;
        if (!await ValidateToolchainAsync(snapshot, ct).ConfigureAwait(false))
            return "toolchain-missing";
        return await RestoreAsync(checkout, prefix, CachePath(fresh), maskGit: true, ct).ConfigureAwait(false);
    }

    internal async Task<VerificationResult> VerifyAsync(TaskDelivery delivery, RunWorkspace agent,
        RunWorkspace fresh, CancellationToken ct)
    {
        var snapshot = delivery.Verification!;
        var revision = snapshot.Revision;
        var checkout = fresh.WorkTree;
        Directory.CreateDirectory(checkout);
        if (!await FetchPinnedAsync(checkout, delivery, ct).ConfigureAwait(false))
            return Blocked(snapshot, "revision-unverified");

        if (!IsReview(delivery))
        {
            var candidate = await ImportAgentCommitAsync(agent, fresh, snapshot, ct).ConfigureAwait(false);
            if (candidate is null)
                return Blocked(snapshot, "revision-unverified");
            revision = candidate;
        }

        var (prefix, reason) = await CheckoutProjectAsync(checkout, snapshot, revision, ct).ConfigureAwait(false);
        if (prefix is null) return Blocked(snapshot, reason!);
        if (!await ValidateToolchainAsync(snapshot, ct).ConfigureAwait(false))
            return Blocked(snapshot, "toolchain-missing");

        var checks = new List<VerificationCheck>();
        foreach (var key in snapshot.RequiredChecks)
        {
            // Give each check the same committed tree and its own dependency restore. Check scripts get no
            // repository metadata or network and cannot alter the checkout used to package the returned commit.
            var work = Directory.CreateDirectory(Path.Combine(fresh.Root, "check-" + key)).FullName;
            var restoreReason = await GitOkAsync(checkout, ["checkout-index", "--all", "--prefix=/output/"], ct, output: work).ConfigureAwait(false)
                ? await RestoreAsync(work, prefix, CachePath(fresh), maskGit: false, ct).ConfigureAwait(false)
                : "revision-unverified";
            checks.Add(restoreReason is null
                ? await CheckAsync(work, prefix, key, ct).ConfigureAwait(false)
                : new() { Name = key, State = VerificationState.Blocked, Reason = restoreReason });
        }
        var state = checks.Any(check => check.State == VerificationState.Blocked) ? VerificationState.Blocked
            : checks.Any(check => check.State == VerificationState.Failed) ? VerificationState.Failed : VerificationState.Passed;
        return new VerificationResult { Environment = snapshot.Environment, Revision = revision, State = state, Checks = checks };
    }

    internal static VerificationResult Blocked(VerificationSnapshot snapshot, string reason) =>
        new() { Environment = snapshot.Environment, State = VerificationState.Blocked, Reason = reason };

    private async Task<string?> ImportAgentCommitAsync(RunWorkspace agent, RunWorkspace fresh, VerificationSnapshot snapshot, CancellationToken ct)
    {
        var gitDirectory = Path.Combine(agent.WorkTree, ".git");
        if (!Directory.Exists(gitDirectory) || !MessageBus.Mcp.RunToolScope.IsInside(agent.Root, gitDirectory)
            || new DirectoryInfo(agent.Root).LinkTarget is not null)
            return null;

        // Import via a bundle across two separate sandboxes. Git reading agent metadata gets only its
        // read-only object store and an empty export directory, never the runner's repository.
        var exchange = Directory.CreateDirectory(Path.Combine(fresh.Root, "export")).FullName;
        if (!await GitOkAsync(null, ["--git-dir=/input", "bundle", "create", "/output/result.bundle",
                snapshot.Revision + ".." + BranchRef], ct, input: gitDirectory, output: exchange).ConfigureAwait(false))
            return null;
        var bundle = new FileInfo(Path.Combine(exchange, "result.bundle"));
        if (!bundle.Exists || bundle.LinkTarget is not null) return null;
        try
        {
            await GitBundles.ApplyAsync(bundle.FullName, "the verification working copy", [BranchRef + ":refs/mbus/result"], async (args, token) =>
            {
                var importing = args.Contains(bundle.FullName, StringComparer.Ordinal);
                var command = args.Select(arg => arg == bundle.FullName ? "/input" : arg).ToArray();
                var run = await GitAsync(fresh.WorkTree, command, token, input: importing ? bundle.FullName : null).ConfigureAwait(false);
                if (run.ExitCode != 0) throw new GitException("Git could not import the verification bundle.", rawOutput: true);
                return run.Output;
            }, writeFetchHead: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GitException or IOException)
        {
            return null;
        }

        var result = await GitAsync(fresh.WorkTree, ["rev-parse", "--verify", "refs/mbus/result^{commit}"], ct).ConfigureAwait(false);
        var revision = result.Output.Trim();
        if (result.ExitCode != 0 || !IsCommit(revision) || revision == snapshot.Revision
            || !await GitOkAsync(fresh.WorkTree, ["merge-base", "--is-ancestor", snapshot.Revision, revision], ct).ConfigureAwait(false))
            return null;
        var merges = await GitAsync(fresh.WorkTree, ["rev-list", "--count", "--merges", snapshot.Revision + ".." + revision], ct).ConfigureAwait(false);
        return merges is { ExitCode: 0 } && merges.Output.Trim() == "0" ? revision : null;
    }

    private async Task<bool> FetchPinnedAsync(string checkout, TaskDelivery delivery, CancellationToken ct)
    {
        var snapshot = delivery.Verification!;
        if (snapshot.Remote != remote || !IsHttpsRemote(remote)
            || !IsCommit(snapshot.Revision) || !IsCommit(snapshot.BaseRevision)
            || !await GitOkAsync(checkout, ["init", "--template=", "--initial-branch=" + Branch], ct).ConfigureAwait(false))
            return false;

        string[] fetch = ["fetch", "--no-tags", "--no-recurse-submodules", "--", remote, snapshot.Revision, snapshot.BaseRevision];
        if (!await GitOkAsync(checkout, fetch, ct, network: true, timeout: FetchTimeout).ConfigureAwait(false)
            && !await FetchWithCredentialAsync(checkout, delivery, fetch, ct).ConfigureAwait(false))
            return false;
        return await GitOkAsync(checkout, ["merge-base", "--is-ancestor", snapshot.BaseRevision, snapshot.Revision], ct).ConfigureAwait(false);
    }

    private async Task<bool> FetchWithCredentialAsync(string checkout, TaskDelivery delivery, string[] fetch, CancellationToken ct)
    {
        if (credentials is null || privateRoot is null || OperatingSystem.IsWindows()) return false;
        var credential = await credentials.IssueAsync(new RepoCredentialRequest
        {
            DeliveryId = delivery.DeliveryId,
            WorkspaceId = delivery.Envelope.WorkspaceId,
            Repository = new Uri(remote),
            CanPush = false,
        }, ct).ConfigureAwait(false);
        if (credential is null || credential.ExpiresAt <= (time ?? TimeProvider.System).GetUtcNow()
            || string.IsNullOrWhiteSpace(credential.UserName) || credential.UserName.Contains(':')
            || credential.UserName.Any(char.IsControl) || string.IsNullOrWhiteSpace(credential.Secret)
            || credential.Secret.Any(char.IsControl))
            return false;

        // Linux agent mounts hide the runner work root and expose only the agent's own run directory.
        // Keep this temporary config outside all run directories and mount it for this fetch alone.
        var directory = Directory.CreateDirectory(Path.Combine(privateRoot, ".verification-git-" + Guid.NewGuid().ToString("N")),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var config = Path.Combine(directory.FullName, "fetch.config");
        try
        {
            var authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.UserName + ":" + credential.Secret));
            using (var writer = new StreamWriter(new FileStream(config, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            })))
            {
                await writer.WriteAsync($"[http \"{remote}\"]\n\textraHeader = Authorization: Basic {authorization}\n").ConfigureAwait(false);
            }
            return await GitOkAsync(checkout, ["-c", "include.path=/input", .. fetch], ct,
                network: true, input: config, timeout: FetchTimeout).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(config);
            directory.Delete();
        }
    }

    private async Task<bool> CheckoutAsync(string checkout, string revision, CancellationToken ct)
    {
        var tree = await GitAsync(checkout, ["ls-tree", "-r", "--format=%(objectmode)", revision], ct).ConfigureAwait(false);
        if (tree.ExitCode != 0 || tree.Output.Length == 0
            || tree.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(mode => mode is not ("100644" or "100755")))
            return false;

        var head = await GitAsync(checkout, ["rev-parse", "--verify", revision + "^{commit}"], ct).ConfigureAwait(false);
        return head.ExitCode == 0 && head.Output.Trim() == revision
            && await GitOkAsync(checkout, ["checkout", "--force", "-B", Branch, revision], ct).ConfigureAwait(false);
    }

    private static string CachePath(RunWorkspace fresh) => Path.Combine(fresh.Root, "npm-cache");

    private async Task<(string? Prefix, string? Reason)> CheckoutProjectAsync(
        string checkout, VerificationSnapshot snapshot, string revision, CancellationToken ct)
    {
        // Attributes can transform the checkout without changing the manifest or lockfile blobs.
        var diff = await GitAsync(checkout, ["diff", "--quiet", "--no-ext-diff", "--no-textconv",
            snapshot.BaseRevision, revision, "--", "package.json", "package-lock.json",
            "src/client/package.json", "src/client/package-lock.json", ":(glob)**/.gitattributes"], ct).ConfigureAwait(false);
        if (diff.ExitCode == 1) return (null, "dependency-policy-changed");
        if (diff.ExitCode != 0 || !await CheckoutAsync(checkout, revision, ct).ConfigureAwait(false))
            return (null, "revision-unverified");

        try
        {
            var prefix = NodeVerificationPolicy.ProjectPrefix(checkout, snapshot.RequiredChecks);
            if (prefix is not null) return (prefix, null);
        }
        catch (JsonException) { }
        return (null, "revision-unverified");
    }

    private async Task<bool> ValidateToolchainAsync(VerificationSnapshot snapshot, CancellationToken ct)
    {
        var node = await process.RunAsync(null, VerificationProcess.Node, ["--version"], ct).ConfigureAwait(false);
        var npm = await process.RunAsync(null, VerificationProcess.Npm, ["--version"], ct).ConfigureAwait(false);
        return node.ExitCode == 0 && npm.ExitCode == 0 && node.Output.Trim().TrimStart('v') == snapshot.NodeVersion.TrimStart('v')
            && npm.Output.Trim() == snapshot.NpmVersion;
    }

    internal async Task<string?> RestoreAsync(string work, string prefix, string cache, bool maskGit, CancellationToken ct)
    {
        // The cache lives in the runner-only workspace. npm ci checks the lockfile integrity on
        // cache hits and downloads, and lifecycle scripts are disabled during this writable mount.
        Directory.CreateDirectory(cache);
        var result = await process.RunAsync(work, VerificationProcess.Npm,
            [.. NpmOptions, "--prefix=" + prefix, "--prefer-offline", "ci", "--include=dev"],
            ct, network: true, maskGit: maskGit, timeout: NpmTimeout, npmCache: cache).ConfigureAwait(false);
        return result.ExitCode == 0 ? null : result.TimedOut ? "timed-out" : "restore-failed";
    }

    internal async Task<VerificationCheck> CheckAsync(string work, string prefix, string key, CancellationToken ct)
    {
        if (!NodeVerificationPolicy.SupportsCheck(key)) throw new ArgumentException("Unsupported check.", nameof(key));
        // Reserve exit codes for commands that start. The shell's 126/127 codes can also occur
        // when a script cannot start one of its own commands, so treat those as inconclusive.
        var result = await process.RunAsync(work, "/bin/sh",
            ["-c", """ "$@"; case $? in 0) exit 42;; 126|127) exit 44;; *) exit 43;; esac """,
             "mbus-check", VerificationProcess.Npm, .. NpmOptions, "--prefix=" + prefix, "run-script", key],
            ct, timeout: NpmTimeout).ConfigureAwait(false);
        return result.ExitCode switch
        {
            42 => new() { Name = key, State = VerificationState.Passed },
            43 => new() { Name = key, State = VerificationState.Failed, Reason = "check-failed" },
            _ => new() { Name = key, State = VerificationState.Blocked, Reason = result.TimedOut ? "timed-out" : "check-unavailable" },
        };
    }

    private Task<VerificationProcess.CommandResult> GitAsync(string? checkout, string[] args, CancellationToken ct,
        bool network = false, string? input = null, string? output = null, TimeSpan? timeout = null) =>
        process.RunAsync(checkout, VerificationProcess.Git,
            ["-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "-c", "http.followRedirects=false",
             "-c", "fetch.fsckObjects=true", "-c", "submodule.recurse=false", .. args], ct, network, input: input, output: output, timeout: timeout);

    private async Task<bool> GitOkAsync(string? checkout, string[] args, CancellationToken ct, bool network = false,
        string? input = null, string? output = null, TimeSpan? timeout = null) =>
        (await GitAsync(checkout, args, ct, network, input, output, timeout).ConfigureAwait(false)).ExitCode == 0;

    private static bool IsCommit(string? value) => value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsHttpsRemote(string? remote) =>
        remote is not null && remote.StartsWith("https://", StringComparison.Ordinal)
        && !remote.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\\' or '%' or '@' or '"')
        && Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 && uri.IsDefaultPort
        && uri.HostNameType == UriHostNameType.Dns && uri.AbsolutePath.Length > 1 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
