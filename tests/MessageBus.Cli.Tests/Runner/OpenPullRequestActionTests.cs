using System.Net;
using System.Text.Json;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner.Actions;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class OpenPullRequestActionTests
{
    private const string Token = "fake-action-token";
    private const string Pull = """{"number":42,"html_url":"https://github.com/example/repo/pull/42","node_id":"PR_example"}""";

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Pushes_source_and_opens_or_reuses_pull_request(bool existing, bool autoMerge)
    {
        await using var harness = new RunnerHarness();
        using var directory = new ScratchDirectory();
        using var log = new StringWriter();
        var context = CreateContext(harness, directory.Path, log, autoMerge);
        var remote = Path.Combine(directory.Path, "remote.git");
        var api = new FakeGitHub(existing);
        using var http = new HttpClient(api);
        var credentials = new Credentials();
        var action = new OpenPullRequestAction(credentials, http, remote);

        await GitAsync(context.WorkDirectory, "init");
        await GitAsync(context.WorkDirectory, "switch", "-c", "topic/reviewed");
        await GitAsync(context.WorkDirectory, "-c", "user.name=Example", "-c", "user.email=dev@example.test",
            "-c", "commit.gpgSign=false", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
            "commit", "--allow-empty", "-m", "Reviewed work");
        await GitAsync(context.WorkDirectory, "switch", "-c", "unrelated");
        await GitAsync(directory.Path, "init", "--bare", remote);
        var input = Directory.CreateDirectory(Path.Combine(context.InputsDirectory, "implement")).FullName;
        await File.WriteAllTextAsync(Path.Combine(input, "branch.txt"), "topic/reviewed\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input, "summary.txt"), "Implemented due dates.", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input, "exhibits.json"),
            """[{"name":"preview.png","caption":"Due date editor","mediaType":"image/png","size":12}]""", TestContext.Current.CancellationToken);
        var review = Directory.CreateDirectory(Path.Combine(context.InputsDirectory, "review")).FullName;
        await File.WriteAllTextAsync(Path.Combine(review, "summary.txt"), "Tests reviewed.", TestContext.Current.CancellationToken);

        var result = await action.RunAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(SettleStatus.Succeeded, result.Status);
        Assert.Equal("opened", result.Outcome);
        var reference = Assert.Single(result.Refs);
        Assert.Equal("github.pr", reference.Kind);
        Assert.Equal(42, reference.Number);
        Assert.Equal("https://github.com/example/repo/pull/42", reference.Url);
        Assert.Equal(await GitAsync(context.WorkDirectory, "rev-parse", "refs/heads/topic/reviewed"),
            await GitAsync(remote, "rev-parse", "refs/heads/topic/reviewed"));
        Assert.Equal("refs/heads/topic/reviewed", await GitAsync(remote, "for-each-ref", "--format=%(refname)"));
        Assert.True(credentials.Request?.CanPush);
        Assert.Equal("https://github.com/example/repo.git", credentials.Request?.Repository.AbsoluteUri);
        Assert.Equal(existing ? ["GET"] : ["GET", "POST", "POST"], api.Methods);
        Assert.DoesNotContain(Token, log.ToString());
        Assert.DoesNotContain(Token, result.Summary!);
        Assert.DoesNotContain(Token, await File.ReadAllTextAsync(Path.Combine(remote, "config"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failure_reports_status_without_echoing_remote_response()
    {
        await using var harness = new RunnerHarness();
        using var directory = new ScratchDirectory();
        using var log = new StringWriter();
        var context = CreateContext(harness, directory.Path, log, false) with
        {
            InputBranches = new Dictionary<string, string> { ["implement"] = "topic/reviewed" },
        };
        using var http = new HttpClient(new FakeGitHub(false, reject: true));

        var result = await new OpenPullRequestAction(new Credentials(), http).RunAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(SettleStatus.Failed, result.Status);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal("Look up the open pull request failed (HTTP 403).", result.Summary);
        Assert.DoesNotContain(Token, result.Error!);
        Assert.DoesNotContain(Token, log.ToString());
    }

    private static RunnerActionContext CreateContext(RunnerHarness harness, string root, TextWriter log, bool autoMerge)
    {
        var delivery = harness.Task("dlv_open_pr");
        return new RunnerActionContext
        {
            Delivery = delivery with
            {
                Envelope = delivery.Envelope with { Refs = [new Ref { Kind = "github.issue", Repo = "example/repo" }] },
                RunnerAction = new RunnerAction
                {
                    Kind = RunnerActionKinds.OpenPullRequest,
                    OpenPullRequest = new OpenPullRequestParameters { BranchFrom = "implement", Base = "main", AutoMerge = autoMerge },
                },
                StepContext = new StepContext
                {
                    JobId = "job_example", StepId = "open-pr", StepName = "Open pull request", Round = 1,
                    AllowedOutcomes = ["opened", "failed"],
                    Inputs =
                    [
                        new StepInput { FromStep = "implement", What = "branch", ThreadId = "thr_code" },
                        new StepInput { FromStep = "review", What = "result", ThreadId = "thr_review" },
                    ],
                },
            },
            RunDirectory = root,
            WorkDirectory = Directory.CreateDirectory(Path.Combine(root, "work")).FullName,
            Log = log,
        };
    }

    private static async Task<string> GitAsync(string directory, params string[] args)
    {
        var result = await CommandRunner.RunAsync("git", ["-C", directory, .. args], TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.True(result.ExitCode == 0, result.Error);
        return result.Output.Trim();
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("mbus-open-pr-").FullName;

        public void Dispose() => MessageBus.Cli.Runner.RunWorkspace.TryDelete(Path);
    }

    private sealed class Credentials : IRepoCredentialSource
    {
        public string Description => "Test credentials";
        public RepoCredentialRequest? Request { get; private set; }

        public Task<RepoCredential?> IssueAsync(RepoCredentialRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult<RepoCredential?>(new RepoCredential
            {
                UserName = "x-access-token", Secret = Token, ExpiresAt = DateTimeOffset.MaxValue,
            });
        }
    }

    private sealed class FakeGitHub(bool existing, bool reject = false) : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method.Method);
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Token, request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/repos/example/repo/pulls", request.RequestUri.AbsolutePath);
                Assert.Equal("?state=open&head=example:topic/reviewed", Uri.UnescapeDataString(request.RequestUri.Query));
                return reject ? Response(HttpStatusCode.Forbidden, Token)
                    : Response(HttpStatusCode.OK, existing ? "[" + Pull + "]" : "[]");
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            var content = await request.Content!.ReadAsStringAsync(ct);
            Assert.DoesNotContain(Token, content);
            using var json = JsonDocument.Parse(content);
            var body = json.RootElement;
            if (request.RequestUri.AbsolutePath == "/graphql")
            {
                var query = body.GetProperty("query").GetString()!;
                Assert.Contains("enablePullRequestAutoMerge", query);
                Assert.Contains("mergeMethod: SQUASH", query);
                Assert.Equal("PR_example", body.GetProperty("variables").GetProperty("id").GetString());
                return Response(HttpStatusCode.OK, """{"data":{"enablePullRequestAutoMerge":{"pullRequest":{"id":"PR_example"}}}}""");
            }

            Assert.Equal("/repos/example/repo/pulls", request.RequestUri.AbsolutePath);
            Assert.Equal("topic/reviewed", body.GetProperty("head").GetString());
            Assert.Equal("main", body.GetProperty("base").GetString());
            Assert.Equal("Add due dates", body.GetProperty("title").GetString());
            var description = body.GetProperty("body").GetString()!;
            Assert.Contains("## implement", description);
            Assert.Contains("Implemented due dates.", description);
            Assert.Contains("- preview.png — Due date editor", description);
            Assert.Contains("## review", description);
            Assert.Contains("Tests reviewed.", description);
            return Response(HttpStatusCode.Created, Pull);
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body) };
    }
}
