using MessageBus.Cli.Runner.Resources;
using MessageBus.Contracts.Runner;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class LinuxResourceSamplerTests
{
    [Fact]
    public async Task Nested_v2_mount_uses_fractional_quota_and_parent_memory_headroom()
    {
        var fixture = new ProcFixture();
        fixture.V2();
        fixture.Files["/sys/fs/cgroup/team/cpu.max"] = "150000 100000\n";
        fixture.Files["/sys/fs/cgroup/team/memory.max"] = "2147483648\n";
        fixture.Files["/sys/fs/cgroup/team/memory.current"] = "1879048192\n";
        fixture.Files["/sys/fs/cgroup/team/job/memory.max"] = "4294967296\n";
        fixture.Files["/sys/fs/cgroup/team/job/memory.current"] = "1073741824\n";
        fixture.Files["/sys/fs/cgroup/team/job/cpu.stat"] = "usage_usec 3000000\nuser_usec 2000000\nsystem_usec 1000000\n";
        var first = await fixture.Sample();
        Assert.Null(first.WindowMs);
        Assert.Null(first.Cpu!.UtilizationPercent);
        Assert.Equal(8, first.Cpu.HostLogicalCpus);
        Assert.Equal(1.5, first.Cpu.EffectiveCpus);
        Assert.Equal(ResourceLimitSource.CgroupV2, first.Cpu.LimitSource);
        Assert.Equal(16L * 1024 * 1024 * 1024, first.Memory!.HostTotalBytes);
        Assert.Equal(2L * 1024 * 1024 * 1024, first.Memory.TotalBytes);
        Assert.Equal(256L * 1024 * 1024, first.Memory.AvailableBytes);
        Assert.Equal(ResourceLimitSource.CgroupV2, first.Memory.LimitSource);

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        // Wall clock adjustments must not change the measurement interval.
        fixture.SamplingClock.UtcOffset = TimeSpan.FromHours(1);
        fixture.Files["/sys/fs/cgroup/team/job/cpu.stat"] = "usage_usec 4500000\n";
        var second = await fixture.Sample();
        Assert.Equal(2000, second.WindowMs);
        Assert.Equal(50d, second.Cpu!.UtilizationPercent);
        Assert.Equal(fixture.Clock.GetUtcNow().AddHours(1), second.SampledAt);
    }

    [Fact]
    public async Task Cpu_sets_and_memory_are_bounded_and_usage_reset_requires_a_new_baseline()
    {
        var fixture = new ProcFixture();
        fixture.V2();
        var leaf = "/sys/fs/cgroup/team/job";
        fixture.Files[leaf + "/cpuset.cpus.effective"] = "0-1,4\n";
        fixture.Files[leaf + "/memory.max"] = "2147483648\n";
        fixture.Files[leaf + "/memory.current"] = "3221225472\n";
        fixture.Files[leaf + "/cpu.stat"] = "usage_usec 9000000\n";
        var first = await fixture.Sample();
        Assert.Equal(3d, first.Cpu!.EffectiveCpus);
        Assert.Equal(0, first.Memory!.AvailableBytes);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Files[leaf + "/cpu.stat"] = "usage_usec 1000000\n";
        Assert.Null((await fixture.Sample()).Cpu!.UtilizationPercent);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Files[leaf + "/cpu.stat"] = "usage_usec 1000000\n";
        Assert.Equal(0d, (await fixture.Sample()).Cpu!.UtilizationPercent);
    }

    [Fact]
    public async Task V1_separate_controllers_inherit_ancestor_limits_and_cpuacct_nanoseconds()
    {
        var fixture = new ProcFixture();
        fixture.Files["/proc/self/cgroup"] = "2:cpu,cpuacct:/pool/agent/child\n3:memory:/pool/agent/child\n4:cpuset:/pool/agent/child\n";
        fixture.Files["/proc/self/mountinfo"] = """
            31 25 0:28 /pool /cg/cpu rw - cgroup cgroup rw,cpu,cpuacct
            32 25 0:29 /pool /cg/memory rw - cgroup cgroup rw,memory
            33 25 0:30 /pool /cg/cpuset rw - cgroup cgroup rw,cpuset
            """;
        fixture.Files["/cg/cpu/agent/child/cpu.cfs_quota_us"] = "-1\n";
        fixture.Files["/cg/cpu/agent/cpu.cfs_quota_us"] = "150000\n";
        fixture.Files["/cg/cpu/agent/cpu.cfs_period_us"] = "100000\n";
        fixture.Files["/cg/cpu/agent/child/cpuacct.usage"] = "2000000000\n";
        fixture.Files["/cg/cpuset/agent/cpuset.cpus"] = "2-5\n";
        fixture.Files["/cg/memory/agent/memory.limit_in_bytes"] = "2147483648\n";
        fixture.Files["/cg/memory/agent/memory.usage_in_bytes"] = "1073741824\n";
        fixture.Files["/cg/memory/memory.limit_in_bytes"] = "9223372036854771712\n";
        var first = await fixture.Sample();
        Assert.Equal(1.5, first.Cpu!.EffectiveCpus);
        Assert.Equal(ResourceLimitSource.CgroupV1, first.Cpu.LimitSource);
        Assert.Equal(2147483648L, first.Memory!.TotalBytes);
        Assert.Equal(1073741824L, first.Memory.AvailableBytes);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Files["/cg/cpu/agent/child/cpuacct.usage"] = "3500000000\n";
        Assert.Equal(50d, (await fixture.Sample()).Cpu!.UtilizationPercent);
    }

    [Fact]
    public async Task Host_cpu_ignores_guest_counters_and_individual_counter_reset_warms_up()
    {
        var fixture = new ProcFixture();
        fixture.HostCpu("100 20 30 500 10 5 5 0 80 10");
        await fixture.Sample();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.HostCpu("150 20 40 540 10 5 5 0 130 10");
        var second = await fixture.Sample();
        Assert.Equal(60d, second.Cpu!.UtilizationPercent);
        Assert.Equal(ResourceLimitSource.Host, second.Cpu.LimitSource);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        // Total still increases, but the user counter reset: cannot compute valid utilization.
        fixture.HostCpu("10 20 40 1000 10 5 5 0 0 0");
        Assert.Null((await fixture.Sample()).Cpu!.UtilizationPercent);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.HostCpu("10 20 40 1100 10 5 5 0 0 0");
        Assert.Equal(0d, (await fixture.Sample()).Cpu!.UtilizationPercent);
    }

    [Fact]
    public async Task Owned_trees_form_a_union_and_are_excluded_from_the_runner_with_pid_reuse_warmup()
    {
        var fixture = new ProcFixture();
        fixture.Process(100, 1, 1000, 10, 10);
        fixture.Process(200, 100, 2000, 20, 20);
        fixture.Process(300, 200, 3000, 30, 30);
        fixture.Process(400, 100, 4000, 40, 40);
        fixture.Roots = [200, 300, 200];
        var first = await fixture.Sample();
        Assert.Equal(2, first.Tasks!.ProcessCount);
        Assert.Equal(50 * 4096, first.Tasks.ResidentBytes);
        Assert.Equal(2, first.Runner!.ProcessCount);
        Assert.Equal(50 * 4096, first.Runner.ResidentBytes);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Process(100, 1, 1000, 110, 10);
        fixture.Process(200, 100, 2000, 120, 20);
        fixture.Process(300, 200, 3000, 130, 30);
        fixture.Process(400, 100, 4000, 140, 40);
        var second = await fixture.Sample();
        Assert.Equal(25d, second.Tasks!.CpuPercent); // 2 CPU seconds / 8 host CPUs.
        Assert.Equal(25d, second.Runner!.CpuPercent);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Process(300, 200, 9000, 9999, 30);
        var reused = await fixture.Sample();
        Assert.Null(reused.Tasks!.CpuPercent);
        Assert.Equal("process-root-reused", reused.Tasks.Reason);
        Assert.Null(reused.Tasks.ResidentBytes);
        Assert.Null(reused.Runner!.CpuPercent);
        Assert.Equal("process-task-exclusion-incomplete", reused.Runner.Reason);
    }

    [Fact]
    public async Task Reused_parent_does_not_adopt_an_older_process_and_removed_roots_release_their_identity()
    {
        var fixture = new ProcFixture { Roots = [200] };
        fixture.Process(100, 1, 1000, 0, 10);
        fixture.Process(200, 100, 5000, 0, 20);
        fixture.Process(300, 200, 3000, 0, 30); // Child of an earlier incarnation of PID 200.
        var first = await fixture.Sample();
        Assert.Equal(1, first.Tasks!.ProcessCount);
        Assert.Equal(20 * 4096, first.Tasks.ResidentBytes);
        fixture.Process(200, 100, 6000, 0, 40);
        var reused = await fixture.Sample();
        Assert.Equal("process-root-reused", reused.Tasks!.Reason);
        Assert.Null(reused.Tasks.ProcessCount);
        fixture.Roots = [];
        await fixture.Sample();
        fixture.Roots = [200];
        var newTask = await fixture.Sample();
        Assert.Equal(1, newTask.Tasks!.ProcessCount);
        Assert.Equal(40 * 4096, newTask.Tasks.ResidentBytes);
    }

    [Fact]
    public async Task Unavailable_owned_root_and_permission_failure_do_not_report_idle_or_disclose_details()
    {
        var fixture = new ProcFixture { Roots = [200] };
        fixture.Process(200, 100, 2000, 20, 20);
        fixture.Denied.Add("/proc/200/stat");
        var sample = await fixture.Sample();
        Assert.Equal(ResourceSampleStatus.Partial, sample.Status);
        Assert.Null(sample.Tasks!.CpuPercent);
        Assert.Null(sample.Tasks.ResidentBytes);
        Assert.Null(sample.Tasks.ProcessCount);
        Assert.Equal("process-unavailable", sample.Tasks.Reason);
        Assert.Equal("read-partial", sample.Reason);
        fixture.Denied.Clear();
        fixture.Files.Remove("/proc/200/stat"); // The same outcome for a root that exited.
        Assert.Equal("process-unavailable", (await fixture.Sample()).Tasks!.Reason);
    }

    [Fact]
    public async Task Rss_permission_failure_retains_the_observed_count_but_marks_usage_unknown()
    {
        var fixture = new ProcFixture { Roots = [200] };
        fixture.Process(200, 100, 2000, 20, 20);
        fixture.Denied.Add("/proc/200/statm");
        var sample = await fixture.Sample();
        Assert.Equal(1, sample.Tasks!.ProcessCount);
        Assert.Null(sample.Tasks.ResidentBytes);
        Assert.Equal("process-partial", sample.Tasks.Reason);
        Assert.Equal(ResourceSampleStatus.Partial, sample.Status);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Process(200, 100, 2000, 120, 20);
        var next = await fixture.Sample();
        Assert.Equal(12.5, next.Tasks!.CpuPercent);
        Assert.Null(next.Tasks.ResidentBytes);
    }

    [Fact]
    public async Task Process_cpu_uses_effective_capacity_and_rss_requires_a_stable_identity()
    {
        var fixture = new ProcFixture { Roots = [200] };
        fixture.V2();
        fixture.Files["/sys/fs/cgroup/team/job/cpu.max"] = "150000 100000\n";
        fixture.Process(200, 100, 2000, 0, 20);
        await fixture.Sample();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Process(200, 100, 2000, 75, 20);
        Assert.Equal(50d, (await fixture.Sample()).Tasks!.CpuPercent);
        // Simulate exit/reuse between reading stat and statm, before validating the identity.
        fixture.OnRead = path =>
        {
            if (path == "/proc/200/statm") fixture.Process(200, 100, 9000, 0, 50);
        };
        var raced = await fixture.Sample();
        Assert.Null(raced.Tasks!.ResidentBytes);
        Assert.Equal("process-partial", raced.Tasks.Reason);
    }

    [Theory]
    [InlineData("0::/unmounted/job\n", "31 25 0:28 /other /cg rw - cgroup2 cgroup rw\n")]
    [InlineData("0::/pool/team/job\n", "malformed mountinfo\n")]
    public async Task Unresolved_cgroup_does_not_claim_host_capacity(string membership, string mount)
    {
        var fixture = new ProcFixture();
        fixture.Files["/proc/self/cgroup"] = membership;
        fixture.Files["/proc/self/mountinfo"] = mount;
        var sample = await fixture.Sample();
        Assert.Equal(ResourceSampleStatus.Partial, sample.Status);
        Assert.Equal(8, sample.Cpu!.HostLogicalCpus);
        Assert.Null(sample.Cpu.EffectiveCpus);
        Assert.Null(sample.Memory!.TotalBytes);
        Assert.Null(sample.Memory.AvailableBytes);
    }

    [Fact]
    public async Task Escaped_mount_names_are_decoded_and_limits_never_read_outside_the_mount()
    {
        var fixture = new ProcFixture();
        fixture.Files["/proc/self/cgroup"] = "0::/pool/team/job\n";
        fixture.Files["/proc/self/mountinfo"] = "31 25 0:28 /pool/team /cg\\040space rw - cgroup2 cgroup rw\n";
        fixture.Files["/cg space/job/cpu.max"] = "max 100000\n";
        fixture.Files["/cg space/cpu.max"] = "50000 100000\n";
        fixture.Files["/cg space/job/cpu.stat"] = "usage_usec 0\n";
        fixture.Files["/cpu.max"] = "1000 100000\n";
        Assert.Equal(0.5, (await fixture.Sample()).Cpu!.EffectiveCpus);
        Assert.DoesNotContain("/cpu.max", fixture.ReadPaths);
    }

    [Fact]
    public async Task Malformed_or_unreadable_limits_are_unknown_and_oversized_proc_reads_are_bounded()
    {
        var fixture = new ProcFixture();
        fixture.V2();
        fixture.Files["/sys/fs/cgroup/team/job/cpu.max"] = "not-a-quota\n";
        var malformed = await fixture.Sample();
        Assert.Null(malformed.Cpu!.EffectiveCpus);
        Assert.Equal(ResourceSampleStatus.Partial, malformed.Status);
        fixture.Files["/sys/fs/cgroup/team/job/cpu.max"] = "150000 100000\n";
        fixture.Denied.Add("/sys/fs/cgroup/team/job/memory.max");
        var denied = await fixture.Sample();
        Assert.Null(denied.Memory!.TotalBytes);
        fixture.Denied.Clear();
        fixture.Files["/sys/fs/cgroup/team/job/memory.max"] = "2147483648\n";
        fixture.Denied.Add("/sys/fs/cgroup/team/job/memory.current");
        var missingUsage = await fixture.Sample();
        Assert.Equal(2147483648L, missingUsage.Memory!.TotalBytes);
        Assert.Null(missingUsage.Memory.AvailableBytes);
        Assert.Equal(1.5, missingUsage.Cpu!.EffectiveCpus);
        fixture.Denied.Clear();
        fixture.Files["/proc/stat"] = new string(' ', 131073);
        Assert.Null((await fixture.Sample()).Cpu!.HostLogicalCpus);
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_committing_a_partial_baseline()
    {
        var fixture = new ProcFixture();
        await fixture.Sample();
        using var cancellation = new CancellationTokenSource();
        fixture.OnRead = path => { if (path == "/proc/self/cgroup") cancellation.Cancel(); };
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fixture.SampleWithCancellation(cancellation.Token));
        fixture.OnRead = null;
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2000, (await fixture.Sample()).WindowMs);
    }

    [Fact]
    public async Task Large_process_enumerations_stop_at_the_budget_and_preserve_unknown_usage()
    {
        var fixture = new ProcFixture { EnumerateThrough = 9000 };
        var sample = await fixture.Sample();
        Assert.DoesNotContain("/proc/8193/stat", fixture.ReadPaths);
        Assert.Equal(ResourceSampleStatus.Partial, sample.Status);
        Assert.Equal("process-partial", sample.Runner!.Reason);
        Assert.Null(sample.Runner.ResidentBytes);
        Assert.Null(sample.Runner.CpuPercent);
        Assert.Equal(0, sample.Tasks!.ProcessCount); // An empty ownership snapshot is still known empty.
    }

    // Representative proc/cgroup file records with synthetic process identities. Mutating kernel
    // counters, not sampler return values, exercises discovery, parsing, attribution and deltas.
    private sealed class ProcFixture
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);
        public List<string> ReadPaths { get; } = [];
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public OffsetClock SamplingClock { get; }
        public int[] Roots { get; set; } = [];
        public int? EnumerateThrough { get; set; }
        public Action<string>? OnRead { get; set; }
        private readonly LinuxResourceSampler _sampler;
        private readonly HashSet<int> _pids = [];

        public ProcFixture()
        {
            SamplingClock = new OffsetClock(Clock);
            Files["/proc/self/cgroup"] = "";
            Files["/proc/self/mountinfo"] = "24 1 0:22 / /proc rw - proc proc rw\n";
            Files["/proc/meminfo"] = "MemTotal:       16777216 kB\nMemFree:         1048576 kB\nMemAvailable:    8388608 kB\nBuffers:          131072 kB\n";
            HostCpu("100 20 30 500 10 5 5 0 80 10");
            Process(100, 1, 1000, 0, 10);
            _sampler = new LinuxResourceSampler(path =>
            {
                ReadPaths.Add(path);
                OnRead?.Invoke(path);
                if (Denied.Contains(path)) throw new UnauthorizedAccessException("fixture-private-detail");
                return Files.GetValueOrDefault(path);
            }, () => EnumerateThrough is { } count ? Enumerable.Range(1, count) : _pids);
        }

        public ValueTask<ResourceSamplingResult> Sample() => SampleWithCancellation(TestContext.Current.CancellationToken);

        public ValueTask<ResourceSamplingResult> SampleWithCancellation(CancellationToken cancellation) =>
            _sampler.SampleAsync(new ResourceSamplingContext(SamplingClock, 100, Roots), cancellation);

        public void V2()
        {
            Files["/proc/self/cgroup"] = "0::/pool/team/job\n";
            Files["/proc/self/mountinfo"] = "31 25 0:28 /pool /sys/fs/cgroup rw,nosuid,nodev,noexec - cgroup2 cgroup rw\n";
            Files["/sys/fs/cgroup/team/job/cpu.max"] = "max 100000\n";
            Files["/sys/fs/cgroup/team/job/cpu.stat"] = "usage_usec 0\n";
        }

        public void HostCpu(string counters) => Files["/proc/stat"] = "cpu  " + counters + "\n" +
            string.Join("\n", Enumerable.Range(0, 8).Select(i => $"cpu{i} 10 0 5 100 0 0 0 0 0 0")) + "\nintr 1024\nctxt 4096\nbtime 1700000000\n";

        public void Process(int pid, int parent, long start, long ticks, long residentPages)
        {
            _pids.Add(pid);
            // State starts at field 3; utime=14, stime=15, starttime=22. The command deliberately
            // contains spaces and parentheses so whitespace splitting of the whole line is wrong.
            Files[$"/proc/{pid}/stat"] = $"{pid} (worker (child)) S {parent} 0 0 0 0 0 0 0 0 0 {ticks} 0 0 0 20 0 1 0 {start} 1048576 10 0 0\n";
            Files[$"/proc/{pid}/statm"] = $"256 {residentPages} 4 1 0 32 0\n";
        }
    }

    private sealed class OffsetClock(FakeTimeProvider clock) : TimeProvider
    {
        public TimeSpan UtcOffset { get; set; }
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow() + UtcOffset;
        public override long GetTimestamp() => clock.GetTimestamp();
        public override long TimestampFrequency => clock.TimestampFrequency;
    }
}
