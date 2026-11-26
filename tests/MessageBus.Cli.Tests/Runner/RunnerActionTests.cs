using MessageBus.Cli.Runner;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class RunnerActionTests
{
    [Fact]
    public async Task An_action_the_runner_has_no_handler_for_is_accepted_and_settled_without_starting_an_agent()
    {
        var sandbox = new RecordingSandbox();
        await using var runner = new RunnerHarness(sandbox: _ => sandbox);
        runner.Service.OnRunApproval = _ => new RunApprovalState { ApprovalId = "apr_test", Status = RunApprovalStatus.Declined };
        var delivery = runner.Task("dlv_unknown_action") with
        {
            // No runner handles roll-back yet, so it stands for any action this runner does not know.
            RunnerAction = new RunnerAction { Kind = RunnerActionKinds.RollBack },
            RequireRunApproval = true,
        };

        var decision = await runner.DeliverAsync(delivery);

        Assert.True(decision.Accepted);
        Assert.Null(decision.Reason);
        var settle = await runner.Service.SettledAsync(delivery.DeliveryId);
        Assert.Null(settle.Declined);
        Assert.Equal(SettleStatus.Failed, settle.Result.Status);
        Assert.Equal("refused", settle.Result.FailureCategory);
        Assert.Contains(RunnerActionKinds.RollBack, settle.Result.Error);
        Assert.False(sandbox.ProcessStarted);
        Assert.False(Directory.Exists(runner.WorkTreeOf(delivery.DeliveryId)));
    }

    private sealed class RecordingSandbox : IRunSandbox
    {
        public bool ProcessStarted { get; private set; }

        public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile) => launch;

        public void Started(RunWorkspace workspace, int processId) => ProcessStarted = true;
    }
}
