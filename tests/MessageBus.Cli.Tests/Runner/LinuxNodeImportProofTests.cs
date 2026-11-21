using System.Text.Json;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class LinuxNodeImportProofTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_boundary_import_and_checked_return(bool externalObjects)
    {
        // Ordinary cross-platform suites skip. The proof launcher requires this opt-in and
        // counts completed cases, so an unavailable boundary can never count as proof.
        var fixture = Environment.GetEnvironmentVariable("MBUS_LINUX_PROOF_ROOT");
        Assert.SkipWhen(fixture is null, "Run scripts/proof/node-import.sh in a disposable Linux container.");
        Assert.True(OperatingSystem.IsLinux(), "Linux proof requested on a non-Linux host.");
        var ct = TestContext.Current.CancellationToken;
        var process = new VerificationProcess(TimeProvider.System);
        Assert.True(await process.ProbeAsync(ct), "Real unprivileged bubblewrap/system Node toolchain unavailable.");
        await using var h = new RunnerHarness();
        var id = Guid.NewGuid().ToString("N");
        var repo = Directory.CreateDirectory(Path.Combine(h.Scratch, "seed")).FullName;
        var served = Path.Combine(fixture!, "served", id + ".git");
        var remote = "https://fixture.test/" + id + ".git";
        await Git.RunAsync(repo, ["init", "--template=", "--initial-branch=main"], ct);
        File.WriteAllText(Path.Combine(repo, "package.json"), """
            {"name":"fixture","version":"1.0.0","scripts":{"preinstall":"exit 91","pretest":"exit 92","test":"node check.js"}}
            """);
        File.WriteAllText(Path.Combine(repo, "package-lock.json"), """
            {"name":"fixture","version":"1.0.0","lockfileVersion":3,"packages":{"":{"name":"fixture","version":"1.0.0","hasInstallScript":true}}}
            """);
        File.WriteAllText(Path.Combine(repo, "check.js"), "process.exit(1);");
        await CommitAsync(repo, ct);
        var pinned = await Git.CommitIdAsync(repo, "HEAD", ct);
        await Git.RunAsync(repo, ["clone", "--bare", "--no-hardlinks", repo, served], ct);
        await Git.RunAsync(served, ["update-server-info"], ct);
        var credentials = new Credentials();
        var verifier = new NodeVerification(remote, process, credentials, h.WorkRoot);
        using var agent = RunWorkspace.Create(h.WorkRoot, "agent", 1);
        using var fresh = RunWorkspace.Create(h.WorkRoot, "fresh", 1);
        var snapshot = new VerificationSnapshot
        {
            Environment = "frontend", Recipe = NodeVerification.Recipe, Version = "1", Digest = new string('a', 64),
            Repository = "gh:example/fixture", Remote = remote, Revision = pinned, BaseRevision = pinned,
            RequiredChecks = ["test"],
            NodeVersion = (await process.RunAsync(null, VerificationProcess.Node, ["--version"], ct)).Output.Trim().TrimStart('v'),
            NpmVersion = (await process.RunAsync(null, VerificationProcess.Npm, ["--version"], ct)).Output.Trim(),
        };
        var delivery = h.Task("dlv_code") with { Verification = snapshot };
        Assert.Null(await verifier.PrepareAsync(agent.WorkTree, delivery, fresh, ct));
        var credentialSentinel = Path.Combine(h.Scratch, "credential-sentinel");
        var controlSentinel = Path.Combine(h.Scratch, "control-sentinel");
        File.WriteAllText(credentialSentinel, "fixture credential sentinel");
        File.WriteAllText(controlSentinel, "fixture control sentinel");
        var denied = $$"""
            const fs = require('node:fs');
            for (const p of {{JsonSerializer.Serialize(new[] { credentialSentinel, controlSentinel })}}) {
              try { fs.readFileSync(p); process.exit(81); }
              catch (e) { if (!['ENOENT', 'EACCES'].includes(e.code)) throw e; }
            }
            if (fs.existsSync('/work/.git/config')) process.exit(82);
            const effective = fs.readFileSync('/proc/self/status', 'utf8').match(/^CapEff:\s*([0-9a-f]+)$/m);
            if (!effective || !/^0+$/.test(effective[1])) process.exit(83);
            const socket = require('node:net').connect(443, '127.0.0.1');
            socket.on('connect', () => process.exit(84));
            socket.on('error', () => process.exit(0));
            socket.setTimeout(1000, () => process.exit(85));
            """;
        File.WriteAllText(Path.Combine(agent.WorkTree, "check.js"), denied);
        await CommitAsync(agent.WorkTree, ct);
        var candidate = await Git.CommitIdAsync(agent.WorkTree, "HEAD", ct);
        var metadata = Path.Combine(agent.WorkTree, ".git");
        // Git must not execute this fsmonitor or hooks while exporting hostile metadata.
        var marker = Path.Combine(h.Scratch, "hostile-executed");
        await Git.RunAsync(agent.WorkTree, ["config", "core.fsmonitor", "touch /output/hostile-executed"], ct);
        await Git.RunAsync(agent.WorkTree, ["config", "core.hooksPath", h.Scratch], ct);
        await Git.RunAsync(agent.WorkTree, ["config", "include.path", credentialSentinel], ct);
        File.WriteAllText(Path.Combine(h.Scratch, "post-checkout"), "#!/bin/sh\ntouch " + marker + "\n");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(Path.Combine(h.Scratch, "post-checkout"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (externalObjects)
        {
            var outsideObjects = Path.Combine(h.Scratch, "outside-objects");
            Directory.Move(Path.Combine(metadata, "objects"), outsideObjects);
            Directory.CreateDirectory(Path.Combine(metadata, "objects", "info"));
            File.WriteAllText(Path.Combine(metadata, "objects", "info", "alternates"), outsideObjects + "\n");
            // The object is readable on the host, but unavailable to the real export sandbox.
            // Reading the intentionally malformed include on the host would fail; inspect
            // object availability without that include before entering the boundary.
            File.WriteAllText(Path.Combine(metadata, "config"), "[core]\nrepositoryformatversion = 0\n");
            Assert.Equal(candidate, await Git.CommitIdAsync(agent.WorkTree, "HEAD", ct));
            await Git.RunAsync(agent.WorkTree, ["config", "include.path", credentialSentinel], ct);
        }
        var result = await verifier.VerifyAsync(delivery, agent, fresh, ct);
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(fresh.Root, "export", "hostile-executed")));
        Assert.Equal("fixture credential sentinel", File.ReadAllText(credentialSentinel));
        Assert.Equal("fixture control sentinel", File.ReadAllText(controlSentinel));
        Assert.Equal(2, credentials.Requests);
        Assert.Empty(Directory.GetDirectories(h.WorkRoot, ".verification-git-*"));
        // Packaging must independently reject agent-owned hostile metadata, even if
        // a caller supplies a checked revision. No key lookup or HTTP may occur.
        agent.VerifiedReturnRevision = candidate;
        using (var service = new FakeServer())
        using (var http = new HttpClient(service) { BaseAddress = new Uri(FakeServer.Url) })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunCodeReturn.ReturnAsync(
                http, http, "ws_test", agent, delivery, NodeVerification.Branch, pinned, ct,
                (_, _) => throw new NotSupportedException("Unexpected recipient lookup")));
            Assert.Empty(service.Requests);
        }
        if (externalObjects)
        {
            Assert.Equal(VerificationState.Blocked, result.State);
            Assert.Equal("revision-unverified", result.Reason);
            Assert.Null(result.Revision);
        }
        else
        {
            Assert.Equal(VerificationState.Passed, result.State);
            Assert.Equal(candidate, result.Revision);
            fresh.VerifiedReturnRevision = result.Revision;
            await RunCodeReturnTests.AssertReturnAsync(h, fresh, delivery, NodeVerification.Branch, pinned);
        }
        // Files are run-owned. Launcher records the retained fixture root for runner cleanup.
        File.WriteAllText(Path.Combine(fixture!, externalObjects ? "alternates.passed" : "return.passed"), "passed\n");
    }

    private static async Task CommitAsync(string repo, CancellationToken ct)
    {
        await Git.RunAsync(repo, ["add", "--all"], ct);
        await Git.RunAsync(repo, ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.com",
            "-c", "commit.gpgsign=false", "commit", "--no-verify", "-m", "Fixture"], ct);
    }

    private sealed class Credentials : IRepoCredentialSource
    {
        public string Description => "Disposable HTTPS fixture";
        public int Requests { get; private set; }
        public Task<RepoCredential?> IssueAsync(RepoCredentialRequest request, CancellationToken cancellationToken)
        {
            Assert.False(request.CanPush);
            Requests++;
            return Task.FromResult<RepoCredential?>(new()
            {
                UserName = "fixture", Secret = "fixture-value", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });
        }
    }
}
