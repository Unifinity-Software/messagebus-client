using MessageBus.Attachments.Client;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

/// <summary>
/// the original issue: a bundle-mode task's code goes through the runner's copy of the repository, so later tasks need only their new
/// commits; a runner without the earlier ones asks the sender. One source repository and its bundles for the class.
/// </summary>
public sealed class BundleCopyTests(BundleCopyTests.Code code) : IClassFixture<BundleCopyTests.Code>
{
    /// <summary>
    /// main: A, B, C. other: B then S (another sender's work). Bundles: the whole of main at B; other after B; main after B
    /// (C only); the whole of main at C (what the sender sends when asked).
    /// </summary>
    public sealed class Code : IAsyncLifetime
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mbus-bundle-copies", Guid.NewGuid().ToString("N"));

        public string? Git { get; } = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);

        public string Whole { get; private set; } = "";
        public string Other { get; private set; } = "";
        public string Next { get; private set; } = "";
        public string History { get; private set; } = "";
        public string Secret { get; private set; } = "";
        public string C { get; private set; } = "";
        public string Remote => Path.Combine(Root, "origin.git");
        public string? LocalFetchUnavailable { get; private set; }

        public async ValueTask InitializeAsync()
        {
            if (Git is null) return;
            var repo = Directory.CreateDirectory(Path.Combine(Root, "sender")).FullName;
            await MessageBus.Attachments.Client.Git.RunAsync(repo, ["init", "--quiet", "--initial-branch=main"]);
            await CommitAsync(repo, "one");
            await CommitAsync(repo, "two");
            var b = await MessageBus.Attachments.Client.Git.CommitIdAsync(repo, "HEAD");
            Whole = (await GitBundles.CreateAsync(repo, Path.Combine(Root, "whole.bundle"), "main")).Path;
            await MessageBus.Attachments.Client.Git.RunAsync(repo, ["checkout", "--quiet", "-b", "other"]);
            await File.WriteAllTextAsync(Path.Combine(repo, "secret.txt"), "not for main's tasks");
            Secret = await CommitAsync(repo, "secret");
            Other = (await GitBundles.CreateAsync(repo, Path.Combine(Root, "other.bundle"), "other", b)).Path;
            await MessageBus.Attachments.Client.Git.RunAsync(repo, ["checkout", "--quiet", "main"]);
            C = await CommitAsync(repo, "three");
            Next = (await GitBundles.CreateAsync(repo, Path.Combine(Root, "next.bundle"), "main", b)).Path;
            History = (await GitBundles.CreateAsync(repo, Path.Combine(Root, "history.bundle"), "main")).Path;
            await MessageBus.Attachments.Client.Git.RunAsync(repo, ["clone", "--quiet", "--bare", repo, Remote]);

            LocalFetchUnavailable = await WindowsRunCapabilities.GitShellUnavailableAsync();
        }

        public void RequireLocalFetch()
        {
            Assert.SkipWhen(Git is null, "git isn't installed.");
            Assert.SkipWhen(LocalFetchUnavailable is not null, LocalFetchUnavailable ?? "");
        }

        private static async Task<string> CommitAsync(string repo, string text)
        {
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), text);
            await MessageBus.Attachments.Client.Git.RunAsync(repo, ["add", "."]);
            await MessageBus.Attachments.Client.Git.RunAsync(repo, ["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", text]);
            return await MessageBus.Attachments.Client.Git.CommitIdAsync(repo, "HEAD");
        }

        public ValueTask DisposeAsync()
        {
            RunWorkspace.TryDelete(Root);
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("missing-revision", false)]
    public async Task Bundle_checkout_without_an_attachment_uses_the_referenced_repository(string? revision, bool missingRemote)
    {
        code.RequireLocalFetch();
        var ct = TestContext.Current.CancellationToken;
        var delivery = Delivery("dlv_no_bundle", "main");
        var remote = missingRemote ? Path.Combine(code.Root, "missing.git") : code.Remote;
        const string url = "https://git.example.test/team/app.git";
        delivery = delivery with
        {
            Envelope = delivery.Envelope with
            {
                Attachments = [],
                Refs = [new Ref { Kind = "git.repository", Url = url, Sha = revision }],
            },
            StepContext = new StepContext { JobId = "job_test", StepId = "code", StepName = "Code", Round = 1, AllowedOutcomes = [], Inputs = [] },
        };
        using var run = RunWorkspace.Create(code.Root, delivery.DeliveryId, 1);
        var log = new List<string>();
        run.GitLog = log.Add;

        var error = await run.CheckOutAsync(delivery, _ => code.Git, bundles: null, ct,
            launch => TestRepositoryRemote.Map(launch, url, remote));

        Assert.True(error is null, error);
        if (missingRemote || revision is not null)
        {
            Assert.True(Directory.Exists(Path.Combine(run.WorkTree, ".git")));
            Assert.Empty((await Git.RunAsync(run.WorkTree, ["for-each-ref"], ct)).Trim());
            Assert.False(File.Exists(Path.Combine(run.WorkTree, "README.md")));
            Assert.Contains("Starting with an empty repository", Assert.Single(log));
        }
        else
        {
            Assert.Equal("main", (await Git.RunAsync(run.WorkTree, ["branch", "--show-current"], ct)).Trim());
            Assert.Equal(code.C, await Git.CommitIdAsync(run.WorkTree, "HEAD", ct));
        }
    }

    [Theory]
    [InlineData("/srv/private/repo.git")]
    [InlineData("../private/repo.git")]
    [InlineData("./repo")]
    [InlineData("repo.git")]
    [InlineData("C:\\private\\repo.git")]
    [InlineData("C:repo.git")]
    [InlineData("\\\\files.example.test\\private\\repo.git")]
    [InlineData("//files.example.test/private/repo.git")]
    [InlineData("file:///srv/private/repo.git")]
    [InlineData("http://git.example.test/team/repo.git")]
    [InlineData("git://git.example.test/team/repo.git")]
    [InlineData("ext::command.git")]
    public async Task Checkout_refuses_local_and_unsupported_repository_addresses_before_starting_git(string address)
    {
        using var run = RunWorkspace.Create(code.Root, "dlv_refused", 1);
        var task = Delivery("dlv_refused", "main");
        task = task with
        {
            RunEnvironment = new RunEnvironment { Checkout = CheckoutMode.Clone },
            Envelope = task.Envelope with { Refs = [new Ref { Kind = "git.repository", Url = address }] },
        };
        var error = await run.CheckOutAsync(task, _ => "git", null, TestContext.Current.CancellationToken,
            _ => throw new InvalidOperationException("A refused address must never start git."));

        Assert.Equal("The repository address must use HTTPS or SSH. Local folders and file addresses aren't allowed.", error);
        Assert.False(Directory.Exists(run.WorkTree));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bundle_checkout_never_copies_a_task_supplied_local_repository(bool incremental)
    {
        code.RequireLocalFetch();
        var task = Delivery("dlv_local", "main");
        task = task with
        {
            Envelope = task.Envelope with
            {
                Attachments = incremental ? task.Envelope.Attachments : [],
                Refs = [new Ref { Kind = "git.repo", Url = code.Remote }],
            },
        };
        using var run = RunWorkspace.Create(Path.Combine(code.Root, Guid.NewGuid().ToString("N")), "dlv_local", 1);
        var log = new List<string>();
        run.GitLog = log.Add;
        var error = await run.CheckOutAsync(task, _ => code.Git, new Bundles { File = code.Next }, TestContext.Current.CancellationToken,
            launch =>
            {
                Assert.DoesNotContain("clone", launch.Arguments);
                return launch;
            });

        Assert.Contains("Local folders and file addresses aren't allowed", error);
        Assert.True(run.RepositoryRefused);
        Assert.False(File.Exists(Path.Combine(run.WorkTree, "README.md")));
    }

    [Theory]
    [InlineData(CheckoutMode.Clone, false)]
    [InlineData(CheckoutMode.Bundle, false)]
    [InlineData(CheckoutMode.Bundle, true)]
    public async Task Checkout_clone_uses_the_prepared_runs_credential(CheckoutMode mode, bool incremental)
    {
        code.RequireLocalFetch();
        var ct = TestContext.Current.CancellationToken;
        const string url = "https://git.example.test/team/private.git";
        var root = Path.Combine(code.Root, Guid.NewGuid().ToString("N"));
        using var run = RunWorkspace.Create(root, "dlv_credential", 1);
        var task = Delivery("dlv_credential", "main");
        task = task with
        {
            RunEnvironment = new RunEnvironment { Checkout = mode },
            Envelope = task.Envelope with
            {
                Attachments = incremental ? task.Envelope.Attachments : [],
                Refs = [new Ref { Kind = "git.repo", Url = "../private/repo.git" }, new Ref { Kind = "git.repository", Url = url }],
            },
        };
        var isolation = RunIsolation.Create(new RunIsolationOptions
        {
            WorkRoot = root, HomeDirectory = root, Requested = IsolationLevel.Basic, Credentials = new CheckoutCredentials(),
        }, TextWriter.Null);
        var profile = new PermissionProfile { Name = "worker" };
        await isolation.PrepareAsync(run, task, profile, ct);
        ProcessLaunch? clone = null;
        try
        {
            var error = await run.CheckOutAsync(task, _ => code.Git, new Bundles { File = code.Next }, ct, launch =>
            {
                launch = isolation.Wrap(launch, run, profile);
                if (launch.Arguments.Contains("clone")) clone = launch;
                return TestRepositoryRemote.Map(launch, url, code.Remote);
            });
            Assert.True(error is null, error);
            Assert.NotNull(clone);
            Assert.Equal(Path.Combine(run.Root, RunGitConfig.ConfigFileName), clone.Environment["GIT_CONFIG_GLOBAL"]);
            Assert.Contains(RunGitConfig.SshWrapperFileName, clone.Environment["GIT_SSH_COMMAND"]);
            Assert.Equal("0", clone.Environment["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("never", clone.Environment["GCM_INTERACTIVE"]);
            Assert.Equal("https:ssh", clone.Environment["GIT_ALLOW_PROTOCOL"]);
            // Ask Git itself using exactly the clone's environment: only the run helper can answer.
            var lines = new List<string>();
            using var credential = AgentProcess.Start(clone with
            {
                Arguments = ["credential", "fill"],
                StandardInput = "protocol=https\nhost=git.example.test\npath=team/private.git\n\n",
            }, lines.Add);
            Assert.Equal(0, await credential.WaitForExitAsync());
            Assert.Contains("username=run-reader", lines);
            Assert.Contains("password=example-run-password", lines);
            Assert.Equal(code.C, await Git.CommitIdAsync(run.WorkTree, "HEAD", ct));
        }
        finally
        {
            isolation.Ended(run);
        }
    }

    private sealed class CheckoutCredentials : IRepoCredentialSource
    {
        public string Description => "Test checkout credential";
        public Task<RepoCredential?> IssueAsync(RepoCredentialRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<RepoCredential?>(new RepoCredential
            {
                UserName = "run-reader", Secret = "example-run-password", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Step_bundle_base_fetch_uses_run_credentials_and_refuses_an_owner_rewrite_to_a_local_folder(bool localRewrite)
    {
        code.RequireLocalFetch();
        var ct = TestContext.Current.CancellationToken;
        const string url = "https://git.example.test/team/private.git";
        var root = Directory.CreateDirectory(Path.Combine(code.Root, Guid.NewGuid().ToString("N"))).FullName;
        if (localRewrite)
            await Git.RunAsync(root, ["config", "--file", Path.Combine(root, ".gitconfig"),
                $"url.{code.Remote.Replace('\\', '/')}.insteadOf", url], ct);
        using var run = RunWorkspace.Create(root, "dlv_step_base", 1);
        Directory.CreateDirectory(run.WorkTree);
        await Git.RunAsync(run.WorkTree, ["init", "--quiet"], ct);
        await Git.RunAsync(run.WorkTree, ["-c", "user.name=Test", "-c", "user.email=test@example.test", "-c", "commit.gpgsign=false",
            "commit", "--quiet", "--allow-empty", "-m", "Existing step"], ct);
        await Git.RunAsync(run.WorkTree, ["remote", "add", "origin", url], ct);
        var task = Delivery("dlv_step_base", "main");
        task = task with { Envelope = task.Envelope with { Refs = [new Ref { Kind = "git.repo", Url = url }] } };
        var profile = new PermissionProfile { Name = "worker" };
        var isolation = RunIsolation.Create(new RunIsolationOptions
        {
            WorkRoot = root, HomeDirectory = root, Requested = IsolationLevel.Basic,
            Credentials = new CheckoutCredentials(), UseOwnerGit = _ => localRewrite,
        }, TextWriter.Null);
        await isolation.PrepareAsync(run, task, profile, ct);
        var fetches = new List<ProcessLaunch>();
        try
        {
            var error = await run.ImportStepBranchAsync(code.Git!, task, code.Next, GitBundles.ReadHeader(code.Next), "main", launch =>
            {
                launch = isolation.Wrap(launch, run, profile);
                if (launch.Arguments.Contains("fetch")) fetches.Add(launch);
                return localRewrite ? launch : TestRepositoryRemote.Map(launch, url, code.Remote);
            }, ct);
            Assert.NotEmpty(fetches);
            foreach (var fetch in fetches)
            {
                Assert.Equal(Path.Combine(run.Root, RunGitConfig.ConfigFileName), fetch.Environment["GIT_CONFIG_GLOBAL"]);
                Assert.Equal("never", fetch.Environment["GCM_INTERACTIVE"]);
                if (fetch.Arguments.Contains("origin"))
                    Assert.Equal("https:ssh", fetch.Environment["GIT_ALLOW_PROTOCOL"]);
                else
                    Assert.False(fetch.Environment.TryGetValue("GIT_ALLOW_PROTOCOL", out var allowed) && allowed == "https:ssh");
            }
            Assert.Contains(fetches, f => f.Arguments.Contains("origin"));
            if (localRewrite)
            {
                Assert.Contains("needs commits missing", error);
                Assert.DoesNotContain(fetches, f => f.Arguments.Contains(Path.GetFullPath(code.Next)));
                await Assert.ThrowsAsync<GitException>(() => Git.CommitIdAsync(run.WorkTree, code.C, ct));
            }
            else
            {
                Assert.True(error is null, error);
                Assert.Contains(fetches, f => f.Arguments.Contains(Path.GetFullPath(code.Next)));
                Assert.Equal(code.C, await Git.CommitIdAsync(run.WorkTree, "HEAD", ct));
            }
        }
        finally { isolation.Ended(run); }
    }

    [Fact]
    public async Task A_shallow_full_step_bundle_never_truncates_an_existing_repository()
    {
        code.RequireLocalFetch();
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateDirectory(Path.Combine(code.Root, Guid.NewGuid().ToString("N"))).FullName;
        var producer = Path.Combine(root, "producer");
        await Git.RunAsync(root, ["clone", "--quiet", "--depth=1", new Uri(code.Remote).AbsoluteUri, producer], ct);
        await Git.RunAsync(producer, ["switch", "--quiet", "-c", "feature/shallow"], ct);
        await Git.RunAsync(producer, ["-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "--quiet", "--allow-empty", "-m", "Code step"], ct);
        var bundle = await GitBundles.CreateAsync(producer, Path.Combine(root, "code.bundle"), baseCommit: "", ct: ct);
        Assert.Empty(bundle.Prerequisites);

        using var run = RunWorkspace.Create(root, "dlv_existing", 1);
        Directory.CreateDirectory(run.WorkTree);
        await Git.RunAsync(run.WorkTree, ["init", "--quiet"], ct);
        await Git.RunAsync(run.WorkTree, ["-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "--quiet", "--allow-empty", "-m", "Existing step"], ct);
        var existing = await Git.CommitIdAsync(run.WorkTree, "HEAD", ct);

        var error = await run.ImportStepBranchAsync(code.Git!, Delivery("dlv_existing", bundle.Branch), bundle.Path,
            GitBundles.ReadHeader(bundle.Path), bundle.Branch, launch => launch, ct);

        Assert.Contains("did not send all necessary objects", error);
        Assert.Equal(existing, await Git.CommitIdAsync(run.WorkTree, "HEAD", ct));
        Assert.False(File.Exists(Path.Combine(run.WorkTree, ".git", "shallow")));
        Assert.Equal("false", (await Git.RunAsync(run.WorkTree, ["rev-parse", "--is-shallow-repository"], ct)).Trim());
    }

    [Fact]
    public async Task Step_bundle_error_keeps_the_repository_refusal_reason()
    {
        code.RequireLocalFetch();
        using var run = RunWorkspace.Create(code.Root, "dlv_step_refused", 1);
        var task = Delivery("dlv_step_refused", "main");
        task = task with { Envelope = task.Envelope with { Refs = [new Ref { Kind = "git.repo", Url = "../private/repo.git" }] } };
        var error = await run.ImportStepBranchAsync(code.Git!, task, code.Next, GitBundles.ReadHeader(code.Next), "main", l => l,
            TestContext.Current.CancellationToken);
        Assert.Contains("Local folders and file addresses aren't allowed", error);
        Assert.Contains("needs commits missing", error);
    }

    [Fact]
    public async Task A_service_without_a_repository_credential_explains_why_the_checkout_could_not_authenticate()
    {
        code.RequireLocalFetch();
        var ct = TestContext.Current.CancellationToken;
        var log = new StringWriter();
        using var server = new FakeServer().On(HttpMethod.Post, ServiceRoutes.RepoCredential,
            _ => new(System.Net.HttpStatusCode.NoContent));
        using var http = new HttpClient(server) { BaseAddress = new Uri(FakeServer.Url) };
        var root = Path.Combine(code.Root, Guid.NewGuid().ToString("N"));
        using var run = RunWorkspace.Create(root, "dlv_no_credential", 1);
        run.GitLog = log.WriteLine;
        const string url = "https://git.example.test/team/private.git";
        var task = Delivery("dlv_no_credential", "main");
        task = task with { Envelope = task.Envelope with { Attachments = [], Refs = [new Ref { Kind = "git.repo", Url = url }] } };
        var profile = new PermissionProfile { Name = "worker" };
        var isolation = RunIsolation.Create(new RunIsolationOptions
        {
            WorkRoot = root, HomeDirectory = root, Requested = IsolationLevel.Basic,
            Credentials = new ServiceRepoCredentials(http, log),
        }, log);
        await isolation.PrepareAsync(run, task, profile, ct);
        try
        {
            Assert.Null(await run.CheckOutAsync(task, _ => code.Git, null, ct,
                launch => TestRepositoryRemote.Map(isolation.Wrap(launch, run, profile), url, Path.Combine(root, "missing.git"))));
            Assert.Contains("the service supplied none (204)", log.ToString());
            Assert.Contains("No repository credential was available for this run", log.ToString());
            Assert.False(File.Exists(Path.Combine(run.WorkTree, "README.md")));
        }
        finally { isolation.Ended(run); }
    }

    [Fact]
    public async Task Checkout_does_not_run_an_inherited_hook()
    {
        code.RequireLocalFetch();
        var root = Directory.CreateDirectory(Path.Combine(code.Root, "h" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var hooks = Directory.CreateDirectory(Path.Combine(root, "hooks")).FullName;
        var hook = Path.Combine(hooks, "post-checkout");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\n: > hook-ran\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var config = Path.Combine(root, "gitconfig");
        await Git.RunAsync(root, ["config", "--file", config, "core.hooksPath", hooks.Replace('\\', '/')]);
        using var run = RunWorkspace.Create(root, "dlv_hooks", 1);
        var source = new Bundles { File = code.Whole };
        var checkoutError = await run.CheckOutAsync(Delivery("dlv_hooks", "main"), _ => code.Git, source,
            TestContext.Current.CancellationToken, launch => launch with
            {
                Environment = new Dictionary<string, string?>(launch.Environment) { ["GIT_CONFIG_GLOBAL"] = config },
            });
        Assert.True(checkoutError is null, checkoutError);
        Assert.True(File.Exists(Path.Combine(run.WorkTree, "README.md")));
        Assert.False(File.Exists(Path.Combine(run.WorkTree, "hook-ran")));
    }

    [Fact]
    public void Checkout_removes_only_its_own_old_index_lock_and_logs_it()
    {
        using var run = RunWorkspace.Create(code.Root, "dlv_lock_" + Guid.NewGuid().ToString("N"), 1);
        Directory.CreateDirectory(Path.Combine(run.WorkTree, ".git"));
        var path = Path.Combine(run.WorkTree, ".git", "index.lock");
        File.WriteAllText(path, "");
        var finished = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, finished.AddMinutes(-1));
        var messages = new List<string>();
        run.GitLog = messages.Add;

        run.RemoveOwnedIndexLock(true, finished);
        Assert.True(File.Exists(path)); // somebody else's lock, however old
        run.RemoveOwnedIndexLock(false, finished.AddMinutes(-2));
        Assert.True(File.Exists(path)); // newer than the completed steps
        using var resumed = RunWorkspace.Create(code.Root, "dlv_resumed_lock", 1, sessionFolder: run.WorkTree);
        resumed.RemoveOwnedIndexLock(false, finished);
        Assert.True(File.Exists(path)); // resumed sessions never clean up their working folder
        Assert.Empty(messages);
        run.RemoveOwnedIndexLock(false, finished);
        Assert.False(File.Exists(path));
        Assert.Single(messages);
    }

    [Fact]
    public async Task Empty_base_sends_whole_history()
    {
        Assert.SkipWhen(code.Git is null, "git isn't installed.");
        var repo = Path.Combine(code.Root, "sender");
        var bundle = await GitBundles.CreateAsync(repo, Path.Combine(code.Root, "empty-base.bundle"), "main", "", TestContext.Current.CancellationToken);
        Assert.Null(bundle.BaseCommit);
        Assert.Empty(GitBundles.ReadHeader(bundle.Path).Prerequisites);
        var receiver = Path.Combine(code.Root, "empty-base-receiver");
        Assert.Equal(code.C, await GitBundles.FetchAsync(bundle.Path, receiver, "main", ct: TestContext.Current.CancellationToken));
        Assert.Equal("3", (await Git.RunAsync(receiver, ["rev-list", "--count", "refs/remotes/mbus/main"], TestContext.Current.CancellationToken)).Trim());
    }

    [Theory]
    [InlineData("mbus/retourné", "")]
    [InlineData("refs/remotes/mbus/retourné", "refs/heads/main")]
    public async Task Remote_tracking_branch_is_sent_as_a_local_branch(string branch, string baseCommit)
    {
        Assert.SkipWhen(code.Git is null, "git isn't installed.");
        var receiver = Path.Combine(code.Root, "remote-" + Guid.NewGuid().ToString("N"));
        await GitBundles.FetchAsync(code.History, receiver, "main", "refs/remotes/mbus/retourné", TestContext.Current.CancellationToken);
        var bundle = await GitBundles.CreateAsync(receiver, Path.Combine(receiver, "returned.bundle"), branch, GitBundles.ReadHeader(code.Whole).Refs.GetValueOrDefault(baseCommit, baseCommit), TestContext.Current.CancellationToken);
        Assert.Equal("retourné", bundle.Branch);
        Assert.Equal(code.C, Assert.Single(GitBundles.ReadHeader(bundle.Path).Refs).Value);
        var final = Path.Combine(receiver, "final");
        await GitBundles.FetchAsync(code.Whole, final, "main", ct: TestContext.Current.CancellationToken);
        Assert.Equal(code.C, await GitBundles.FetchAsync(bundle.Path, final, "retourné", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Behind_fetched_copy_notice_explains_how_to_send_new_code()
    {
        Assert.SkipWhen(code.Git is null, "git isn't installed.");
        var repo = Path.Combine(code.Root, "notice-receiver");
        await GitBundles.FetchAsync(code.Whole, repo, "main", ct: TestContext.Current.CancellationToken);
        await Git.RunAsync(repo, ["update-ref", "refs/heads/main", GitBundles.ReadHeader(code.Whole).Refs["refs/heads/main"]], TestContext.Current.CancellationToken);
        await GitBundles.FetchAsync(code.History, repo, "main", ct: TestContext.Current.CancellationToken);
        var source = await GitBundles.ResolveBranchAsync(repo, "main", TestContext.Current.CancellationToken);
        var notice = await GitBundles.BehindFetchedNoticeAsync(repo, source, TestContext.Current.CancellationToken);
        Assert.Contains("behind refs/remotes/mbus/main", notice);
        Assert.Contains("mbus/main or update the local branch", notice);
        Assert.Null(await GitBundles.BehindFetchedNoticeAsync(repo, "refs/remotes/mbus/main", TestContext.Current.CancellationToken));
        await Git.RunAsync(repo, ["update-ref", "refs/heads/main", code.C], TestContext.Current.CancellationToken);
        Assert.Null(await GitBundles.BehindFetchedNoticeAsync(repo, source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Parent_commit_notice_checks_ancestry_including_divergent_branches_and_missing_objects()
    {
        Assert.SkipWhen(code.Git is null, "git isn't installed.");
        var repo = Path.Combine(code.Root, "sender");
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await GitBundles.ParentCommitNoticeAsync(repo, code.C, code.C, "main", ct));
        var b = GitBundles.ReadHeader(code.Whole).Refs["refs/heads/main"];
        Assert.Null(await GitBundles.ParentCommitNoticeAsync(repo, code.C, b, "main", ct));
        var notice = await GitBundles.ParentCommitNoticeAsync(repo, code.C, code.Secret, "other", ct);
        Assert.Contains(code.C, notice);
        Assert.Contains(code.Secret, notice);
        Assert.Contains("mbus/other", notice);
        Assert.Contains("fetch it first", await GitBundles.ParentCommitNoticeAsync(repo, code.C, new string('0', 40), "main", ct));
    }

    [Fact]
    public async Task Later_tasks_check_out_from_the_runners_copy_and_each_holds_only_the_history_it_was_sent()
    {
        code.RequireLocalFetch();
        var work = Path.Combine(code.Root, "runs-" + Guid.NewGuid().ToString("N")[..8]);
        var source = new Bundles();

        // The whole history: checked out, and kept in a copy for the workspace.
        source.File = code.Whole;
        var checkoutError = await CheckOutAsync(work, "dlv_first", "main", source);
        Assert.True(checkoutError is null, checkoutError);
        var copy = Assert.Single(Directory.GetDirectories(Path.Combine(work, ".mirrors"), "code-*.git"));

        // Another sender's branch, after a commit the copy has, puts S in the copy.
        source.File = code.Other;
        checkoutError = await CheckOutAsync(work, "dlv_other", "other", source);
        Assert.True(checkoutError is null, checkoutError);

        // main after B: only C travelled, the rest came from the copy, and nothing of the other branch did.
        source.File = code.Next;
        using var next = RunWorkspace.Create(work, "dlv_next", 1);
        var delivery = Delivery("dlv_next", "main");
        delivery = delivery with
        {
            Envelope = delivery.Envelope with { Refs = [new Ref { Kind = "git.repo", Url = Path.Combine(work, "unavailable.git") }] },
        };
        checkoutError = await next.CheckOutAsync(delivery, _ => code.Git, source, TestContext.Current.CancellationToken,
            wrap: launch =>
            {
                Assert.DoesNotContain("clone", launch.Arguments);
                return launch;
            }, ask: source.AskAsync);
        Assert.True(checkoutError is null, checkoutError);
        Assert.Equal(0, source.Asked);
        Assert.Equal("three", File.ReadAllText(Path.Combine(next.WorkTree, "README.md")));
        Assert.Equal(code.C, await Git.CommitIdAsync(next.WorkTree, "HEAD"));
        Assert.Equal("main", (await Git.RunAsync(next.WorkTree, ["rev-parse", "--abbrev-ref", "HEAD"])).Trim());
        var commits = (await Git.RunAsync(next.WorkTree, ["rev-list", "--all"])).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, commits.Length);
        Assert.DoesNotContain(code.Secret, commits);
        Assert.Contains(code.Secret, (await Git.RunAsync(copy, ["-c", "core.longpaths=true", "rev-list", "--all"])).Split('\n'));
        // Its own objects: nothing shared with the copy, so the task can't change what later tasks get.
        Assert.False(File.Exists(Path.Combine(next.WorkTree, ".git", "objects", "info", "alternates")));
    }

    [Fact]
    public async Task A_runner_without_a_copy_clones_the_task_repository_before_applying_its_bundle()
    {
        code.RequireLocalFetch();
        var ct = TestContext.Current.CancellationToken;
        var remote = Path.Combine(code.Root, "task-repository.git");
        await Git.RunAsync(code.Root, ["clone", "--bare", "--branch", "main", code.Whole, remote], ct);
        Assert.NotEqual(code.C, await Git.CommitIdAsync(remote, "refs/heads/main", ct));

        var source = new Bundles { File = code.Next };
        const string url = "https://git.example.test/team/base.git";
        using var run = RunWorkspace.Create(Path.Combine(code.Root, "cold-checkout"), "dlv_clone", 1);
        var delivery = Delivery("dlv_clone", "main");
        delivery = delivery with
        {
            Envelope = delivery.Envelope with { Refs = [new Ref { Kind = "git.repo", Url = url }] },
        };

        var error = await run.CheckOutAsync(delivery, _ => code.Git, source, ct,
            launch => TestRepositoryRemote.Map(launch, url, remote), ask: source.AskAsync);
        Assert.True(error is null, error);
        Assert.Equal(0, source.Asked);
        Assert.Equal(code.C, await Git.CommitIdAsync(run.WorkTree, "HEAD", ct));
        Assert.Equal("main", (await Git.RunAsync(run.WorkTree, ["branch", "--show-current"], ct)).Trim());
        Assert.Equal("three", await File.ReadAllTextAsync(Path.Combine(run.WorkTree, "README.md"), ct));
    }

    [Fact]
    public async Task A_runner_without_a_copy_asks_the_sender_in_its_progress_and_fails_plainly_when_the_history_never_comes()
    {
        code.RequireLocalFetch();
        var source = new Bundles { File = code.Next, History = code.History };
        await using var h = new RunnerHarness(bundles: source);
        var bundle = new AttachmentRef { Id = "att_next", Name = "main.bundle", Format = AttachmentFormat.GitBundle, SizeBytes = 1, Sha256 = "00", Branch = "main" };
        var environment = new RunEnvironment { Checkout = CheckoutMode.Bundle };

        Assert.True((await h.DeliverAsync(h.Task("dlv_cold", environment: environment, attachments: [bundle]))).Accepted);
        var result = (await h.Service.SettledAsync("dlv_cold")).Result;
        Assert.True(result.Status == SettleStatus.Succeeded, result.Error);
        Assert.Equal("three", File.ReadAllText(Path.Combine(h.WorkTreeOf("dlv_cold"), "README.md")));
        var asked = Assert.Single(Reports(h), p => p.Progress.Request is not null).Progress;
        Assert.StartsWith("Waiting for the code from the sender", asked.Message);
        Assert.Equal((ProgressRequestKinds.GitHistory, "att_next"), (asked.Request!.Kind, asked.Request.AttachmentId));

        // The sender's session has closed: nothing comes, and the run says so rather than starting without the code.
        source.History = null;
        var task = h.Task("dlv_never", environment: environment, attachments: [bundle]);
        Assert.True((await h.DeliverAsync(task with { Envelope = task.Envelope with { WorkspaceId = "ws_other" } })).Accepted);
        var never = (await h.Service.SettledAsync("dlv_never")).Result;
        Assert.Equal(SettleStatus.Failed, never.Status);
        Assert.Contains("the sender's mbus didn't send it within 30 minutes", never.Error);
    }

    [Fact]
    public async Task Housekeeping_drops_task_refs_older_than_14_days_from_a_copy_and_keeps_newer_ones_other_refs_and_the_copy()
    {
        // the original issue: a copy keeps a ref per task commit it was sent, which would otherwise only grow while the copy is in use.
        code.RequireLocalFetch();
        var ct = TestContext.Current.CancellationToken;
        var work = Path.Combine(code.Root, "refs-" + Guid.NewGuid().ToString("N")[..8]);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
        var mirrors = RepoMirrors.For(work, time);
        var whole = GitBundles.ReadHeader(code.Whole);
        var copy = (await mirrors.TakeInHistoryAsync(code.Git!, "ws_test", code.Whole, whole, ct)).Copy;
        Assert.NotNull(copy);
        var b = whole.Refs["refs/heads/main"];
        time.Advance(TimeSpan.FromDays(10));
        var checkoutError = await mirrors.CheckOutCodeAsync(code.Git!, copy, code.Next, GitBundles.ReadHeader(code.Next), "main", code.C, Path.Combine(work, "task"), ct);
        Assert.True(checkoutError is null, checkoutError);
        await Git.RunAsync(copy, ["-c", "core.longpaths=true", "update-ref", "refs/heads/kept", b]);
        time.Advance(TimeSpan.FromDays(5));

        Assert.Empty(await mirrors.PruneAsync(code.Git, TimeSpan.FromDays(30), ct));

        var refs = (await Git.RunAsync(copy, ["for-each-ref", "--format=%(refname)"])).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["refs/heads/kept", RepoMirrors.CodeRef(code.C)], refs);
    }

    [Fact]
    public async Task Bundle_copies_read_and_check_out_prerequisite_objects_beyond_Windows_MAX_PATH()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "MAX_PATH applies only to Windows.");
        code.RequireLocalFetch();
        var ct = TestContext.Current.CancellationToken;
        // Keep the repository directory below MAX_PATH: it is its object filenames that must cross the limit.
        var mirrors = RepoMirrors.For(Path.Combine(code.Root, new string('p', Math.Max(1, 165 - code.Root.Length - 1))));
        var (copy, error) = await mirrors.TakeInHistoryAsync(code.Git!, "ws_long", code.Whole, GitBundles.ReadHeader(code.Whole), ct);
        Assert.True(copy is not null, error);
        Assert.True(Path.Combine(copy!, "objects", "pack", "pack-" + new string('0', 40) + ".pack").Length > 260);
        Assert.Equal(copy, await mirrors.FindCodeAsync(code.Git!, "ws_long", code.Next, ct));
        // Local upload-pack must read those objects too, both when updating a copy and checking out a task.
        var updated = await mirrors.TakeInHistoryAsync(code.Git!, "ws_long", code.History, GitBundles.ReadHeader(code.History), ct);
        Assert.True(updated.Copy == copy, updated.Error);
        var workTree = Path.Combine(code.Root, "long-path-task");
        var checkoutError = await mirrors.CheckOutCodeAsync(code.Git!, copy!, code.Next, GitBundles.ReadHeader(code.Next), "main", code.C, workTree, ct);
        Assert.True(checkoutError is null, checkoutError);
        await Git.RunAsync(workTree, ["-c", "core.longpaths=true", "checkout", "--quiet", "-b", "main", "origin/main"], ct);
        Assert.Equal("three", await File.ReadAllTextAsync(Path.Combine(workTree, "README.md"), ct));
    }

    private static List<RunnerProgress> Reports(RunnerHarness h)
    {
        var all = new List<RunnerProgress>();
        while (h.Service.Progress.Reader.TryRead(out var p)) all.Add(p);
        return all;
    }

    private async Task<string?> CheckOutAsync(string work, string id, string branch, Bundles source)
    {
        using var run = RunWorkspace.Create(work, id, 1);
        return await run.CheckOutAsync(Delivery(id, branch), _ => code.Git, source, TestContext.Current.CancellationToken, ask: source.AskAsync);
    }

    private static TaskDelivery Delivery(string id, string branch) => new()
    {
        DeliveryId = id,
        Attempt = 1,
        LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        AgentId = "claude-code",
        HeartbeatSeconds = 30,
        RunEnvironment = new RunEnvironment { Checkout = CheckoutMode.Bundle },
        Envelope = new MessageEnvelope
        {
            Id = "msg_" + id, Type = "task.implement", Source = "agent://ws_test/agt_sender", Time = DateTimeOffset.UtcNow, WorkspaceId = "ws_test",
            Target = new RoleTarget { Role = "developer" }, ThreadId = "thr_" + id,
            Sender = new SenderIdentity { Kind = SenderKind.Agent }, Body = new BodyRef { SizeBytes = 1, Sha256 = "00" },
            Attachments = [new AttachmentRef { Id = "att_" + id, Name = branch + ".bundle", Format = AttachmentFormat.GitBundle, SizeBytes = 1, Sha256 = "00", Branch = branch }],
        },
    };

    /// <summary>The task's bundle as a file, and the sender's answer when asked for the earlier history.</summary>
    private sealed class Bundles : IBundleSource
    {
        public string? File { get; set; }

        public string? History { get; set; }

        public int Asked { get; private set; }

        public Task AskAsync(ProgressRequest request, CancellationToken ct)
        {
            Asked++;
            return Task.CompletedTask;
        }

        public Task<string?> FetchAsync(TaskDelivery delivery, AttachmentRef bundle, string runDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(File);

        public async Task<string?> RequestHistoryAsync(TaskDelivery delivery, AttachmentRef bundle, string runDirectory,
            Func<ProgressRequest, CancellationToken, Task> ask, TimeSpan wait, CancellationToken cancellationToken)
        {
            await ask(new ProgressRequest { Kind = ProgressRequestKinds.GitHistory, AttachmentId = bundle.Id, ReplyTo = new InboxTarget { InboxId = "inb_run" } }, cancellationToken);
            return History;
        }
    }
}
