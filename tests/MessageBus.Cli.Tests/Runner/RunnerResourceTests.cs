using System.Collections.Concurrent;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Resources;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class RunnerResourceTests
{
    [Fact]
    public async Task Idle_status_and_heartbeats_have_independent_collection_and_negotiation()
    {
        var sampler = new RecordingSampler();
        await using var h = new RunnerHarness(extensions: e => e with { ResourceSampler = sampler });
        await h.Engine.CurrentResourceCollection;
        var initial = await ReadStatusAsync(h);
        Assert.Equal(1L, initial.Sequence);

        await h.Engine.HeartbeatAsync();
        Assert.Null((await h.Service.NextHeartbeatAsync()).Resources);
        h.Service.OnHeartbeat = _ => new HeartbeatAck { SupportsResourceSamples = true };
        await h.Engine.HeartbeatAsync();
        Assert.Null((await h.Service.NextHeartbeatAsync()).Resources);

        h.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Single(sampler.Contexts);
        Assert.Equal(2, h.Service.Heartbeats.Count);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.Engine.CurrentResourceCollection;
        Assert.Equal(2, sampler.Contexts.Count);
        Assert.Equal(2L, (await ReadStatusAsync(h)).Sequence);
        var beat = await h.Service.NextHeartbeatAsync();
        Assert.Equal(0, beat.SlotsBusy);
        Assert.NotNull(beat.Resources);
    }

    [Theory]
    [InlineData(60, 15)]
    [InlineData(5, 5)]
    public async Task Busy_reporting_honors_resource_and_lease_cadence_and_collects_owned_roots(int leaseSeconds, int reportSeconds)
    {
        var sampler = new RecordingSampler();
        var sandbox = new RecordingSandbox();
        await using var h = new RunnerHarness(sandbox: _ => sandbox, extensions: e => e with { ResourceSampler = sampler });
        await h.Engine.CurrentResourceCollection;
        var initial = await ReadStatusAsync(h);
        h.Service.OnHeartbeat = _ => new HeartbeatAck { SupportsResourceSamples = true };
        await h.Engine.HeartbeatAsync();
        Assert.Null((await h.Service.NextHeartbeatAsync()).Resources);

        const string deliveryId = "dlv_resource";
        var delivery = h.Task(deliveryId, directives: d => d["mode"] = "hang") with { HeartbeatSeconds = leaseSeconds };
        Assert.True((await h.DeliverAsync(delivery)).Accepted);
        var processId = await sandbox.StartedAsync();

        h.Time.Advance(TimeSpan.FromSeconds(reportSeconds - 1));
        Assert.Single(h.Service.Heartbeats);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        var beat = await h.Service.NextHeartbeatAsync();
        Assert.Equal([deliveryId], beat.ActiveDeliveryIds);
        Assert.Equal(1, beat.SlotsBusy);
        Assert.NotNull(beat.Resources);
        if (reportSeconds < 15)
        {
            Assert.Equal(initial, beat.Resources);
            Assert.Single(sampler.Contexts);
            h.Time.Advance(TimeSpan.FromSeconds(15 - reportSeconds));
        }

        await h.Engine.CurrentResourceCollection;
        Assert.Equal(2, sampler.Contexts.Count);
        var ownedRoot = Assert.Single(sampler.Contexts.Last().OwnedRunRootProcessIds);
        Assert.Equal(processId, ownedRoot);
        Assert.NotEqual(Environment.ProcessId, ownedRoot);
        Assert.Equal(Environment.ProcessId, sampler.Contexts.Last().RunnerProcessId);
        var busy = await ReadStatusAsync(h);
        Assert.Equal(2L, busy.Sequence);
        Assert.Equal(h.Time.GetUtcNow(), busy.SampledAt);

        Assert.True(h.Engine.Cancel(new CancelDelivery { DeliveryId = deliveryId, ThreadId = delivery.Envelope.ThreadId }));
        await h.Service.SettledAsync(deliveryId);
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await h.Engine.CurrentResourceCollection;
        Assert.Empty(sampler.Contexts.Last().OwnedRunRootProcessIds);
    }

    [Fact]
    public async Task Negotiation_resets_on_reconnect_and_ignores_ack_from_previous_connection()
    {
        await using var h = new RunnerHarness(extensions: e => e with { ResourceSampler = new RecordingSampler() });
        await h.Engine.CurrentResourceCollection;
        h.Engine.SessionId = "session_one";
        h.Service.OnHeartbeat = _ => new HeartbeatAck { SupportsResourceSamples = true };
        await h.Engine.HeartbeatAsync();
        Assert.Null((await h.Service.NextHeartbeatAsync()).Resources);
        await h.Engine.HeartbeatAsync();
        Assert.NotNull((await h.Service.NextHeartbeatAsync()).Resources);

        h.Service.OnHeartbeat = _ =>
        {
            h.Engine.SetResourceConnectionAvailable(false);
            h.Engine.SetResourceConnectionAvailable(true);
            return new HeartbeatAck { SupportsResourceSamples = true };
        };
        await h.Engine.HeartbeatAsync();
        Assert.NotNull((await h.Service.NextHeartbeatAsync()).Resources);

        h.Service.OnHeartbeat = _ => new HeartbeatAck { SupportsResourceSamples = true };
        await h.Engine.HeartbeatAsync();
        var reconnect = await h.Service.NextHeartbeatAsync();
        Assert.Equal("session_one", reconnect.SessionId);
        Assert.Null(reconnect.Resources);

        h.Service.OnHeartbeat = _ => new HeartbeatAck { SupportsResourceSamples = false };
        await h.Engine.HeartbeatAsync();
        Assert.NotNull((await h.Service.NextHeartbeatAsync()).Resources);
        await h.Engine.HeartbeatAsync();
        Assert.Null((await h.Service.NextHeartbeatAsync()).Resources);

        h.Service.OnHeartbeat = _ => new HeartbeatAck { SupportsResourceSamples = true };
        await h.Engine.HeartbeatAsync();
        await h.Service.NextHeartbeatAsync();
        h.Engine.SessionId = "session_two";
        await h.Engine.HeartbeatAsync();
        Assert.Null((await h.Service.NextHeartbeatAsync()).Resources);
    }

    [Fact]
    public async Task Slow_sampling_does_not_overlap_or_block_heartbeats_and_errors_are_safe()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sampler = new RecordingSampler(async cancellationToken =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            throw new IOException("platform diagnostics must stay local");
        });
        await using var h = new RunnerHarness(extensions: e => e with { ResourceSampler = sampler });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        h.Service.OnHeartbeat = _ => new HeartbeatAck { SupportsResourceSamples = true };
        await h.Engine.HeartbeatAsync();
        await h.Service.NextHeartbeatAsync();

        h.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Null((await h.Service.NextHeartbeatAsync()).Resources);
        Assert.Single(sampler.Contexts);

        release.SetResult();
        await h.Engine.CurrentResourceCollection;
        var status = await ReadStatusAsync(h);
        Assert.Equal(ResourceSampleStatus.Error, status.Status);
        Assert.Equal("collection-failed", status.Reason);
        Assert.Null(status.Cpu);
        Assert.Null(status.Memory);
        Assert.Null(status.Runner);
        Assert.Null(status.Tasks);
    }

    [Fact]
    public async Task Protected_process_with_unknown_root_reports_partial_attribution()
    {
        var sampler = new RecordingSampler();
        var sandbox = new RecordingSandbox(protectedLaunch: true);
        await using var h = new RunnerHarness(sandbox: _ => sandbox, extensions: e => e with { ResourceSampler = sampler });
        await h.Engine.CurrentResourceCollection;
        const string deliveryId = "dlv_protected";
        Assert.True((await h.DeliverAsync(h.Task(deliveryId, directives: d => d["mode"] = "hang"))).Accepted);
        await sandbox.StartedAsync();

        h.Time.Advance(TimeSpan.FromSeconds(15));
        await h.Engine.CurrentResourceCollection;
        var status = await ReadStatusAsync(h);
        Assert.Empty(sampler.Contexts.Last().OwnedRunRootProcessIds);
        Assert.Equal(ResourceSampleStatus.Partial, status.Status);
        Assert.Equal("task-roots-unknown", status.Reason);
        Assert.Null(status.Runner);
        Assert.Null(status.Tasks);
    }

    private sealed class RecordingSandbox(bool protectedLaunch = false) : IRunSandbox
    {
        private readonly TaskCompletionSource<int> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool IRunSandbox.UsesPrivateHome => protectedLaunch;

        public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile) => launch;

        public void Started(RunWorkspace workspace, int processId) => _started.TrySetResult(processId);

        public Task<int> StartedAsync() =>
            _started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private static async Task<RunnerResourceSample> ReadStatusAsync(RunnerHarness h)
    {
        var response = await h.Routes.HandleAsync("GET", RunnerRoutes.Status, null, TestContext.Current.CancellationToken);
        Assert.Equal(200, response.StatusCode);
        return Assert.IsType<RunnerResourceSample>(MessageBusJson.Deserialize<RunnerStatus>(response.Json!)!.Resources);
    }

    private sealed class RecordingSampler(Func<CancellationToken, Task>? beforeSample = null) : ISystemResourceSampler
    {
        public ConcurrentQueue<ResourceSamplingContext> Contexts { get; } = new();

        public async ValueTask<ResourceSamplingResult> SampleAsync(ResourceSamplingContext context, CancellationToken cancellationToken)
        {
            Contexts.Enqueue(context);
            if (beforeSample is not null) await beforeSample(cancellationToken);
            return new ResourceSamplingResult(context.TimeProvider.GetUtcNow(), 500, ResourceSampleStatus.Ok,
                Runner: new RunnerProcessTreeSample { ProcessCount = 1 },
                Tasks: new RunnerProcessTreeSample { ProcessCount = context.OwnedRunRootProcessIds.Count });
        }
    }
}
