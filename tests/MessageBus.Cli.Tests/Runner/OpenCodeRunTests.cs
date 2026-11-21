using MessageBus.Cli.Agents;
using MessageBus.Cli.Readiness;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class OpenCodeRunTests
{
    [Theory]
    [InlineData("local/publisher/coder")]
    [InlineData("local/alternative")]
    public async Task Unverified_local_confinement_is_refused_after_approval_before_checkout_without_poisoning_cloud(string model)
    {
        var env = new Dictionary<string, string> { [OpenCodeAdapter.ConfigContentVariable] = OpenCodeLocalTests.Config().Replace("\"hidden\":", "\"alternative\":{\"modelID\":\"api-b\"},\"hidden\":", StringComparison.Ordinal) };
        var calls = 0;
        var approved = false;
        var readiness = new RunnerReadiness(TextWriter.Null);
        await using var h = new RunnerHarness(environment: env, openCodeCommand: RunnerHarness.FakeAgentPath, sandbox: _ => new PromptInputSandbox(),
            extensions: e => e with
            {
                Readiness = readiness,
                Adapters = _ => OpenCodeAdapter.ForRunner("2.3.0", env.GetValueOrDefault, ".", () =>
                {
                    Assert.True(approved);
                    Interlocked.Increment(ref calls);
                    return OpenCodeLocalTests.DownHandler();
                }),
            });
        h.Service.OnRunApproval = _ =>
        {
            approved = true;
            return new RunApprovalState { ApprovalId = "apr_local", Status = RunApprovalStatus.Approved };
        };
        var local = h.Task("dlv_local", "opencode", environment: new RunEnvironment { Checkout = CheckoutMode.Empty, SetupCommand = "exit 99" })
            with { Model = model, RequireRunApproval = true };
        Assert.True((await h.DeliverAsync(local)).Accepted);
        var refused = await h.Service.SettledAsync(local.DeliveryId);
        Assert.Equal(DeclineReason.MachineNotReady, refused.Declined);
        Assert.Equal(SettleStatus.Abandoned, refused.Result.Status);
        Assert.True(approved);
        Assert.Contains("local-only inference is not verified", refused.Result.Error);
        Assert.False(Directory.Exists(Path.GetDirectoryName(h.WorkTreeOf(local.DeliveryId))));
        Assert.Equal(0, calls);
        var cloud = h.Task("dlv_cloud", "opencode") with { Model = "cloud/paid" };
        Assert.True((await h.DeliverAsync(cloud)).Accepted);
        Assert.Equal(SettleStatus.Succeeded, (await h.Service.SettledAsync(cloud.DeliveryId)).Result.Status);
        Assert.Equal(0, calls);
    }

    // The fake CLI reads its directives from stdin; real OpenCode reads the saved prompt file.
    private sealed class PromptInputSandbox : IRunSandbox
    {
        public bool IsolationEnabled => false;
        public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile) =>
            launch with { StandardInput = File.ReadAllText(workspace.PromptFile) };
    }
}
