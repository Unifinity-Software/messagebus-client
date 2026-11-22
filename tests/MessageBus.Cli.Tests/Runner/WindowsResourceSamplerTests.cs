using MessageBus.Cli.Runner.Resources;
using MessageBus.Contracts.Runner;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static MessageBus.Cli.Runner.Resources.WindowsResourceSampler;

namespace MessageBus.Cli.Tests.Runner;

public sealed class WindowsResourceSamplerTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deltas_use_monotonic_window_and_deduplicate_owned_trees_excluding_them_from_runner()
    {
        var time = new FakeTimeProvider();
        var snapshot = Observation();
        var sampler = new WindowsResourceSampler((_, _) => snapshot);
        var context = new ResourceSamplingContext(time, 10, [20, 21, 20, 30]);
        var first = await sampler.SampleAsync(context, Cancel);
        Assert.Null(first.WindowMs);
        Assert.Null(first.Cpu!.UtilizationPercent);
        Assert.Null(first.Runner!.CpuPercent);
        Assert.Null(first.Tasks!.CpuPercent);
        Assert.Equal(1, first.Runner.ProcessCount);
        Assert.Equal(3, first.Tasks.ProcessCount);
        Assert.Equal(100, first.Runner.ResidentBytes);
        Assert.Equal(900, first.Tasks.ResidentBytes);

        time.Advance(TimeSpan.FromSeconds(15));
        snapshot = snapshot with
        {
            System = new(160, 400, 300),
            Processes = snapshot.Processes.ToDictionary(p => p.Key,
                p => p.Value with { CpuTicks = p.Value.CpuTicks + (p.Key == 30 ? 0UL : 150_000_000UL) }),
        };
        var result = await sampler.SampleAsync(context, Cancel);
        Assert.Equal(ResourceSampleStatus.Ok, result.Status);
        Assert.Equal(time.GetUtcNow(), result.SampledAt);
        Assert.Equal(15000, result.WindowMs);
        Assert.Equal(85, result.Cpu!.UtilizationPercent);
        Assert.Equal(4, result.Cpu.EffectiveCpus);
        Assert.Equal(25, result.Runner!.CpuPercent);
        Assert.Equal(50, result.Tasks!.CpuPercent);
        Assert.Equal(10000, result.Memory!.TotalBytes);
        Assert.Equal(2500, result.Memory.AvailableBytes);
        Assert.Equal(ResourceLimitSource.Host, result.Memory.LimitSource);
    }

    [Fact]
    public async Task Measured_idle_is_zero_but_new_processes_do_not_have_a_complete_cpu_baseline()
    {
        var time = new FakeTimeProvider();
        var snapshot = Observation();
        var sampler = new WindowsResourceSampler((_, _) => snapshot);
        var context = new ResourceSamplingContext(time, 10, [20]);
        await sampler.SampleAsync(context, Cancel);
        time.Advance(TimeSpan.FromSeconds(15));
        snapshot = snapshot with { System = new(200, 300, 100) };
        var idle = await sampler.SampleAsync(context, Cancel);
        Assert.Equal(0, idle.Cpu!.UtilizationPercent);
        Assert.Equal(0, idle.Tasks!.CpuPercent);

        time.Advance(TimeSpan.FromSeconds(15));
        var processes = snapshot.Processes.ToDictionary();
        processes[22] = new(20, 40, 10, 500);
        snapshot = snapshot with { Processes = processes, System = new(300, 400, 100) };
        var changed = await sampler.SampleAsync(context, Cancel);
        Assert.Null(changed.Tasks!.CpuPercent);
        Assert.Equal(1000, changed.Tasks.ResidentBytes);
        Assert.Equal(3, changed.Tasks.ProcessCount);
        Assert.Equal("windows_process_cpu_baseline_incomplete", changed.Tasks.Reason);
        Assert.Equal(ResourceSampleStatus.Partial, changed.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exited_or_inaccessible_task_roots_never_look_like_an_empty_measured_tree(bool inaccessible)
    {
        var snapshot = Observation();
        var processes = snapshot.Processes.ToDictionary();
        if (inaccessible) processes[20] = new(10);
        else processes.Remove(20);
        snapshot = snapshot with { Processes = processes };
        var result = await new WindowsResourceSampler((_, _) => snapshot)
            .SampleAsync(new(new FakeTimeProvider(), 10, [20]), Cancel);
        Assert.Equal(ResourceSampleStatus.Partial, result.Status);
        Assert.Null(result.Tasks!.CpuPercent);
        Assert.Null(result.Tasks.ResidentBytes);
        Assert.Null(result.Tasks.ProcessCount);
        Assert.NotNull(result.Tasks.Reason);
        Assert.NotNull(result.Runner!.Reason);
        Assert.Null(result.Runner.ResidentBytes);
    }

    [Fact]
    public async Task Reused_parent_pid_cannot_adopt_older_children_and_reused_owned_root_stays_unknown()
    {
        var time = new FakeTimeProvider();
        var snapshot = Observation();
        var processes = snapshot.Processes.ToDictionary();
        processes[20] = processes[20] with { Created = 1000 };
        snapshot = snapshot with { Processes = processes };
        var sampler = new WindowsResourceSampler((_, _) => snapshot);
        var context = new ResourceSamplingContext(time, 10, [20]);
        var first = await sampler.SampleAsync(context, Cancel);
        Assert.Equal(1, first.Tasks!.ProcessCount);
        Assert.Equal(200, first.Tasks.ResidentBytes); // Old child 21 does not belong to new parent 20.
        time.Advance(TimeSpan.FromSeconds(15));
        processes = snapshot.Processes.ToDictionary();
        processes[20] = processes[20] with { Created = 2000, ResidentBytes = 999 };
        snapshot = snapshot with { Processes = processes, System = new(200, 400, 200) };
        var reused = await sampler.SampleAsync(context, Cancel);
        Assert.Equal("windows_process_root_reused", reused.Tasks!.Reason);
        Assert.Null(reused.Tasks.ResidentBytes);
        Assert.Null(reused.Tasks.CpuPercent);
        time.Advance(TimeSpan.FromSeconds(15));
        var again = await sampler.SampleAsync(context, Cancel);
        Assert.Equal("windows_process_root_reused", again.Tasks!.Reason);
        // Once the caller removes the old root, it can explicitly register a new run with that PID.
        await sampler.SampleAsync(context with { OwnedRunRootProcessIds = [] }, Cancel);
        var registered = await sampler.SampleAsync(context, Cancel);
        Assert.Equal(999, registered.Tasks!.ResidentBytes);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(null, false)]
    public async Task Immediate_job_limits_never_masquerade_as_verified_effective_limits(bool? inJob, bool queried)
    {
        var time = new FakeTimeProvider();
        // A 25% immediate cap may itself be nested beneath an unqueryable 50% ancestor.
        var snapshot = Observation() with { Job = new(inJob, queried, 5, 2500, 1000, 3) };
        var sampler = new WindowsResourceSampler((_, _) => snapshot);
        var context = new ResourceSamplingContext(time, 10, [20]);
        await sampler.SampleAsync(context, Cancel);
        time.Advance(TimeSpan.FromSeconds(15));
        snapshot = snapshot with { System = new(200, 400, 200) };
        var result = await sampler.SampleAsync(context, Cancel);
        Assert.Equal(ResourceSampleStatus.Partial, result.Status);
        Assert.Contains(queried ? "windows_job_hierarchy_unavailable" : "windows_job_query_failed", result.Reason);
        Assert.Equal(4, result.Cpu!.HostLogicalCpus);
        Assert.Null(result.Cpu.EffectiveCpus);
        Assert.Null(result.Cpu.UtilizationPercent);
        Assert.Equal(inJob == true ? ResourceLimitSource.JobObject : null, result.Cpu.LimitSource);
        Assert.Equal(10000, result.Memory!.HostTotalBytes);
        Assert.Null(result.Memory.TotalBytes);
        Assert.Null(result.Memory.AvailableBytes);
        Assert.Equal(500, result.Tasks!.ResidentBytes);
        Assert.Null(result.Tasks.CpuPercent);
    }

    [Fact]
    public async Task Temporary_identity_read_failure_does_not_permanently_reject_a_registered_root()
    {
        var time = new FakeTimeProvider();
        var original = Observation();
        var snapshot = original;
        var sampler = new WindowsResourceSampler((_, _) => snapshot);
        var context = new ResourceSamplingContext(time, 10, [20]);
        await sampler.SampleAsync(context, Cancel);
        time.Advance(TimeSpan.FromSeconds(15));
        var processes = original.Processes.ToDictionary();
        processes[20] = new(10);
        snapshot = original with { Processes = processes, System = new(200, 400, 200) };
        var inaccessible = await sampler.SampleAsync(context, Cancel);
        Assert.Equal("windows_process_identity_unavailable", inaccessible.Tasks!.Reason);
        time.Advance(TimeSpan.FromSeconds(15));
        snapshot = original with { System = new(300, 600, 300) };
        var recovered = await sampler.SampleAsync(context, Cancel);
        Assert.Equal(500, recovered.Tasks!.ResidentBytes);
        Assert.Equal(2, recovered.Tasks.ProcessCount);
        Assert.Null(recovered.Tasks.CpuPercent);
        Assert.Equal("windows_process_cpu_baseline_incomplete", recovered.Tasks.Reason);
    }

    [Fact]
    public async Task Counter_reset_and_clock_change_discard_cpu_baselines()
    {
        var time = new FakeTimeProvider();
        var snapshot = Observation();
        var sampler = new WindowsResourceSampler((_, _) => snapshot);
        var context = new ResourceSamplingContext(time, 10, []);
        await sampler.SampleAsync(context, Cancel);
        time.Advance(TimeSpan.FromSeconds(15));
        snapshot = snapshot with { System = new(0, 0, 0) };
        var reset = await sampler.SampleAsync(context, Cancel);
        Assert.Null(reset.Cpu!.UtilizationPercent);
        Assert.Contains("windows_cpu_counter_reset", reset.Reason);
        var newClock = await sampler.SampleAsync(context with { TimeProvider = new FakeTimeProvider() }, Cancel);
        Assert.Null(newClock.WindowMs);
        Assert.Null(newClock.Runner!.CpuPercent);
        Assert.Null(newClock.Cpu!.UtilizationPercent);
    }

    [Fact]
    public async Task Snapshot_failure_and_multiple_processor_groups_are_explicitly_partial()
    {
        var snapshot = Observation() with
        {
            HostLogicalCpus = 128, Memory = null, Processes = new Dictionary<int, ProcessReading>(),
            Reason = "windows_process_snapshot_unavailable",
        };
        var result = await new WindowsResourceSampler((_, _) => snapshot)
            .SampleAsync(new(new FakeTimeProvider(), 10, []), Cancel);
        Assert.Equal(ResourceSampleStatus.Partial, result.Status);
        Assert.Contains("windows_processor_groups_unsupported", result.Reason);
        Assert.Contains("windows_memory_unavailable", result.Reason);
        Assert.Null(result.Cpu!.UtilizationPercent);
        Assert.Null(result.Memory!.HostTotalBytes);
        Assert.Null(result.Runner!.ResidentBytes);
        Assert.Null(result.Tasks!.ResidentBytes);
    }

    [Fact]
    public async Task Cancellation_does_not_consume_a_baseline()
    {
        var time = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var sampler = new WindowsResourceSampler((_, _) =>
        {
            cancellation.Cancel();
            return Observation();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sampler.SampleAsync(new(time, 10, []), cancellation.Token).AsTask());
        var first = await sampler.SampleAsync(new(time, 10, []), Cancel);
        Assert.Null(first.WindowMs);
        Assert.Null(first.Cpu!.UtilizationPercent);
    }

    [Fact]
    public async Task Native_windows_collection_reads_own_process_and_host_without_launching_children()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await new WindowsResourceSampler().SampleAsync(
            new(TimeProvider.System, Environment.ProcessId, []), Cancel);
        Assert.NotEqual(ResourceSampleStatus.Error, result.Status);
        Assert.True(result.Cpu!.HostLogicalCpus > 0);
        Assert.True(result.Memory!.HostTotalBytes > 0);
        // Other parallel tests can launch and reap children while Toolhelp is walking this tree.
        // Their lost identities must be reported as partial, rather than fabricated resident bytes.
        if (result.Runner!.ResidentBytes is { } resident) Assert.True(resident > 0);
        else Assert.Equal("windows_process_identity_unavailable", result.Runner.Reason);
        Assert.True(result.Runner.ProcessCount >= 1);
        Assert.Null(result.Runner.CpuPercent);
        Assert.Null(result.Cpu.UtilizationPercent);
        Assert.Equal(0, result.Tasks!.ProcessCount);
        Assert.Equal(0, result.Tasks.ResidentBytes);
    }

    private static Snapshot Observation() => new(4, new(100, 200, 100), new(10000, 2500), new(false),
        new Dictionary<int, ProcessReading>
        {
            [10] = new(0, 10, 100, 100),
            [20] = new(10, 20, 100, 200),
            [21] = new(20, 30, 100, 300),
            [30] = new(0, 40, 100, 400),
        });
}
