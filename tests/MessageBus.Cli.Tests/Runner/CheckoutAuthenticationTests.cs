using MessageBus.Attachments.Client;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class CheckoutAuthenticationTests
{
    [Theory]
    [InlineData(false, "https://GIT.EXAMPLE.TEST/team/private.GIT")]
    [InlineData(true, "https://git.example.test/team/private.git")]
    public async Task A_workflow_checkout_without_a_bundle_authenticates_through_the_real_sandbox(bool ownerGit, string url)
    {
        await WindowsRunCapabilities.RequireGitShellAsync();
        var ct = TestContext.Current.CancellationToken;
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        await using var h = new RunnerHarness();
        var source = Directory.CreateDirectory(Path.Combine(h.Scratch, "source")).FullName;
        await Git.RunAsync(source, ["init", "--quiet"], ct);
        await File.WriteAllTextAsync(Path.Combine(source, "README.md"), "Private repository", ct);
        await Git.RunAsync(source, ["add", "."], ct);
        await Git.RunAsync(source, ["-c", "user.name=Test", "-c", "user.email=test@example.test", "-c", "commit.gpgsign=false",
            "commit", "--quiet", "-m", "Initial"], ct);
        Directory.CreateDirectory(h.Home);
        if (ownerGit)
            await File.WriteAllTextAsync(Path.Combine(h.Home, ".gitconfig"),
                "[credential]\n\thelper =\n\thelper = \"!f() { echo username=fixture; echo password=fixture-password; }; f\"\n", ct);
        using var run = RunWorkspace.Create(h.WorkRoot, "dlv_authentication", 1);
        var task = h.Task("dlv_authentication", environment: new RunEnvironment { Checkout = CheckoutMode.Bundle });
        task = task with
        {
            Envelope = task.Envelope with { Refs = [new Ref { Kind = "git.repo", Url = url }] },
            StepContext = new StepContext { JobId = "job_test", StepId = "code", StepName = "Code", Round = 1, AllowedOutcomes = [], Inputs = [] },
        };
        var profile = new PermissionProfile { Name = "worker" };
        var isolation = RunIsolation.Create(new RunIsolationOptions
        {
            WorkRoot = h.WorkRoot, HomeDirectory = h.Home, Requested = IsolationLevel.Basic,
            Credentials = new FixtureCredential(), UseOwnerGit = _ => ownerGit,
        }, TextWriter.Null);
        await isolation.PrepareAsync(run, task, profile, ct);
        var authenticated = false;
        try
        {
            var error = await run.CheckOutAsync(task, _ => git, null, ct, launch =>
            {
                launch = isolation.Wrap(launch, run, profile);
                if (launch.Arguments.Contains("clone"))
                {
                    // Before substituting the local test transport, ask real Git to authenticate with
                    // precisely the clone's scrubbed environment and the original address.
                    var answer = new List<string>();
                    using var probe = AgentProcess.Start(launch with
                    {
                        Arguments = ["credential", "fill"], StandardInput = $"url={url}\n\n",
                    }, answer.Add);
                    Assert.Equal(0, probe.WaitForExitAsync().GetAwaiter().GetResult());
                    Assert.Contains("password=fixture-password", answer);
                    authenticated = true;
                }
                return TestRepositoryRemote.Map(launch, url, source);
            });
            Assert.True(error is null, error);
            Assert.True(authenticated);
            Assert.Equal("Private repository", await File.ReadAllTextAsync(Path.Combine(run.WorkTree, "README.md"), ct));
        }
        finally { isolation.Ended(run); }
    }

    internal sealed class FixtureCredential : IRepoCredentialSource
    {
        public string Description => "Fixture repository credential";
        public Task<RepoCredential?> IssueAsync(RepoCredentialRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<RepoCredential?>(new RepoCredential
            {
                UserName = "fixture", Secret = "fixture-password", ExpiresAt = DateTimeOffset.MaxValue,
            });
    }
}
