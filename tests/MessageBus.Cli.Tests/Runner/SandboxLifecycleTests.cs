using MessageBus.Cli.Runner;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class SandboxLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_finishes_or_quarantines_before_settle_and_capacity_release(bool cleanupFails)
    {
        var sandbox = new AwaitedSandbox(cleanupFails);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activityCalls = 0;
        bool? lastActivity = null;
        await using var harness = new RunnerHarness(slots: 1, sandbox: _ => sandbox, keepRunFolders: false,
            extensions: extensions => extensions with { WorkActivityChanged = busy =>
            {
                lastActivity = busy;
                if (Interlocked.Increment(ref activityCalls) == 2) finished.TrySetResult();
            } });
        const string id = "dlv_cleanup";
        try
        {
            Assert.True((await harness.DeliverAsync(harness.Task(id))).Accepted);
            await sandbox.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            await harness.Engine.HeartbeatAsync();
            Assert.Equal(1, (await harness.Service.NextHeartbeatAsync()).SlotsBusy);
            Assert.Empty(harness.Service.Settles);
        }
        finally { sandbox.Release.TrySetResult(); }
        var result = (await harness.Service.SettledAsync(id)).Result;
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        if (cleanupFails)
        {
            Assert.Equal(SettleStatus.Abandoned, result.Status);
            Assert.Contains("quarantined", result.Error);
            Assert.True(harness.Engine.GetStatus().Draining);
            await harness.Engine.HeartbeatAsync();
            Assert.True((await harness.Service.NextHeartbeatAsync()).Draining);
            Assert.False((await harness.DeliverAsync(harness.Task("dlv_after_quarantine"))).Accepted);
            Assert.Null(harness.Engine.TryPauseForToolSetup());
            Assert.False((await harness.Engine.StartToolSetupAsync(TestContext.Current.CancellationToken)).CanStart);
            harness.Engine.RequestUpdate(new UpdateRequest { Version = "0.2.0" });
            Assert.False(harness.Engine.UpdateReady.IsCompleted);
            Assert.False(harness.Engine.IsCurrentUpdate(new UpdateRequest { Version = "0.2.0" }));
            Assert.True(lastActivity);
        }
        else Assert.Equal(SettleStatus.Succeeded, result.Status);
        await harness.Engine.StopAsync();
        var root = Path.GetDirectoryName(harness.WorkTreeOf(id))!;
        if (cleanupFails)
        {
            Assert.True(Directory.Exists(root));
            Assert.Contains("9999-12-31", File.ReadAllText(Path.Combine(root, RunWorkspace.HoldFileName)));
        }
        else Assert.False(Directory.Exists(harness.WorkTreeOf(id)));
    }

    private sealed class AwaitedSandbox(bool fails) : IRunSandbox
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _quarantined;
        string? IRunSandbox.AgentRefusal(AgentKind kind) => _quarantined ? "The owned runtime is quarantined." : null;
        string? IRunSandbox.QuarantineProblem => _quarantined ? "The owned runtime is quarantined." : null;
        public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile) => launch;
        public async Task EndedAsync(RunWorkspace workspace)
        {
            Entered.TrySetResult();
            await Release.Task;
            if (!fails) return;
            _quarantined = true;
            workspace.SandboxCleanupIncomplete = true;
            throw new IOException("Unproven runtime shutdown.");
        }
    }
}
