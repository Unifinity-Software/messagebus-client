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

        await PreparePushAsync(context, remote);
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

    [Theory]
    [InlineData("create", "POST,PATCH", true)]
    [InlineData("existing", "", false)]
    [InlineData("conflict", "POST,GET", false)]
    [InlineData("reject", "", false)]
    public async Task Pushes_source_and_handles_azure_devops_pull_request(string scenario, string followup, bool bearer)
    {
        const string jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJleGFtcGxlIn0.signature";
        const string azurePull = """{"pullRequestId":42,"createdBy":{"id":"example-creator"}}""";
        const string pulls = "/acme/Example%20Project/_apis/git/repositories/example-repo/pullrequests";
        const string version = "?api-version=7.1";
        const string source = "refs/heads/topic/reviewed";
        const string target = "refs/heads/main";
        const string repo = "acme/Example Project/example-repo";
        const string repositoryUrl = "https://dev.azure.com/acme/Example%20Project/_git/example-repo";
        var ct = TestContext.Current.CancellationToken;
        await using var harness = new RunnerHarness();
        using var directory = new ScratchDirectory();
        using var log = new StringWriter();
        var context = CreateContext(harness, directory.Path, log, autoMerge: scenario != "conflict", azureRepo: repo) with
        {
            InputBranches = new Dictionary<string, string> { ["implement"] = "topic/reviewed" },
        };
        var remote = Path.Combine(directory.Path, "remote.git");
        await PreparePushAsync(context, remote);
        var input = Directory.CreateDirectory(Path.Combine(context.InputsDirectory, "implement")).FullName;
        var summary = "Implemented due dates. " + new string('x', 4000);
        await File.WriteAllTextAsync(Path.Combine(input, "summary.txt"), summary, ct);
        var secret = bearer ? jwt : Token;
        var credentials = new Credentials(secret);
        var methods = new List<string>();
        using var http = new HttpClient(new FakeAzureDevOps(async (request, token) =>
        {
            methods.Add(request.Method.Method);
            Assert.Equal("api.example.test", request.RequestUri!.Host);
            Assert.Equal(bearer ? "Bearer" : "Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal(bearer ? jwt : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + Token)),
                request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal(source, await GitAsync(remote, "for-each-ref", "--format=%(refname)"));
                Assert.Equal(pulls, request.RequestUri.AbsolutePath);
                Assert.Equal("?searchCriteria.sourceRefName=refs%2Fheads%2Ftopic%2Freviewed"
                    + "&searchCriteria.targetRefName=refs%2Fheads%2Fmain&searchCriteria.status=active" + "&api-version=7.1",
                    request.RequestUri.Query);
                if (scenario == "reject")
                    return new(HttpStatusCode.Forbidden) { Content = new StringContent(secret) };
                var existing = scenario == "existing"
                    ? """{"pullRequestId":42,"autoCompleteSetBy":{"id":"example-creator"}}"""
                    : methods.Count > 1 ? azurePull : "";
                return new(HttpStatusCode.OK) { Content = new StringContent($$"""{"value":[{{existing}}]}""") };
            }

            Assert.Equal(version, request.RequestUri.Query);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var body = json.RootElement;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(pulls, request.RequestUri.AbsolutePath);
                Assert.Equal(source, body.GetProperty("sourceRefName").GetString());
                Assert.Equal(target, body.GetProperty("targetRefName").GetString());
                Assert.Equal("Add due dates", body.GetProperty("title").GetString());
                Assert.Equal(($"## implement{Environment.NewLine}{Environment.NewLine}{summary}")[..4000],
                    body.GetProperty("description").GetString());
                return scenario == "conflict" ? new(HttpStatusCode.Conflict)
                    : new(HttpStatusCode.Created) { Content = new StringContent(azurePull) };
            }

            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal(pulls + "/42", request.RequestUri.AbsolutePath);
            Assert.Equal("example-creator", body.GetProperty("autoCompleteSetBy").GetProperty("id").GetString());
            Assert.Equal("squash", body.GetProperty("completionOptions").GetProperty("mergeStrategy").GetString());
            Assert.Equal(2, body.EnumerateObject().Count());
            return new(HttpStatusCode.OK);
        })) { BaseAddress = new Uri("https://api.example.test/") };

        var result = await new OpenPullRequestAction(credentials, pushDestination: remote, azureDevOpsHttp: http)
            .RunAsync(context, ct);

        Assert.True(credentials.Request?.CanPush);
        Assert.Equal(repositoryUrl, credentials.Request?.Repository.AbsoluteUri);
        Assert.Equal(followup.Length == 0 ? "GET" : "GET," + followup, string.Join(",", methods));
        Assert.DoesNotContain(secret, log.ToString());
        if (scenario == "reject")
        {
            Assert.Equal(SettleStatus.Failed, result.Status);
            Assert.Equal("failed", result.Outcome);
            Assert.Equal("Look up the open pull request failed (HTTP 403).", result.Summary);
            Assert.Equal(result.Summary, result.Error);
            Assert.Empty(result.Refs);
            return;
        }

        Assert.Equal(SettleStatus.Succeeded, result.Status);
        Assert.Equal("opened", result.Outcome);
        var reference = Assert.Single(result.Refs);
        Assert.Equal("ado.pr", reference.Kind);
        Assert.Equal(repo, reference.Repo);
        Assert.Equal(42, reference.Number);
        Assert.Equal(repositoryUrl + "/pullrequest/42", reference.Url);
    }

    private static async Task PreparePushAsync(RunnerActionContext context, string remote)
    {
        await GitAsync(context.WorkDirectory, "init");
        await GitAsync(context.WorkDirectory, "switch", "-c", "topic/reviewed");
        await GitAsync(context.WorkDirectory, "-c", "user.name=Example", "-c", "user.email=dev@example.test",
            "-c", "commit.gpgSign=false", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
            "commit", "--allow-empty", "-m", "Reviewed work");
        await GitAsync(context.WorkDirectory, "switch", "-c", "unrelated");
        await GitAsync(context.RunDirectory, "init", "--bare", remote);
    }

    private static RunnerActionContext CreateContext(RunnerHarness harness, string root, TextWriter log, bool autoMerge,
        string? azureRepo = null)
    {
        var delivery = harness.Task("dlv_open_pr");
        return new RunnerActionContext
        {
            Delivery = delivery with
            {
                Envelope = delivery.Envelope with
                {
                    Refs = [azureRepo is null ? new Ref { Kind = "github.issue", Repo = "example/repo" }
                        : new Ref { Kind = "ado.workitem", Repo = azureRepo }],
                },
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

    private sealed class Credentials(string secret = Token) : IRepoCredentialSource
    {
        public string Description => "Test credentials";
        public RepoCredentialRequest? Request { get; private set; }

        public Task<RepoCredential?> IssueAsync(RepoCredentialRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult<RepoCredential?>(new RepoCredential
            {
                UserName = "x-access-token", Secret = secret, ExpiresAt = DateTimeOffset.MaxValue,
            });
        }
    }

    private sealed class FakeAzureDevOps(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request, ct);
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
