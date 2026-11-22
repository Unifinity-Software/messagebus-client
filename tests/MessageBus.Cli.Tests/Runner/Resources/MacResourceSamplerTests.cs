using MessageBus.Cli.Runner.Resources;
using MessageBus.Contracts.Runner;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using ProcessObservation = MessageBus.Cli.Runner.Resources.MacResourceSampler.ProcessObservation;
using ProcessStart = MessageBus.Cli.Runner.Resources.MacResourceSampler.ProcessStart;

namespace MessageBus.Cli.Tests.Runner.Resources;

public sealed class MacResourceSamplerTests
{
    [Fact]
    public void HostCpuDeltaIncludesNiceTimeAndHandlesRollover()
    {
        uint[] previous = [uint.MaxValue - 4, 20, 40, 0];
        uint[] current = [5, 25, 65, 10];

        Assert.Equal(50, MacResourceSampler.CpuPercent(previous, current));
        Assert.Null(MacResourceSampler.CpuPercent(current, current));
    }

    [Fact]
    public void ProcessInfoParsesDarwinLayoutAndPreservesBsdOnlyIdentity()
    {
        var data = new byte[232];
        BitConverter.GetBytes(25).CopyTo(data, 12);
        BitConverter.GetBytes(17).CopyTo(data, 16);
        BitConverter.GetBytes(42UL).CopyTo(data, 120);
        BitConverter.GetBytes(87UL).CopyTo(data, 128);
        BitConverter.GetBytes(8192L).CopyTo(data, 144);
        BitConverter.GetBytes(210UL).CopyTo(data, 152);
        BitConverter.GetBytes(90UL).CopyTo(data, 160);

        Assert.Equal(new ProcessObservation(25, 17, new ProcessStart(42, 87), 8192, 300),
            MacResourceSampler.ParseProcess(25, data));
        Assert.Equal(new ProcessObservation(25, 17, new ProcessStart(42, 87), null, null),
            MacResourceSampler.ParseProcess(25, data.AsSpan(0, 136)));
        Assert.Null(MacResourceSampler.ParseProcess(26, data));
        Assert.Null(MacResourceSampler.ParseProcess(25, data.AsSpan(0, 135)));
    }

    [Fact]
    public void NestedTaskRootsAreCountedOnceAndExcludedFromRunner()
    {
        var runnerRoot = new ProcessObservation(1, 0, new ProcessStart(2, 0), 10, 50);
        var taskRoot = new ProcessObservation(2, 1, new ProcessStart(3, 0), 20, 50);
        var nestedTask = new ProcessObservation(3, 2, new ProcessStart(3, 1), null, null);
        var sibling = new ProcessObservation(4, 1, new ProcessStart(4, 0), 40, 50);
        var staleChild = new ProcessObservation(5, 2, new ProcessStart(1, 0), 50, 50);
        var children = new[] { runnerRoot, taskRoot, nestedTask, sibling, staleChild }.ToLookup(p => p.ParentPid);
        var tasks = new Dictionary<int, ProcessObservation>();
        var runner = new Dictionary<int, ProcessObservation>();

        MacResourceSampler.AddTree(nestedTask, children, tasks, null, CancellationToken.None);
        MacResourceSampler.AddTree(taskRoot, children, tasks, null, CancellationToken.None);
        MacResourceSampler.AddTree(runnerRoot, children, runner, tasks, CancellationToken.None);

        Assert.Equal(new[] { 2, 3 }, tasks.Keys.Order());
        Assert.Equal(new[] { 1, 4 }, runner.Keys.Order());
        var sample = MacResourceSampler.SummarizeTree(tasks, [], false, 0, 1, false, true);
        Assert.Equal(2, sample.ProcessCount);
        Assert.Equal(20, sample.ResidentBytes);
        Assert.Null(sample.CpuPercent);
        Assert.Equal("process-tree-partial", sample.Reason);
    }

    [Fact]
    public void ProcessCpuUsesNativeFrequencyUnderRosettaAndIgnoresReusedPid()
    {
        var stable = new ProcessObservation(1, 0, new ProcessStart(1, 0), 100, 5_000_000);
        var reused = new ProcessObservation(2, 1, new ProcessStart(4, 0), 200, 500_000_000);
        var sample = MacResourceSampler.SummarizeTree(
            new() { [1] = stable, [2] = reused },
            new() { [1] = stable with { CpuTicks = 1_000_000 }, [2] = reused with { Start = new ProcessStart(2, 0) } },
            true, 2, 24_000_000, false, true);

        // Native proc_taskinfo counters still run at 24 MHz when Rosetta's mach_timebase_info says 1/1.
        Assert.Equal(8.333333333, Assert.IsType<double>(sample.CpuPercent), 8);
        Assert.Equal(300, sample.ResidentBytes);
        Assert.Equal("process-tree-partial", sample.Reason);

        var unavailable = MacResourceSampler.SummarizeTree([], [], true, 1, 1, true, false);
        Assert.Null(unavailable.ProcessCount);
        Assert.Null(unavailable.ResidentBytes);
        Assert.Null(unavailable.CpuPercent);
        Assert.Equal("process-tree-unavailable", unavailable.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void MissingOrInvalidProcessFrequencyReportsUnknownCpu(long? ticksPerSecond)
    {
        var process = new ProcessObservation(1, 0, new ProcessStart(1, 0), 100, 48_000_000);
        var sample = MacResourceSampler.SummarizeTree(new() { [1] = process },
            new() { [1] = process with { CpuTicks = 24_000_000 } }, true, 1, ticksPerSecond, false, true);
        Assert.Null(sample.CpuPercent);
        Assert.Equal(100, sample.ResidentBytes);
        Assert.Equal("process-tree-partial", sample.Reason);
    }

    [Fact]
    public async Task SamplesRealMacHostAndCurrentProcess()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var clock = new FakeTimeProvider();
        var sampler = new MacResourceSampler();
        var context = new ResourceSamplingContext(clock, Environment.ProcessId, []);
        var first = await sampler.SampleAsync(context, TestContext.Current.CancellationToken);
        Assert.Null(first.WindowMs);
        Assert.Null(first.Cpu?.UtilizationPercent);
        Assert.True(first.Cpu?.HostLogicalCpus > 0);
        Assert.Equal(ResourceLimitSource.Host, first.Cpu?.LimitSource);
        var total = Assert.IsType<long>(first.Memory?.TotalBytes);
        Assert.True(total > 0);
        Assert.InRange(Assert.IsType<long>(first.Memory?.AvailableBytes), 0, total);
        Assert.True(first.Runner?.ProcessCount > 0);
        Assert.True(first.Runner?.ResidentBytes > 0);
        Assert.Null(first.Runner?.CpuPercent);
        Assert.Equal(0, first.Tasks?.ProcessCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await sampler.SampleAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(1000, second.WindowMs);
        Assert.InRange(Assert.IsType<double>(second.Runner?.CpuPercent), 0, 100);
    }
}
