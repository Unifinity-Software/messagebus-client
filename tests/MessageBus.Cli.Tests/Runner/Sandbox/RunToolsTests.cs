using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Roles;
using MessageBus.Mcp;
using MessageBus.Tests.Shared;
using Xunit;

namespace MessageBus.Cli.Tests.Runner.Sandbox;

/// <summary>
/// the original issue: each run's mbus tools are served by the runner with a credential for that run alone. The end-to-end
/// acceptance test runs a task whose agent has no mbus settings or key at all; these cover what it can't see:
/// the credential and its end, and the scope the runner holds every call to.
/// </summary>
public sealed class RunToolsTests
{
    [Fact]
    public async Task Only_a_connection_with_the_runs_credential_reaches_its_tools_and_only_while_the_run_lasts()
    {
        await using var h = new RunnerHarness();
        using var workspace = RunWorkspace.Create(h.WorkRoot, "dlv_tools", 1);
        var served = 0;
        var tools = new PipeRunTools(async (_, stream, ct) =>
        {
            Interlocked.Increment(ref served);
            await stream.WriteAsync("served\n"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
        }, TextWriter.Null);

        var session = await tools.StartAsync(new RunToolsRequest(workspace, h.Task("dlv_tools"), new PermissionProfile { Name = "worker" }, l => l), CancellationToken.None);
        var file = session.Variables[RunToolsVariables.File];
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        var (pipe, token) = ((string)json["pipe"]!, (string)json["token"]!);

        await using (session)
        {
            string? greeting = null;
            try
            {
                greeting = await GreetAsync(pipe, "not-the-token");
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && InsideRun.Now)
            {
                Assert.Skip("Windows run sandbox denies connecting to the current-user named pipe, needed to test tool authentication; runs outside this sandbox and in CI.");
            }
            Assert.Equal("refused", greeting);
            Assert.Equal(0, served);
            Assert.Equal("ok served", await GreetAsync(pipe, token));
            Assert.Equal(1, served);
        }

        // The run is over: its file is gone and nothing answers on its pipe any more.
        Assert.False(File.Exists(file));
        await Assert.ThrowsAnyAsync<Exception>(() => GreetAsync(pipe, token, TimeSpan.FromMilliseconds(300)));
        Assert.Equal(1, served);
    }

    [Fact]
    public async Task A_worker_run_may_act_only_on_its_own_task_and_the_threads_it_started()
    {
        await using var h = new RunnerHarness();
        var delivery = h.Task("dlv_a") with { Envelope = h.Task("dlv_a").Envelope with { ThreadId = "thr_a" } };
        var scope = RunToolScope.For(delivery, Path.GetTempPath());
        var started = new HashSet<string> { "thr_child" };

        Assert.Null(scope.Check(McpToolNames.Progress, Args(new { threadId = "thr_a", message = "Building" }), started));
        Assert.Null(scope.Check(McpToolNames.FetchAttachment, Args(new { threadId = "thr_a", attachmentId = "att_1", deliveryId = "dlv_a" }), started));

        // Tools its role doesn't show it, another task's thread or delivery.
        Assert.Equal(McpErrorCodes.Forbidden, scope.Check(McpToolNames.Directory, Args(new { }), started)?.Code);
        Assert.Equal("threadId", scope.Check(McpToolNames.FetchAttachment, Args(new { threadId = "thr_other", attachmentId = "att_1" }), started)?.Field);
        Assert.Equal("deliveryId", scope.Check(McpToolNames.Settle, Args(new { threadId = "thr_a", deliveryId = "dlv_other" }), started)?.Field);

        // A role that allows sending: within its thread (a follow-up, a sub-task), and following what it started.
        var sender = RunToolScope.For(delivery with
        {
            RunEnvironment = delivery.RunEnvironment with { PermissionProfile = new PermissionProfile { Name = "worker", AllowedTools = ["mcp__mbus__send", "mcp__mbus__wait"] } },
        }, Path.GetTempPath());
        Assert.Null(sender.Check(McpToolNames.Send, Args(new { to = "role:reviewer", type = "task.review", parentThreadId = "thr_a" }), started));
        Assert.Null(sender.Check(McpToolNames.Wait, Args(new { threadIds = new[] { "thr_child" } }), started));
        Assert.NotNull(sender.Check(McpToolNames.Send, Args(new { to = "role:reviewer", type = "task.review" }), started));
        Assert.NotNull(sender.Check(McpToolNames.Wait, Args(new { threadIds = new[] { "thr_child", "thr_someone_elses" } }), started));
    }

    [Fact]
    public async Task A_runs_tools_read_and_write_only_inside_its_run_folder()
    {
        await WindowsRunCapabilities.RequireJunctionsAsync();
        await using var h = new RunnerHarness();
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-scope-tests", Guid.NewGuid().ToString("N"))).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-scope-tests", Guid.NewGuid().ToString("N"))).FullName;
        var link = Path.Combine(root, "work", "escape");
        try
        {
            var scope = RunToolScope.For(h.Task("dlv_a"), root);
            Directory.CreateDirectory(Path.Combine(root, "work", "src"));

            Assert.True(scope.Contains(Path.Combine(root, "work", "src", "a.patch")));
            Assert.False(scope.Contains(Path.Combine(root, "..", Path.GetFileName(outside), "secret")));
            Assert.False(scope.Contains(Path.Combine(outside, "secret")));

            // A link inside the run that leads out of it (an agent can make one: a junction needs no rights on Windows)
            // doesn't count as inside.
            if (OperatingSystem.IsWindows())
            {
                using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, outside])
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                })!;
                await mklink.WaitForExitAsync();
                Assert.Equal(0, mklink.ExitCode);
            }
            else
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            Assert.False(scope.Contains(Path.Combine(link, "secret")));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link); // the link itself, not what it leads to
            }
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task A_repository_whose_object_store_leads_out_of_the_run_by_any_way_isnt_packaged()
    {
        await WindowsRunCapabilities.RequireJunctionsAsync();
        await using var h = new RunnerHarness();
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-scope-tests", Guid.NewGuid().ToString("N"))).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-scope-tests", Guid.NewGuid().ToString("N"))).FullName;
        var links = new List<string>();
        try
        {
            var scope = RunToolScope.For(h.Task("dlv_a"), root);
            var theirs = Directory.CreateDirectory(Path.Combine(outside, ".git", "objects")).FullName;
            string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([root, .. parts])).FullName;
            void Alternate(string objects, string to) => File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(objects, "info")).FullName, "alternates"), to + "\n");

            // A real .git whose objects folder is a link to another repository's.
            Folder("linked", ".git");
            await LinkAsync(Path.Combine(root, "linked", ".git", "objects"), theirs, links);
            Assert.Contains("borrows objects from outside", scope.RepositoryProblem(Path.Combine(root, "linked")));

            // A pack folder linked out of a real objects folder.
            Folder("packs", ".git", "objects");
            await LinkAsync(Path.Combine(root, "packs", ".git", "objects", "pack"), theirs, links);
            Assert.Contains("borrows objects from outside", scope.RepositoryProblem(Path.Combine(root, "packs")));

            // A linked worktree whose common folder is in the run but borrows its objects from outside.
            var worktree = Folder("worktree");
            Alternate(Folder("common", "objects"), theirs);
            Folder("worktree-git");
            File.WriteAllText(Path.Combine(root, "worktree-git", "commondir"), "../common\n");
            File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(root, "worktree-git")}\n");
            Assert.Contains($"({theirs})", scope.RepositoryProblem(worktree));

            // Alternates that lead out two steps away; an in-run cycle is fine.
            var middle = Folder("middle", ".git", "objects");
            Alternate(Folder("nested", ".git", "objects"), middle);
            Alternate(middle, theirs);
            Assert.Contains($"({theirs})", scope.RepositoryProblem(Path.Combine(root, "nested")));
            var a = Folder("a", ".git", "objects");
            var b = Folder("b", ".git", "objects");
            Alternate(a, b);
            Alternate(b, a);
            Assert.Null(scope.RepositoryProblem(Path.Combine(root, "a")));
        }
        finally
        {
            foreach (var link in links)
            {
                Directory.Delete(link); // the link itself, before what it leads to
            }
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>A folder link: a junction on Windows (no rights needed, as an agent would make it), else a symbolic link.</summary>
    private static async Task LinkAsync(string link, string target, List<string> links)
    {
        if (OperatingSystem.IsWindows())
        {
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            await mklink.WaitForExitAsync();
            Assert.Equal(0, mklink.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
        links.Add(link);
    }

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    /// <summary>Connects, sends the greeting with <paramref name="token"/>, and returns what came back (lines joined by spaces).</summary>
    private static async Task<string> GreetAsync(string pipe, string token, TimeSpan? connectTimeout = null)
    {
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync((int)(connectTimeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds);
        await client.WriteAsync(Encoding.ASCII.GetBytes($"{PipeRunTools.Greeting} {token}\n"));
        await client.FlushAsync();
        using var reader = new StreamReader(client);
        return (await reader.ReadToEndAsync()).Trim().Replace('\n', ' ');
    }
}
