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
    [Fact]
    public async Task Selected_local_failure_is_after_approval_before_checkout_and_does_not_poison_a_cloud_override()
    {
        var env = new Dictionary<string, string> { [OpenCodeAdapter.ConfigContentVariable] = OpenCodeLocalTests.Config() };
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
            with { Model = "local/publisher/coder", RequireRunApproval = true };
        Assert.True((await h.DeliverAsync(local)).Accepted);
        var refused = await h.Service.SettledAsync(local.DeliveryId);
        Assert.Equal(DeclineReason.MachineNotReady, refused.Declined);
        Assert.Equal(SettleStatus.Abandoned, refused.Result.Status);
        Assert.Contains("LM Studio", refused.Result.Error);
        Assert.False(Directory.Exists(Path.GetDirectoryName(h.WorkTreeOf(local.DeliveryId))));
        Assert.Equal(1, calls);
        var cloud = h.Task("dlv_cloud", "opencode") with { Model = "cloud/paid" };
        Assert.True((await h.DeliverAsync(cloud)).Accepted);
        Assert.Equal(SettleStatus.Succeeded, (await h.Service.SettledAsync(cloud.DeliveryId)).Result.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_blocked_local_probe_does_not_hold_the_engine_lock_or_delay_cloud_admission()
    {
        var env = new Dictionary<string, string> { [OpenCodeAdapter.ConfigContentVariable] = OpenCodeLocalTests.Config(cloudDefault: true) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new RunnerHarness(environment: env, openCodeCommand: RunnerHarness.FakeAgentPath, sandbox: _ => new PromptInputSandbox(),
            extensions: e => e with
            {
                Adapters = _ => OpenCodeAdapter.ForRunner("2.3.0", env.GetValueOrDefault, ".", () => new OpenCodeLocalTests.Handler(async (r, ct) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    return OpenCodeLocalTests.Json(r.RequestUri!.AbsolutePath == "/api/v1/models" ? OpenCodeLocalTests.Native : OpenCodeLocalTests.Models);
                })),
            });
        Assert.True((await h.DeliverAsync(h.Task("dlv_wait", "opencode") with { Model = "local/publisher/coder" })).Accepted);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True((await h.DeliverAsync(h.Task("dlv_cloud_now", "opencode") with { Model = "cloud/paid" })).Accepted);
        Assert.Equal(SettleStatus.Succeeded, (await h.Service.SettledAsync("dlv_cloud_now")).Result.Status);
        Assert.False(Directory.Exists(h.WorkTreeOf("dlv_wait")));
        release.TrySetResult();
        Assert.Equal(SettleStatus.Succeeded, (await h.Service.SettledAsync("dlv_wait")).Result.Status);
    }

    // The fake CLI reads its directives from stdin; real OpenCode reads the saved prompt file.
    private sealed class PromptInputSandbox : IRunSandbox
    {
        public bool IsolationEnabled => false;
        public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile) =>
            launch with { StandardInput = File.ReadAllText(workspace.PromptFile) };
    }
}
