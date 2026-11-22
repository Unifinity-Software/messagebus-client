using System.Runtime.InteropServices;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner.Resources;

internal sealed class MacResourceSampler : ISystemResourceSampler
{
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
    private const string ProcLibrary = "libproc.dylib";
    private const int HostCpuLoadInfo = 3;
    private const int HostVmInfo64 = 4;
    private const uint ProcAllPids = 1;
    private const int ProcPidTaskAllInfo = 2;
    private const int ProcPidBsdInfo = 3;
    private const int BsdInfoSize = 136;
    private const int TaskAllInfoSize = BsdInfoSize + 96;
    private const int NoSuchProcess = 3; // ESRCH
    private const int MaxPidCount = 65536;

    private TimeProvider? previousClock;
    private long previousTimestamp;
    private uint[]? previousTicks;
    private Dictionary<int, ProcessObservation> previousRunner = [];
    private Dictionary<int, ProcessObservation> previousTasks = [];
    private Dictionary<int, ProcessStart> rootIdentities = [];

    public ValueTask<ResourceSamplingResult> SampleAsync(
        ResourceSamplingContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            return ValueTask.FromResult(new ResourceSamplingResult(
                context.TimeProvider.GetUtcNow(), null, ResourceSampleStatus.Unsupported, "platform-unsupported"));
        }

        var clock = context.TimeProvider;
        var timestamp = clock.GetTimestamp();
        var elapsed = ReferenceEquals(clock, previousClock)
            ? clock.GetElapsedTime(previousTimestamp, timestamp)
            : TimeSpan.Zero;
        var hasWindow = elapsed > TimeSpan.Zero;

        var host = mach_host_self();
        uint[]? ticks;
        long? available;
        var totalMemory = ReadMemorySize();
        try
        {
            ticks = ReadCpuTicks(host);
            available = ReadAvailableMemory(host, totalMemory);
        }
        finally
        {
            if (host != 0)
            {
                // mach_host_self acquires a send right on each call.
                mach_port_deallocate(mach_task_self(), host);
            }
        }

        var cpuCount = ReadLogicalCpuCount();
        var utilization = hasWindow && ticks is not null && previousTicks is not null
            ? CpuPercent(previousTicks, ticks)
            : null;
        var cpu = new RunnerCpuSample
        {
            HostLogicalCpus = cpuCount,
            EffectiveCpus = cpuCount,
            UtilizationPercent = utilization,
            LimitSource = cpuCount.HasValue ? ResourceLimitSource.Host : null,
        };
        var memory = new RunnerMemorySample
        {
            HostTotalBytes = totalMemory,
            TotalBytes = totalMemory,
            AvailableBytes = available,
            LimitSource = totalMemory.HasValue ? ResourceLimitSource.Host : null,
        };

        var observations = ReadProcesses(cancellationToken, out var incomplete);
        var children = observations.Values.ToLookup(process => process.ParentPid);
        var nextRootIdentities = new Dictionary<int, ProcessStart>();
        var tasks = new Dictionary<int, ProcessObservation>();
        var tasksIncomplete = false;
        foreach (var pid in context.OwnedRunRootProcessIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetRoot(pid, observations, nextRootIdentities, out var root))
            {
                AddTree(root, children, tasks, excluded: null, cancellationToken);
            }
            else
            {
                tasksIncomplete = true;
            }
        }

        var runner = new Dictionary<int, ProcessObservation>();
        var runnerFound = TryGetRoot(context.RunnerProcessId, observations, nextRootIdentities, out var runnerRoot);
        if (runnerFound)
        {
            AddTree(runnerRoot, children, runner, tasks, cancellationToken);
        }

        var nsPerTick = mach_timebase_info(out var timebase) == 0 && timebase.Denominator != 0
            ? (double)timebase.Numerator / timebase.Denominator
            : 0;
        var cpuBudget = elapsed.TotalSeconds * 1_000_000_000 * (cpuCount ?? 0);
        var runnerSample = SummarizeTree(runner, previousRunner, hasWindow, cpuBudget, nsPerTick,
            incomplete || tasksIncomplete, runnerFound);
        var tasksSample = SummarizeTree(tasks, previousTasks, hasWindow, cpuBudget, nsPerTick,
            context.OwnedRunRootProcessIds.Count > 0 && (incomplete || tasksIncomplete),
            tasks.Count > 0 || context.OwnedRunRootProcessIds.Count == 0);

        cancellationToken.ThrowIfCancellationRequested();
        previousClock = clock;
        previousTimestamp = timestamp;
        previousTicks = ticks;
        previousRunner = runner;
        previousTasks = tasks;
        rootIdentities = nextRootIdentities;

        var partial = ticks is null || cpuCount is null || (hasWindow && utilization is null) ||
            totalMemory is null || available is null || runnerSample.Reason is not null || tasksSample.Reason is not null;
        var failed = ticks is null && cpuCount is null && totalMemory is null && available is null &&
            runnerSample.ProcessCount is null && tasksSample.ProcessCount is null or 0;
        var status = failed ? ResourceSampleStatus.Error : partial ? ResourceSampleStatus.Partial : ResourceSampleStatus.Ok;
        return ValueTask.FromResult(new ResourceSamplingResult(clock.GetUtcNow(),
            hasWindow ? (long)elapsed.TotalMilliseconds : null, status,
            failed ? "macos-collection-failed" : partial ? "macos-collection-partial" : null,
            cpu, memory, runnerSample, tasksSample));
    }

    private static int? ReadLogicalCpuCount()
    {
        nuint length = sizeof(int);
        return sysctlbyname("hw.logicalcpu", out int count, ref length, 0, 0) == 0 &&
            length == sizeof(int) && count > 0 ? count : null;
    }

    private static long? ReadMemorySize()
    {
        nuint length = sizeof(long);
        return sysctlbyname("hw.memsize", out long bytes, ref length, 0, 0) == 0 &&
            length == sizeof(long) && bytes > 0 ? bytes : null;
    }

    private static uint[]? ReadCpuTicks(uint host)
    {
        var ticks = new uint[4];
        uint count = (uint)ticks.Length;
        return host_statistics(host, HostCpuLoadInfo, ticks, ref count) == 0 && count == ticks.Length ? ticks : null;
    }

    internal static double? CpuPercent(ReadOnlySpan<uint> previous, ReadOnlySpan<uint> current)
    {
        // CPU_STATE_USER, SYSTEM, IDLE, NICE. Unsigned subtraction handles Mach's 32-bit rollover.
        ulong busy = (ulong)unchecked(current[0] - previous[0]) +
            unchecked(current[1] - previous[1]) + unchecked(current[3] - previous[3]);
        var total = busy + unchecked(current[2] - previous[2]);
        return total > 0 ? busy * 100.0 / total : null;
    }

    private static long? ReadAvailableMemory(uint host, long? total)
    {
        // HOST_VM_INFO64_REV0_COUNT is 24 Mach integer slots. Only the first three are needed.
        var vm = new uint[24];
        uint count = (uint)vm.Length;
        if (host_statistics64(host, HostVmInfo64, vm, ref count) != 0 || count < 3)
        {
            return null;
        }

        var pageSize = getpagesize();
        if (pageSize <= 0)
        {
            return null;
        }

        // Estimate availability as free plus inactive pages, capped at physical capacity.
        // Inactive pages may need reclaim or writeback. Free already includes speculative pages.
        var estimate = ((long)vm[0] + vm[2]) * pageSize;
        return total.HasValue ? Math.Min(total.Value, estimate) : estimate;
    }

    private static Dictionary<int, ProcessObservation> ReadProcesses(CancellationToken cancellationToken, out bool incomplete)
    {
        var observations = new Dictionary<int, ProcessObservation>();
        incomplete = true;
        var estimatedBytes = proc_listpids(ProcAllPids, 0, null, 0);
        if (estimatedBytes <= 0)
        {
            return observations;
        }

        var capacity = (int)Math.Min(MaxPidCount, (long)estimatedBytes / sizeof(int) + 64);
        int[] pids;
        int bytes;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pids = new int[capacity];
            bytes = proc_listpids(ProcAllPids, 0, pids, capacity * sizeof(int));
            if (bytes <= 0)
            {
                return observations;
            }
            if (bytes < capacity * sizeof(int) || capacity == MaxPidCount)
            {
                break;
            }
            capacity = Math.Min(capacity * 2, MaxPidCount);
        }

        incomplete = bytes >= capacity * sizeof(int) || bytes % sizeof(int) != 0;
        var buffer = new byte[TaskAllInfoSize];
        foreach (var pid in pids.AsSpan(0, Math.Min(bytes / sizeof(int), pids.Length)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pid <= 0 || observations.ContainsKey(pid))
            {
                continue;
            }

            var read = proc_pidinfo(pid, ProcPidTaskAllInfo, 0, buffer, buffer.Length);
            var process = ParseProcess(pid, buffer.AsSpan(0, Math.Clamp(read, 0, buffer.Length)));
            if (process is null)
            {
                // BSD-only data can preserve ancestry even when task counters are inaccessible.
                read = proc_pidinfo(pid, ProcPidBsdInfo, 0, buffer, BsdInfoSize);
                var error = Marshal.GetLastPInvokeError();
                process = ParseProcess(pid, buffer.AsSpan(0, Math.Clamp(read, 0, BsdInfoSize)));
                if (process is null)
                {
                    incomplete |= read != 0 || error != NoSuchProcess;
                    continue;
                }
            }
            observations.Add(pid, process.Value);
        }
        return observations;
    }

    internal static ProcessObservation? ParseProcess(int pid, ReadOnlySpan<byte> data)
    {
        if (data.Length < BsdInfoSize || BitConverter.ToInt32(data[12..]) != pid)
        {
            return null;
        }

        // proc_bsdinfo is 136 bytes on both Darwin 64-bit ABIs. proc_taskinfo follows it in
        // proc_taskallinfo. The total user and system counters are in Mach absolute time units.
        var start = new ProcessStart(BitConverter.ToUInt64(data[120..]), BitConverter.ToUInt64(data[128..]));
        var parent = BitConverter.ToInt32(data[16..]);
        if (data.Length < TaskAllInfoSize)
        {
            return new ProcessObservation(pid, parent, start, null, null);
        }

        var resident = BitConverter.ToInt64(data[144..]);
        var user = BitConverter.ToUInt64(data[152..]);
        var system = BitConverter.ToUInt64(data[160..]);
        return new ProcessObservation(pid, parent, start, resident >= 0 ? resident : null,
            ulong.MaxValue - user >= system ? user + system : null);
    }

    private bool TryGetRoot(int pid, Dictionary<int, ProcessObservation> observations,
        Dictionary<int, ProcessStart> nextRootIdentities, out ProcessObservation root)
    {
        var found = observations.TryGetValue(pid, out root);
        if (rootIdentities.TryGetValue(pid, out var identity))
        {
            nextRootIdentities[pid] = identity;
            return found && root.Start == identity;
        }
        if (found)
        {
            nextRootIdentities[pid] = root.Start;
        }
        return found;
    }

    internal static void AddTree(ProcessObservation root, ILookup<int, ProcessObservation> children,
        Dictionary<int, ProcessObservation> tree, Dictionary<int, ProcessObservation>? excluded,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<ProcessObservation>();
        pending.Push(root);
        while (pending.TryPop(out var process))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excluded?.ContainsKey(process.Pid) == true || !tree.TryAdd(process.Pid, process))
            {
                continue;
            }
            foreach (var child in children[process.Pid])
            {
                // A child can outlive a parent PID that was reused during the scan.
                if (child.Start.Seconds > process.Start.Seconds ||
                    (child.Start.Seconds == process.Start.Seconds && child.Start.Microseconds >= process.Start.Microseconds))
                {
                    pending.Push(child);
                }
            }
        }
    }

    internal static RunnerProcessTreeSample SummarizeTree(Dictionary<int, ProcessObservation> tree,
        Dictionary<int, ProcessObservation> previous, bool hasWindow, double cpuBudget, double nsPerTick,
        bool incomplete, bool rootAvailable)
    {
        if (!rootAvailable)
        {
            return new RunnerProcessTreeSample { Reason = "process-tree-unavailable" };
        }

        long? resident = tree.Count == 0 ? 0 : null;
        double? delta = tree.Count == 0 && previous.Count == 0 && !incomplete ? 0 : null;
        var canMeasureCpu = hasWindow && cpuBudget > 0 && nsPerTick > 0;
        incomplete |= hasWindow && (!canMeasureCpu || previous.Count != tree.Count);
        foreach (var (pid, process) in tree)
        {
            if (process.ResidentBytes is long bytes && bytes <= long.MaxValue - resident.GetValueOrDefault())
            {
                resident = resident.GetValueOrDefault() + bytes;
            }
            else
            {
                incomplete = true;
            }

            if (canMeasureCpu)
            {
                if (previous.TryGetValue(pid, out var baseline) && baseline.Start == process.Start &&
                    process.CpuTicks is ulong ticks && baseline.CpuTicks is ulong priorTicks && ticks >= priorTicks)
                {
                    delta = delta.GetValueOrDefault() + (ticks - priorTicks);
                }
                else
                {
                    incomplete = true;
                }
            }
        }

        return new RunnerProcessTreeSample
        {
            ProcessCount = tree.Count,
            ResidentBytes = resident,
            CpuPercent = canMeasureCpu && delta.HasValue ? Math.Min(100, delta.Value * nsPerTick / cpuBudget * 100) : null,
            Reason = incomplete ? "process-tree-partial" : null,
        };
    }

    internal readonly record struct ProcessStart(ulong Seconds, ulong Microseconds);
    internal readonly record struct ProcessObservation(int Pid, int ParentPid, ProcessStart Start, long? ResidentBytes, ulong? CpuTicks);

    [StructLayout(LayoutKind.Sequential)]
    private struct TimebaseInfo
    {
        public uint Numerator;
        public uint Denominator;
    }

    [DllImport(SystemLibrary)]
    private static extern uint mach_host_self();

    // mach_task_self() is a header macro for the cached port. task_self_trap requires a
    // separate send right, so use the libSystem compatibility entry point.
    [DllImport(SystemLibrary)]
    private static extern uint mach_task_self();

    [DllImport(SystemLibrary)]
    private static extern int mach_port_deallocate(uint task, uint name);

    [DllImport(SystemLibrary)]
    private static extern int mach_timebase_info(out TimebaseInfo info);

    [DllImport(SystemLibrary)]
    private static extern int getpagesize();

    [DllImport(SystemLibrary)]
    private static extern int host_statistics(uint host, int flavor, [Out] uint[] info, ref uint count);

    [DllImport(SystemLibrary)]
    private static extern int host_statistics64(uint host, int flavor, [Out] uint[] info, ref uint count);

    [DllImport(SystemLibrary)]
    private static extern int sysctlbyname(string name, out int value, ref nuint length, nint newValue, nuint newLength);

    [DllImport(SystemLibrary)]
    private static extern int sysctlbyname(string name, out long value, ref nuint length, nint newValue, nuint newLength);

    [DllImport(ProcLibrary)]
    private static extern int proc_listpids(uint type, uint typeInfo, [Out] int[]? buffer, int size);

    [DllImport(ProcLibrary, SetLastError = true)]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, [Out] byte[] buffer, int size);
}
