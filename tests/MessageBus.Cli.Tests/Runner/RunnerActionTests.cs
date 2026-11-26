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

    [Theory]
    [InlineData(0, "passed")]
    [InlineData(7, "failed")]
    public async Task A_published_command_runs_in_the_checkout_and_settles_with_its_exit_code_and_output(int exit, string outcome)
    {
        var sandbox = new RecordingSandbox();
        await using var runner = new RunnerHarness(sandbox: _ => sandbox);
        var command = OperatingSystem.IsWindows()
            ? $"type marker.txt & exit /b {exit}"
            : $"cat marker.txt; exit {exit}";
        var delivery = CommandTask(runner, command, folder: "check");

        Assert.True((await runner.DeliverAsync(delivery)).Accepted);
        var settle = await runner.Service.SettledAsync(delivery.DeliveryId);
        Assert.Equal(SettleStatus.Succeeded, settle.Result.Status);
        Assert.Equal(outcome, settle.Result.Outcome);
        Assert.Equal(exit, settle.Result.ExitCode);
        Assert.Equal("from the checkout", settle.Result.Summary);
        Assert.True(sandbox.ProcessStarted);
    }

    [Fact]
    public async Task The_time_limit_stops_the_running_command()
    {
        var sandbox = new RecordingSandbox();
        await using var runner = new RunnerHarness(sandbox: _ => sandbox);
        var command = OperatingSystem.IsWindows()
            ? "ping -t 127.0.0.1"
            : "sleep 600 & wait";
        var delivery = CommandTask(runner, command, timeLimitMinutes: 1);

        Assert.True((await runner.DeliverAsync(delivery)).Accepted);
        await sandbox.StartedSignal.Task.WaitAsync(TestContext.Current.CancellationToken);
        runner.Time.Advance(TimeSpan.FromMinutes(1));
        var result = (await runner.Service.SettledAsync(delivery.DeliveryId)).Result;

        Assert.Equal(SettleStatus.Succeeded, result.Status);
        Assert.Equal("failed", result.Outcome);
        Assert.Contains("time limit", result.Summary);
    }

    [Fact]
    public async Task An_action_cannot_use_a_folder_outside_the_checkout()
    {
        var sandbox = new RecordingSandbox();
        await using var runner = new RunnerHarness(sandbox: _ => sandbox);
        var delivery = CommandTask(runner, "echo unexpected", folder: "check/../..");

        Assert.True((await runner.DeliverAsync(delivery)).Accepted);
        var result = (await runner.Service.SettledAsync(delivery.DeliveryId)).Result;

        Assert.Equal("failed", result.Outcome);
        Assert.Contains("inside the checkout", result.Summary);
        Assert.False(sandbox.ProcessStarted);
    }

    private static TaskDelivery CommandTask(RunnerHarness runner, string command, string? folder = null, int? timeLimitMinutes = null) =>
        runner.Task("dlv_command", agent: "missing-agent", environment: new RunEnvironment
        {
            Checkout = CheckoutMode.Empty,
            SetupCommand = "exit 1",
        }) with
        {
            RunnerAction = new RunnerAction
            {
                Kind = RunnerActionKinds.RunCommand,
                RunCommand = new RunCommandParameters { Command = command, Folder = folder, TimeLimitMinutes = timeLimitMinutes },
            },
        };

    private sealed class RecordingSandbox : IRunSandbox
    {
        public TaskCompletionSource StartedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ProcessStarted => StartedSignal.Task.IsCompleted;

        public async Task PrepareAsync(RunWorkspace workspace, TaskDelivery delivery, PermissionProfile profile, CancellationToken cancellationToken)
        {
            var check = Directory.CreateDirectory(Path.Combine(workspace.WorkTree, "check")).FullName;
            await File.WriteAllTextAsync(Path.Combine(check, "marker.txt"), "from the checkout", cancellationToken);
        }

        public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile) => launch;

        public void Started(RunWorkspace workspace, int processId) => StartedSignal.TrySetResult();
    }
}
