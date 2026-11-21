using System.Diagnostics;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Sandbox;
using Microsoft.Extensions.Time.Testing;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class NodeVerificationTests
{
    private const string Remote = "https://example.com/example/frontend.git";
    private const string Manifest = """
        {"name":"frontend","version":"1.0.0","scripts":{"preinstall":"exit 10","pretest":"exit 11","test":"node check.js"}}
        """;
    private const string Lockfile = """
        {"name":"frontend","version":"1.0.0","lockfileVersion":3,"packages":{"":{"name":"frontend","version":"1.0.0","hasInstallScript":true}}}
        """;

    [Theory]
    [InlineData("review", VerificationState.Passed)]
    [InlineData("nested-review", VerificationState.Passed)]
    [InlineData("nested-build", VerificationState.Passed)]
    [InlineData("private", VerificationState.Passed)]
    [InlineData("expired-credential", VerificationState.Blocked)]
    [InlineData("unapproved-remote", VerificationState.Blocked)]
    [InlineData("nested-changed-lockfile", VerificationState.Blocked)]
    [InlineData("input-changed-lockfile", VerificationState.Blocked)]
    [InlineData("ambiguous", VerificationState.Blocked)]
    [InlineData("commit", VerificationState.Failed)]
    [InlineData("changed-lockfile", VerificationState.Blocked)]
    [InlineData("changed-attributes", VerificationState.Blocked)]
    [InlineData("changed-manifest", VerificationState.Blocked)]
    [InlineData("added-npmrc", VerificationState.Blocked)]
    [InlineData("setup-error", VerificationState.Blocked)]
    public async Task Local_npm_fixture_checks_the_pinned_revision(string scenario, VerificationState expected)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The native fixture uses a POSIX shell.");
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        var node = CommandLocator.Find("node", Environment.GetEnvironmentVariable, []);
        var npm = CommandLocator.Find("npm", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null || node is null || npm is null, "The fixture requires Git, Node and npm.");
        var ct = TestContext.Current.CancellationToken;
        await using var runner = new RunnerHarness();
        var repo = Directory.CreateDirectory(Path.Combine(runner.Scratch, "repo")).FullName;
        var credential = new FixtureCredentialSource
        {
            ExpiresAt = scenario == "expired-credential" ? DateTimeOffset.UnixEpoch : DateTimeOffset.UnixEpoch.AddHours(1),
        };
        var process = new LocalFixtureProcess(repo, runner.Scratch, git!, node!, npm!)
        {
            RequireCredential = scenario is "private" or "expired-credential",
        };
        var executor = new NodeVerification(Remote, process, credential, runner.WorkRoot,
            new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        await Git.RunAsync(repo, ["init", "--template=", "--initial-branch=main"], ct);
        var projectPath = scenario.StartsWith("nested-", StringComparison.Ordinal) ? "src/client" : "";
        var project = Directory.CreateDirectory(Path.Combine(repo, projectPath)).FullName;
        if (projectPath.Length > 0 || scenario == "ambiguous")
        {
            var api = Directory.CreateDirectory(Path.Combine(repo, "src", "api")).FullName;
            File.WriteAllText(Path.Combine(api, "marker.txt"), "sibling");
        }
        if (scenario == "ambiguous")
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(repo, "src", "client")).FullName, "package.json"), Manifest);
        File.WriteAllText(Path.Combine(project, "package.json"), Manifest);
        File.WriteAllText(Path.Combine(project, "package-lock.json"), scenario == "setup-error" ? "{}" : Lockfile);
        File.WriteAllText(Path.Combine(project, "check.js"), projectPath.Length == 0 ? "process.exit(0);"
            : "require('node:fs').writeFileSync('../api/output.txt', require('node:fs').readFileSync('../api/marker.txt'));");
        await CommitAsync(repo, ct);
        var approved = await Git.CommitIdAsync(repo, "HEAD", ct);
        var pinned = approved;
        if (scenario == "input-changed-lockfile")
        {
            File.AppendAllText(Path.Combine(project, "package-lock.json"), "\n");
            await CommitAsync(repo, ct);
            pinned = await Git.CommitIdAsync(repo, "HEAD", ct);
        }
        var snapshot = new VerificationSnapshot
        {
            Environment = "frontend", Recipe = NodeVerification.Recipe, Version = "1", Digest = new string('a', 64),
            Repository = "gh:example/frontend",
            Remote = scenario == "unapproved-remote" ? "https://example.com/other/frontend.git" : Remote,
            Revision = pinned, BaseRevision = approved, RequiredChecks = ["test"],
            NodeVersion = await process.VersionAsync(VerificationProcess.Node, ct),
            NpmVersion = await process.VersionAsync(VerificationProcess.Npm, ct),
        };

        using var agent = RunWorkspace.Create(runner.WorkRoot, "agent", 1);
        using var fresh = RunWorkspace.Create(runner.WorkRoot, "fresh", 1);
        var review = scenario is "review" or "nested-review" or "private";
        var task = runner.Task("delivery");
        var delivery = task with
        {
            Verification = snapshot,
            Envelope = task.Envelope with { Type = review ? "task.review.code" : "task.implement" },
        };
        var setup = await executor.PrepareAsync(agent.WorkTree, delivery, fresh, ct);
        if (scenario is "setup-error" or "ambiguous" or "expired-credential" or "unapproved-remote" or "input-changed-lockfile")
        {
            Assert.Equal(scenario == "input-changed-lockfile" ? "dependency-policy-changed" : "revision-unverified", setup);
            Assert.Equal(0, process.Restores);
            Assert.Equal(0, process.Checks);
            Assert.Equal(scenario == "expired-credential" ? 1 : 0, credential.Requests.Count);
            Assert.Equal(scenario == "unapproved-remote" ? 0 : 1, process.Fetches);
            Assert.Empty(process.CredentialFiles);
            return;
        }
        Assert.Null(setup);
        Assert.Equal(1, process.Restores);

        var expectedRevision = pinned;
        if (!review)
        {
            Assert.Equal(NodeVerification.Branch, (await Git.RunAsync(agent.WorkTree, ["branch", "--show-current"], ct)).Trim());
            if (scenario.EndsWith("changed-lockfile", StringComparison.Ordinal))
                File.AppendAllText(Path.Combine(agent.WorkTree, projectPath, "package-lock.json"), "\n");
            else if (scenario == "changed-attributes")
                File.WriteAllText(Path.Combine(agent.WorkTree, projectPath, ".gitattributes"), "* text=auto");
            else if (scenario == "changed-manifest")
                File.AppendAllText(Path.Combine(agent.WorkTree, projectPath, "package.json"), "\n");
            else if (scenario == "added-npmrc")
                File.WriteAllText(Path.Combine(agent.WorkTree, ".npmrc"), "registry=https://example.com");
            else if (scenario == "nested-build")
                File.WriteAllText(Path.Combine(agent.WorkTree, "src", "api", "marker.txt"), "changed sibling");
            else
                File.WriteAllText(Path.Combine(agent.WorkTree, "check.js"), "process.exit(7);");
            await CommitAsync(agent.WorkTree, ct);
            expectedRevision = await Git.CommitIdAsync(agent.WorkTree, "HEAD", ct);
        }
        // A dirty working tree must never be checked or packaged, in either mode.
        File.WriteAllText(Path.Combine(agent.WorkTree, projectPath, "check.js"), "process.exit(99);");
        var result = await executor.VerifyAsync(delivery, agent, fresh, ct);

        Assert.Equal(expected, result.State);
        Assert.Equal(scenario == "private" ? 2 : 0, credential.Requests.Count);
        Assert.Equal(credential.Requests.Count, process.CredentialFiles.Count);
        Assert.All(credential.Requests, request =>
        {
            Assert.False(request.CanPush);
            Assert.Equal(delivery.DeliveryId, request.DeliveryId);
            Assert.Equal(Remote, request.Repository.AbsoluteUri);
        });
        Assert.All(process.CredentialFiles, file => Assert.False(File.Exists(file)));
        if (expected == VerificationState.Blocked)
        {
            Assert.Null(result.Revision);
            Assert.Equal(scenario.Contains("changed-", StringComparison.Ordinal) ? "dependency-policy-changed" : "revision-unverified", result.Reason);
            Assert.Equal(0, process.Checks);
        }
        else
        {
            Assert.Equal(expectedRevision, result.Revision);
            Assert.Equal(expectedRevision, await Git.CommitIdAsync(fresh.WorkTree, "HEAD", ct));
            var check = Assert.Single(result.Checks!);
            Assert.Equal(expected, check.State);
            Assert.Equal(expected == VerificationState.Failed ? "check-failed" : null, check.Reason);
            Assert.Equal(2, process.Restores);
        }
    }

    [Theory]
    [InlineData("^19.0.0-rc", "https://registry.npmjs.org/@scope/tool/-/tool-9.0.2-beta.1.tgz", true, true)]
    [InlineData(">=9.0.2-beta.1 <10 || ~11.0.0", "https://registry.npmjs.org/@scope/tool/-/tool-9.0.2-beta.1.tgz", true, true)]
    [InlineData("9.0.2-beta.1", "https://example.com/tool/-/tool-9.0.2-beta.1.tgz", true, false)]
    [InlineData("9.0.2-beta.1", "https://registry.npmjs.org/@scope/tool/-/tool-9.0.2-beta.1.tgz", false, false)]
    [InlineData("npm:other@9.0.2", "https://registry.npmjs.org/@scope/tool/-/tool-9.0.2-beta.1.tgz", true, false)]
    [InlineData("https://example.com/tool.tgz", "https://registry.npmjs.org/@scope/tool/-/tool-9.0.2-beta.1.tgz", true, false)]
    [InlineData("beta9", "https://registry.npmjs.org/@scope/tool/-/tool-9.0.2-beta.1.tgz", true, false)]
    [InlineData("owner/tool", "https://registry.npmjs.org/@scope/tool/-/tool-9.0.2-beta.1.tgz", true, false)]
    public async Task Nested_lockfile_requires_registry_ranges_pinned_tarballs_and_sha512(
        string range, string resolved, bool validHash, bool allowed)
    {
        await using var runner = new RunnerHarness();
        var project = Directory.CreateDirectory(Path.Combine(runner.Scratch, "src", "client")).FullName;
        var integrity = validHash ? "sha512-" + Convert.ToBase64String(new byte[64]) : "sha1-invalid";
        File.WriteAllText(Path.Combine(project, "package.json"), $$"""
            {"name":"frontend","version":"1.0.0","scripts":{"test":"node check.js"},"devDependencies":{"@scope/tool":"{{range}}" } }
            """);
        File.WriteAllText(Path.Combine(project, "package-lock.json"), $$"""
            {"lockfileVersion":3,"packages":{
              "":{"devDependencies":{"@scope/tool":"{{range}}"} },
              "node_modules/@scope/tool":{"version":"9.0.2-beta.1","resolved":"{{resolved}}","integrity":"{{integrity}}","dev":true}
            } }
            """);
        Assert.Equal(allowed ? "/work/src/client" : null, NodeVerificationPolicy.ProjectPrefix(runner.Scratch, ["test"]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Registry_archive_can_declare_bundled_packages_only_when_the_archive_is_pinned(bool pinned)
    {
        await using var runner = new RunnerHarness();
        File.WriteAllText(Path.Combine(runner.Scratch, "package.json"), Manifest);
        var integrity = pinned ? "sha512-" + Convert.ToBase64String(new byte[64]) : "sha1-invalid";
        File.WriteAllText(Path.Combine(runner.Scratch, "package-lock.json"), $$"""
            {"lockfileVersion":3,"packages":{
              "":{},
              "node_modules/@scope/tool":{
                "version":"1.0.0",
                "resolved":"https://registry.npmjs.org/@scope/tool/-/tool-1.0.0.tgz",
                "integrity":"{{integrity}}",
                "bundleDependencies":["@napi-rs/wasm-runtime","tslib"]
              }
            } }
            """);
        Assert.Equal(pinned ? "/work" : null, NodeVerificationPolicy.ProjectPrefix(runner.Scratch, ["test"]));
    }

    [Fact]
    public async Task Runner_publishes_the_exact_checked_commit_after_the_agent_exits()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The native fixture uses a POSIX shell.");
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        var node = CommandLocator.Find("node", Environment.GetEnvironmentVariable, []);
        var npm = CommandLocator.Find("npm", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null || node is null || npm is null, "The fixture requires Git, Node and npm.");
        var ct = TestContext.Current.CancellationToken;
        await using var fixtures = new RunnerHarness();
        var repo = Directory.CreateDirectory(Path.Combine(fixtures.Scratch, "repo")).FullName;
        var process = new LocalFixtureProcess(repo, fixtures.Scratch, git!, node!, npm!);
        var verifier = new NodeVerification(Remote, process);
        await Git.RunAsync(repo, ["init", "--template=", "--initial-branch=main"], ct);
        File.WriteAllText(Path.Combine(repo, "package.json"), """
            {"name":"frontend","version":"1.0.0","scripts":{
              "test":"node check.js && touch check-output",
              "typecheck":"test ! -e check-output && node check.js",
              "build":"test ! -e check-output && node check.js"
            }}
            """);
        File.WriteAllText(Path.Combine(repo, "package-lock.json"), Lockfile);
        File.WriteAllText(Path.Combine(repo, "check.js"), "process.exit(1);");
        await CommitAsync(repo, ct);
        var pinned = await Git.CommitIdAsync(repo, "HEAD", ct);
        var snapshot = new VerificationSnapshot
        {
            Environment = "frontend", Recipe = NodeVerification.Recipe, Version = "1", Digest = new string('a', 64),
            Repository = "gh:example/frontend", Remote = Remote, Revision = pinned, BaseRevision = pinned, RequiredChecks = ["test", "typecheck", "build"],
            NodeVersion = await process.VersionAsync(VerificationProcess.Node, ct),
            NpmVersion = await process.VersionAsync(VerificationProcess.Npm, ct),
        };
        string? returned = null;
        await using var runner = new RunnerHarness(keepRunFolders: false, extensions: e => e with
        {
            Verification = verifier,
            Tools = new FixtureRunTools(),
            ReturnCommits = async (workspace, _, branch, baseCommit, stop) =>
            {
                Assert.Equal(3, process.Checks);
                Assert.Equal(pinned, baseCommit);
                Assert.Equal(NodeVerification.Branch, branch);
                returned = await Git.CommitIdAsync(workspace.WorkTree, branch, stop);
                Assert.Equal(returned, workspace.VerifiedReturnRevision);
                return true;
            },
        });
        await runner.Engine.RescanAsync(ct);
        Assert.Contains(CapabilityNames.NodeVerificationV1, (await runner.Service.NextInventoryAsync()).Capabilities);
        var task = runner.Task("verified", directives: values =>
        {
            values["replace"] = "check.js|process.exit(1);|process.exit(0);";
            values["commit"] = "Fix the check";
        });
        task = task with
        {
            Verification = snapshot,
            Envelope = task.Envelope with { Verification = new() { Environment = "frontend", RequiredChecks = ["test", "typecheck", "build"] } },
        };
        Assert.True((await runner.DeliverAsync(task)).Accepted);
        var settled = await runner.Service.SettledAsync(task.DeliveryId);
        Assert.Equal(SettleStatus.Succeeded, settled.Result.Status);
        Assert.Equal(VerificationState.Passed, settled.Verification?.State);
        Assert.Equal(4, process.Restores);
        var cache = Assert.Single(process.RestoreCaches.Distinct());
        Assert.Equal(Path.Combine(runner.WorkRoot, task.DeliveryId + "-verification-1", "npm-cache"), cache);
        Assert.NotNull(returned);
        Assert.NotEqual(pinned, returned);
        Assert.Equal(returned, settled.Verification?.Revision);
        Assert.Equal(returned, Assert.Single(settled.Result.Refs).Sha);
        await runner.Engine.StopAsync();
        Assert.False(Directory.Exists(cache));
    }

    private sealed class FixtureRunTools : IRunTools
    {
        public Task<RunToolsSession> StartAsync(RunToolsRequest request, CancellationToken cancellationToken)
        {
            request.RecordVerifiedTaskSettle?.Invoke(new SettleInput
            {
                ThreadId = request.Delivery.Envelope.ThreadId,
                Result = new SettleResult
                {
                    Status = SettleStatus.Succeeded,
                    Refs = [new Ref { Kind = "git.commit", Sha = new string('b', 40) }],
                },
            });
            return Task.FromResult(new RunToolsSession(new Dictionary<string, string>(), () => ValueTask.CompletedTask));
        }
    }

    [Fact]
    public async Task Bubblewrap_fixture_has_no_runner_files_and_ignores_lifecycle_hooks()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The verifier's executable boundary requires Linux.");
        var ct = TestContext.Current.CancellationToken;
        var process = new VerificationProcess(TimeProvider.System);
        Assert.SkipUnless(await process.ProbeAsync(ct), "The host has no usable unprivileged bubblewrap and system Node toolchain.");
        await using var runner = new RunnerHarness();
        var checkout = Directory.CreateDirectory(Path.Combine(runner.Scratch, "checkout")).FullName;
        var outside = Path.Combine(runner.Scratch, "outside");
        File.WriteAllText(outside, "fixture");
        File.WriteAllText(Path.Combine(checkout, "package.json"), Manifest);
        File.WriteAllText(Path.Combine(checkout, "package-lock.json"), Lockfile);
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        File.WriteAllText(Path.Combine(checkout, ".git", "private"), "fixture");
        var executor = new NodeVerification(Remote, process);
        var cache = Path.Combine(runner.Scratch, "npm-cache");
        Assert.Null(await executor.RestoreAsync(checkout, "/work", cache, maskGit: true, ct));
        Assert.True(Directory.Exists(Path.Combine(cache, "_logs")));
        File.WriteAllText(Path.Combine(cache, "private"), "fixture");
        var hidden = await process.RunAsync(checkout, "/bin/sh",
            ["-c", "test ! -e \"$1\" && test ! -e /work/.git/private && test \"$HOME\" = /tmp/home", "fixture", outside],
            ct, maskGit: true);
        Assert.Equal(0, hidden.ExitCode);
        Directory.Delete(Path.Combine(checkout, ".git"), recursive: true);
        File.WriteAllText(Path.Combine(checkout, "check.js"), "process.exit(Number(require('node:fs').existsSync('/tmp/npm-cache/private')));");
        Assert.Equal(VerificationState.Passed, (await executor.CheckAsync(checkout, "/work", "test", ct)).State);
        File.WriteAllText(Path.Combine(checkout, "check.js"), "process.exit(7);");
        Assert.Equal(VerificationState.Failed, (await executor.CheckAsync(checkout, "/work", "test", ct)).State);
    }

    private static async Task CommitAsync(string repo, CancellationToken ct)
    {
        await Git.RunAsync(repo, ["add", "--all"], ct);
        await Git.RunAsync(repo, ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.com",
            "-c", "commit.gpgsign=false", "commit", "--no-verify", "-m", "Fixture"], ct);
    }

    private sealed class FixtureCredentialSource : IRepoCredentialSource
    {
        public string Description => "Fixture";
        public required DateTimeOffset ExpiresAt { get; init; }
        public List<RepoCredentialRequest> Requests { get; } = [];

        public Task<RepoCredential?> IssueAsync(RepoCredentialRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult<RepoCredential?>(new()
            {
                UserName = "fixture",
                Secret = "fixture-value",
                ExpiresAt = ExpiresAt,
            });
        }
    }

    // Exercise actual Git and npm with a local fixture on POSIX. The Linux-only test exercises mounts and namespaces.
    private sealed class LocalFixtureProcess(string repo, string scratch, string git, string node, string npm) : IVerificationProcess
    {
        public bool RequireCredential { get; init; }

        public int Fetches { get; private set; }

        public List<string> CredentialFiles { get; } = [];

        public int Restores => RestoreCaches.Count;

        public List<string> RestoreCaches { get; } = [];

        public int Checks { get; private set; }

        public async Task<string> VersionAsync(string executable, CancellationToken ct)
        {
            var version = await RunAsync(null, executable, ["--version"], ct);
            Assert.Equal(0, version.ExitCode);
            return version.Output.Trim();
        }

        public async Task<VerificationProcess.CommandResult> RunAsync(string? checkout, string executable, string[] args, CancellationToken ct,
            bool network = false, bool maskGit = false, string? input = null, string? output = null, TimeSpan? timeout = null, string? npmCache = null)
        {
            if (executable == VerificationProcess.Git && network && args.Contains("fetch"))
            {
                Fetches++;
                if (RequireCredential && input is null) return new(128, "");
                if (input is not null)
                {
                    Assert.Contains("include.path=/input", args);
                    Assert.NotNull(checkout);
                    Assert.StartsWith("../", Path.GetRelativePath(checkout, input));
                    Assert.DoesNotContain(args, arg => arg.Contains("fixture-value", StringComparison.Ordinal));
                    if (!OperatingSystem.IsWindows())
                        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(input));
                    Assert.Contains($"[http \"{Remote}\"]", File.ReadAllText(input));
                    CredentialFiles.Add(input);
                }
            }
            else
            {
                Assert.All(CredentialFiles, file => Assert.False(File.Exists(file)));
            }

            var home = Directory.CreateTempSubdirectory("mbus-npm-fixture-");
            try
            {
                var workingDirectory = checkout ?? scratch;
                var start = new ProcessStartInfo(Resolve(executable))
                {
                    WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                start.Environment.Clear();
                start.Environment["HOME"] = home.FullName;
                start.Environment["PATH"] = Path.GetDirectoryName(node) + ":/usr/bin:/bin";
                start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
                start.Environment["GIT_ALLOW_PROTOCOL"] = "file";
                foreach (var arg in args)
                    start.ArgumentList.Add(Map(arg));
                if (executable == VerificationProcess.Npm && args.Contains("ci"))
                {
                    Assert.NotNull(npmCache);
                    Assert.StartsWith("../", Path.GetRelativePath(workingDirectory, npmCache));
                    Assert.True(network);
                    RestoreCaches.Add(npmCache);
                }
                else
                {
                    Assert.Null(npmCache);
                }
                if (executable == "/bin/sh" && args.Contains("run-script")) Checks++;
                using var child = Process.Start(start)!;
                child.StandardInput.Close();
                var stdout = child.StandardOutput.ReadToEndAsync(ct);
                var stderr = child.StandardError.ReadToEndAsync(ct);
                await child.WaitForExitAsync(ct);
                if (child.ExitCode is not (0 or 42 or 43))
                    TestContext.Current.TestOutputHelper?.WriteLine($"{executable} {string.Join(' ', args)}: {await stderr}");
                return new(child.ExitCode, await stdout);

                string Map(string arg) => arg switch
                {
                    Remote => repo,
                    "/input" => input!,
                    "include.path=/input" => "include.path=" + input,
                    "--git-dir=/input" => "--git-dir=" + input,
                    "/output/result.bundle" => Path.Combine(output!, "result.bundle"),
                    "--prefix=/work" => "--prefix=" + workingDirectory,
                    "--prefix=/work/src/client" => "--prefix=" + Path.Combine(workingDirectory, "src", "client"),
                    "--prefix=/output/" => "--prefix=" + output + "/",
                    "--cache=/tmp/npm-cache" => "--cache=" + (npmCache ?? home.FullName),
                    "--userconfig=/tmp/npm-userconfig" => "--userconfig=" + Path.Combine(home.FullName, "userconfig"),
                    "--globalconfig=/tmp/npm-globalconfig" => "--globalconfig=" + Path.Combine(home.FullName, "globalconfig"),
                    VerificationProcess.Npm => npm,
                    _ => arg,
                };
            }
            finally
            {
                home.Delete(recursive: true);
            }
        }

        private string Resolve(string executable) => executable switch
        {
            VerificationProcess.Git => git,
            VerificationProcess.Node => node,
            VerificationProcess.Npm => npm,
            _ => executable,
        };
    }
}
