using MessageBus.Attachments.Client;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Tests.Infrastructure;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class RunCommitsTests
{
    // Windows only lets a symbolic link be made with Developer Mode or admin rights; a junction needs neither.
    private static async Task LinkOutsideAsync(string link, string targetFolder)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, targetFolder);
            return;
        }
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/d", "/c", "mklink", "/J", link, targetFolder])
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        await mklink.WaitForExitAsync();
        Assert.Equal(0, mklink.ExitCode);
    }

    private static async Task CommitAsync(string directory, string text)
    {
        File.WriteAllText(Path.Combine(directory, "change.txt"), text);
        await Git.RunAsync(directory, ["add", "."]);
        await Git.RunAsync(directory, ["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", text]);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("objects")]
    [InlineData("link")]
    [InlineData("alternates")]
    [InlineData("commondir")]
    [InlineData("gitfile")]
    public async Task Unsafe_or_corrupt_repository_is_kept_without_reading_parent(string damage)
    {
        if (damage == "link") await WindowsRunCapabilities.RequireJunctionsAsync();
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        await using var h = new RunnerHarness();
        Directory.CreateDirectory(h.WorkRoot);
        await Git.RunAsync(h.WorkRoot, ["init", "--quiet", "--initial-branch=parent"]);
        await CommitAsync(h.WorkRoot, "parent");
        using var run = RunWorkspace.Create(h.WorkRoot, "dlv_unsafe", 1);
        Directory.CreateDirectory(run.WorkTree);
        await Git.RunAsync(run.WorkTree, ["init", "--quiet", "--initial-branch=main"]);
        await CommitAsync(run.WorkTree, "child");
        var commits = new RunCommits(git, run);
        await commits.CaptureAsync();
        var dotgit = Path.Combine(run.WorkTree, ".git");
        switch (damage)
        {
            case "HEAD": File.WriteAllText(Path.Combine(dotgit, "HEAD"), "broken"); break;
            case "objects": RunWorkspace.TryDelete(Path.Combine(dotgit, "objects")); break;
            case "link": await LinkOutsideAsync(Path.Combine(dotgit, "outside"), Path.Combine(h.WorkRoot, ".git")); break;
            case "alternates": File.WriteAllText(Path.Combine(dotgit, "objects", "info", "alternates"), Path.Combine(h.WorkRoot, ".git", "objects")); break;
            case "commondir": File.WriteAllText(Path.Combine(dotgit, "commondir"), Path.Combine(h.WorkRoot, ".git")); break;
            case "gitfile": RunWorkspace.TryDelete(dotgit); File.WriteAllText(dotgit, "gitdir: " + Path.Combine(h.WorkRoot, ".git")); break;
        }
        commits.InspectAfterExit();
        Assert.True(await commits.MustKeepAsync());
        var branch = await commits.BranchAsync();
        Assert.NotEqual("parent", branch);
        if (damage != "objects") Assert.Null(branch);
        Assert.True(Directory.Exists(run.WorkTree));
        if (damage is not "HEAD" and not "objects") Assert.False(RunCommits.IsLocalRepository(run));
    }

    [Theory]
    [InlineData("extensions.partialClone", "origin")]
    [InlineData("remote.origin.promisor", "false")]
    [InlineData("core.repositoryformatversion", "1")]
    [InlineData("include.path", "missing-config")]
    [InlineData("includeIf.gitdir:*/.path", "missing-config")]
    public async Task Unsupported_config_is_kept(string key, string value)
    {
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        await using var h = new RunnerHarness();
        using var run = RunWorkspace.Create(h.WorkRoot, "dlv_config", 1);
        Directory.CreateDirectory(run.WorkTree);
        await Git.RunAsync(run.WorkTree, ["init", "--quiet", "--initial-branch=main"]);
        await CommitAsync(run.WorkTree, "base");
        var commits = new RunCommits(git, run);
        await commits.CaptureAsync();
        await Git.RunAsync(run.WorkTree, ["config", key, value]);
        commits.InspectAfterExit();
        Assert.True(await commits.MustKeepAsync());
        Assert.Null(await commits.BranchAsync());
        Assert.False(await RunCommits.HasSafeConfigAsync(git!, run));
    }

    [Fact]
    public async Task Missing_partial_clone_blob_cannot_execute_uploadpack()
    {
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        await using var h = new RunnerHarness();
        using var run = RunWorkspace.Create(h.WorkRoot, "dlv_lazy", 1);
        Directory.CreateDirectory(run.WorkTree);
        await Git.RunAsync(run.WorkTree, ["init", "--quiet", "--initial-branch=main"]);
        await CommitAsync(run.WorkTree, "base");
        var commits = new RunCommits(git, run);
        await commits.CaptureAsync();
        await CommitAsync(run.WorkTree, "missing blob");
        var blob = (await Git.RunAsync(run.WorkTree, ["rev-parse", "HEAD:change.txt"])).Trim();
        var loose = Path.Combine(run.WorkTree, ".git", "objects", blob[..2], blob[2..]);
        File.SetAttributes(loose, FileAttributes.Normal); // git writes objects read-only; Windows won't delete those
        File.Delete(loose);
        var marker = Path.Combine(run.Root, "executed");
        await Git.RunAsync(run.WorkTree, ["config", "core.repositoryformatversion", "1"]);
        await Git.RunAsync(run.WorkTree, ["config", "extensions.partialClone", "origin"]);
        await Git.RunAsync(run.WorkTree, ["config", "remote.origin.promisor", "true"]);
        await Git.RunAsync(run.WorkTree, ["config", "remote.origin.url", h.WorkRoot]);
        await Git.RunAsync(run.WorkTree, ["config", "remote.origin.uploadpack", "sh -c 'touch \"" + marker.Replace("\\", "/") + "\"' --"]);
        commits.InspectAfterExit();
        Assert.True(await commits.MustKeepAsync());
        Assert.Null(await commits.BranchAsync());
        Assert.True(Directory.Exists(run.WorkTree));

        using var server = new FakeServer();
        using var http = new HttpClient(server) { BaseAddress = new Uri(FakeServer.Url) };
        server.On(HttpMethod.Get, "/api/w/ws_test/threads/thr_dlv_lazy/messages", _ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[]") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunCodeReturn.ReturnAsync(http, http,
            "ws_test", run, h.Task("dlv_lazy"), "main", commits.BaseCommit, TestContext.Current.CancellationToken,
            (_, _) => throw new InvalidOperationException("Must reject before requesting a key.")));
        Assert.DoesNotContain(server.Requests, r => r.Method == HttpMethod.Put);

        await Git.RunAsync(run.WorkTree, ["config", "protocol.file.allow", "always"]);
        // Exercise transport restrictions independently of the config rejection, including on
        // git versions predating GIT_NO_LAZY_FETCH support.
        using (Git.PrepareWith(start =>
        {
            var args = start.ArgumentList.ToArray();
            start.ArgumentList.Clear();
            foreach (var arg in RunCommits.GitArguments(run, args)) start.ArgumentList.Add(arg);
            foreach (var entry in RunCommits.GitEnvironment(run)) start.Environment[entry.Key] = entry.Value;
        }))
        {
            await Assert.ThrowsAsync<GitException>(() => GitBundles.CreateAsync(run.WorkTree,
                Path.Combine(run.Root, "unsafe.bundle"), "main", commits.BaseCommit));
        }
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Packaging_rechecks_links_after_network_round_trips()
    {
        // Probe before ThrowsAsync, which must still require the product's rejection exception.
        await WindowsRunCapabilities.RequireJunctionsAsync();
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        await using var h = new RunnerHarness();
        using var run = RunWorkspace.Create(h.WorkRoot, "dlv_swap", 1);
        Directory.CreateDirectory(run.WorkTree);
        await Git.RunAsync(run.WorkTree, ["init", "--quiet", "--initial-branch=main"]);
        await CommitAsync(run.WorkTree, "base");
        var commits = new RunCommits(git, run);
        commits.InspectAfterExit();
        Assert.True(run.LocalRepositoryAfterExit);
        using var server = new FakeServer();
        using var http = new HttpClient(server) { BaseAddress = new Uri(FakeServer.Url) };
        server.On(HttpMethod.Get, "/api/w/ws_test/threads/thr_dlv_swap/messages", _ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[]") });
        var swapped = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunCodeReturn.ReturnAsync(http, http,
            "ws_test", run, h.Task("dlv_swap"), "main", null, TestContext.Current.CancellationToken,
            async (_, _) =>
            {
                var pack = Path.Combine(run.WorkTree, ".git", "objects", "pack");
                Directory.Delete(pack);
                await LinkOutsideAsync(pack, Directory.CreateDirectory(Path.Combine(h.WorkRoot, "outside-pack")).FullName);
                swapped = true;
                return null!; // Rejected before any key is used or upload begins.
            }));
        Assert.True(swapped);
        Assert.DoesNotContain(server.Requests, r => r.Method == HttpMethod.Put);
        Assert.True(Directory.Exists(run.WorkTree));
    }

    [Fact]
    public async Task Only_local_branches_tags_and_head_count_as_new_commits()
    {
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        await using var h = new RunnerHarness();
        using var run = RunWorkspace.Create(h.WorkRoot, "dlv_refs", 1);
        Directory.CreateDirectory(run.WorkTree);
        await Git.RunAsync(run.WorkTree, ["init", "--quiet", "--initial-branch=main"]);
        await CommitAsync(run.WorkTree, "base");
        var commits = new RunCommits(git, run);
        await commits.CaptureAsync();
        await CommitAsync(run.WorkTree, "fetched");
        var fetched = await Git.CommitIdAsync(run.WorkTree, "HEAD");
        await Git.RunAsync(run.WorkTree, ["reset", "--hard", commits.BaseCommit!]);
        await Git.RunAsync(run.WorkTree, ["update-ref", "refs/remotes/origin/new", fetched]);
        await Git.RunAsync(run.WorkTree, ["update-ref", "refs/remotes/mbus/new", fetched]);
        commits.InspectAfterExit();
        Assert.False(await commits.MustKeepAsync());
        await Git.RunAsync(run.WorkTree, ["branch", "other", fetched]);
        Assert.True(await commits.MustKeepAsync());
        Assert.True(await commits.HasOtherNewAsync("main"));
        Assert.False(await commits.HasOtherNewAsync("other"));
        await Git.RunAsync(run.WorkTree, ["branch", "-D", "other"]);
        await Git.RunAsync(run.WorkTree, ["tag", "new", fetched]);
        Assert.True(await commits.MustKeepAsync());
        await Git.RunAsync(run.WorkTree, ["tag", "-d", "new"]);
        await Git.RunAsync(run.WorkTree, ["checkout", "--detach", fetched]);
        Assert.True(await commits.MustKeepAsync());
    }
}
