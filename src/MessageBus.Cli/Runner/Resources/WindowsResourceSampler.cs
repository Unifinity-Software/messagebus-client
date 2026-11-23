using System.Runtime.InteropServices;
using MessageBus.Contracts.Runner;
using Microsoft.Win32.SafeHandles;

namespace MessageBus.Cli.Runner.Resources;

/// <summary>Windows counters, collected only when the caller requests an observation.</summary>
internal sealed class WindowsResourceSampler : ISystemResourceSampler
{
    // Bounds both the native walk and retained process baselines on unusually large hosts.
    private const int MaximumProcesses = 8192;
    private readonly Func<ResourceSamplingContext, CancellationToken, Snapshot> _read;
    private Snapshot? _previous;
    private TimeProvider? _clock;
    private long _timestamp;
    private Dictionary<int, long> _rootIdentities = [];

    internal WindowsResourceSampler() => _read = new NativeReader().Read;

    // Inject whole observations so tests exercise real grouping and delta arithmetic without OS state.
    internal WindowsResourceSampler(Func<ResourceSamplingContext, CancellationToken, Snapshot> read) => _read = read;

    public ValueTask<ResourceSamplingResult> SampleAsync(ResourceSamplingContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() && _read.Target is NativeReader)
            return ValueTask.FromResult(new ResourceSamplingResult(context.TimeProvider.GetUtcNow(), null,
                ResourceSampleStatus.Unsupported, "windows_required"));

        var current = _read(context, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var timestamp = context.TimeProvider.GetTimestamp();
        var elapsed = ReferenceEquals(_clock, context.TimeProvider) && _previous is not null
            ? context.TimeProvider.GetElapsedTime(_timestamp, timestamp) : TimeSpan.Zero;
        var previous = elapsed > TimeSpan.Zero ? _previous : null;
        var reasons = new List<string>();
        if (current.Reason is not null) reasons.Add(current.Reason);

        double? capacity = current.HostLogicalCpus > 0 ? current.HostLogicalCpus : null;
        ResourceLimitSource? source = capacity is not null ? ResourceLimitSource.Host : null;
        var jobMayLimit = current.Job.InJob != false && !current.Job.SetsNoLimits;
        if (jobMayLimit)
        {
            // QueryInformationJobObject(NULL) sees ONLY the immediate job. Ancestor caps can be
            // stricter, and CPU rates are relative to the parent. Never publish that upper bound
            // as an effective capacity. Preserve host measurements and explain what is unknown.
            capacity = null;
            source = current.Job.InJob == true ? ResourceLimitSource.JobObject : null;
            reasons.Add(current.Job.QuerySucceeded ? "windows_job_hierarchy_unavailable" : "windows_job_query_failed");
        }
        if (current.HostLogicalCpus <= 0) reasons.Add("windows_cpu_capacity_unavailable");
        double? utilization = null;
        if (current.HostLogicalCpus > 64)
            reasons.Add("windows_processor_groups_unsupported");
        else if (current.System is null)
            reasons.Add("windows_cpu_unavailable");
        else if (capacity is not null && previous?.System is { } before)
        {
            var after = current.System;
            if (after.Kernel >= before.Kernel && after.User >= before.User && after.Idle >= before.Idle)
            {
                var total = (double)(after.Kernel - before.Kernel) + (after.User - before.User);
                var idle = (double)(after.Idle - before.Idle);
                if (total > 0 && idle <= total)
                    utilization = Math.Clamp((total - idle) / total * 100, 0, 100);
                else reasons.Add("windows_cpu_counter_invalid");
            }
            else reasons.Add("windows_cpu_counter_reset");
        }

        var hostTotal = current.Memory?.Total;
        long? totalBytes = hostTotal;
        long? available = current.Memory?.Available;
        if (current.Memory is null) reasons.Add("windows_memory_unavailable");
        if (jobMayLimit)
        {
            totalBytes = null;
            available = null;
        }

        var children = Children(current.Processes);
        var identities = new Dictionary<int, long>();
        var reusedRoots = new HashSet<int>();
        foreach (var root in context.OwnedRunRootProcessIds.Take(MaximumProcesses).Prepend(context.RunnerProcessId))
        {
            if (_rootIdentities.TryGetValue(root, out var identity))
            {
                identities[root] = identity;
                if (current.Processes.TryGetValue(root, out var reading) && reading.Created is { } currentIdentity &&
                    currentIdentity != identity)
                    reusedRoots.Add(root);
            }
            else if (current.Processes.TryGetValue(root, out var reading) && reading.Created is { } created)
                identities[root] = created;
        }
        var tasks = SelectTree(current.Processes, children, context.OwnedRunRootProcessIds, cancellationToken,
            reusedRoots: reusedRoots);
        var runner = SelectTree(current.Processes, children, [context.RunnerProcessId], cancellationToken,
            reusedRoots: reusedRoots);
        if (current.Reason is not null)
        {
            runner.Reason ??= current.Reason;
            tasks.Reason ??= current.Reason;
        }
        runner.Ids.ExceptWith(tasks.Ids);
        // A missing task root makes exclusion uncertain, even if the runner itself was readable.
        if (tasks.Reason is not null) runner.Reason ??= "windows_task_exclusion_incomplete";
        var runnerSample = Aggregate(runner, current, previous, elapsed, capacity, cancellationToken);
        var taskSample = Aggregate(tasks, current, previous, elapsed, capacity, cancellationToken);
        if (runnerSample.Reason is not null || taskSample.Reason is not null)
            reasons.Add("windows_process_data_partial");

        _previous = current;
        _clock = context.TimeProvider;
        _timestamp = timestamp;
        _rootIdentities = identities;
        return ValueTask.FromResult(new ResourceSamplingResult(context.TimeProvider.GetUtcNow(),
            previous is null ? null : (long)elapsed.TotalMilliseconds,
            reasons.Count == 0 ? ResourceSampleStatus.Ok : ResourceSampleStatus.Partial,
            reasons.Count == 0 ? null : string.Join(';', reasons.Distinct()),
            new RunnerCpuSample { HostLogicalCpus = current.HostLogicalCpus > 0 ? current.HostLogicalCpus : null,
                EffectiveCpus = capacity, UtilizationPercent = utilization, LimitSource = source },
            new RunnerMemorySample { HostTotalBytes = hostTotal, TotalBytes = totalBytes,
                AvailableBytes = available, LimitSource = !jobMayLimit
                    ? (hostTotal is null ? null : ResourceLimitSource.Host) : source },
            runnerSample, taskSample));
    }

    private static RunnerProcessTreeSample Aggregate(Tree tree, Snapshot current, Snapshot? previous,
        TimeSpan elapsed, double? capacity, CancellationToken cancellationToken)
    {
        if (tree.Ids.Count == 0 && tree.Reason is not null)
            return new RunnerProcessTreeSample { Reason = tree.Reason };
        long resident = 0;
        double cpuTicks = 0;
        var memoryComplete = true;
        var cpuComplete = previous is not null && capacity > 0;
        var reason = tree.Reason;
        foreach (var pid in tree.Ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var process = current.Processes[pid];
            if (process.ResidentBytes is { } bytes && bytes >= 0 && bytes <= long.MaxValue - resident)
                resident += bytes;
            else { memoryComplete = false; reason ??= "windows_process_memory_unavailable"; }
            if (previous is not null && process.CpuTicks is { } ticks && process.Created is { } created &&
                previous.Processes.TryGetValue(pid, out var old) && old.Created == created &&
                old.CpuTicks is { } oldTicks && ticks >= oldTicks)
                cpuTicks += ticks - oldTicks;
            else
            {
                cpuComplete = false;
                if (previous is not null) reason ??= "windows_process_cpu_baseline_incomplete";
            }
        }
        return new RunnerProcessTreeSample
        {
            // No roots means a measured empty union; missing/inaccessible roots never become zero.
            CpuPercent = cpuComplete && tree.Reason is null
                ? Math.Clamp(cpuTicks / elapsed.Ticks / capacity!.Value * 100, 0, 100) : null,
            ResidentBytes = memoryComplete && tree.Reason is null ? resident : null,
            ProcessCount = tree.Ids.Count,
            Reason = reason,
        };
    }

    private static Dictionary<int, List<int>> Children(IReadOnlyDictionary<int, ProcessReading> processes)
    {
        var result = new Dictionary<int, List<int>>();
        foreach (var (pid, process) in processes)
        {
            if (!result.TryGetValue(process.ParentId, out var list)) result[process.ParentId] = list = [];
            list.Add(pid);
        }
        return result;
    }

    private static Tree SelectTree(IReadOnlyDictionary<int, ProcessReading> processes,
        Dictionary<int, List<int>> children, IReadOnlyList<int> roots, CancellationToken cancellationToken,
        bool requireIdentity = true, HashSet<int>? reusedRoots = null)
    {
        var tree = new Tree();
        var visited = new HashSet<int>();
        var pending = new Queue<(int Pid, long? ParentCreated)>();
        foreach (var root in roots.Take(MaximumProcesses)) pending.Enqueue((root, null));
        if (roots.Count > MaximumProcesses) tree.Reason = "windows_process_limit";
        while (pending.TryDequeue(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(item.Pid)) continue;
            if (reusedRoots?.Contains(item.Pid) == true)
            { tree.Reason ??= "windows_process_root_reused"; continue; }
            if (!processes.TryGetValue(item.Pid, out var process))
            { tree.Reason ??= "windows_process_root_unavailable"; continue; }
            if (requireIdentity && process.Created is null)
            { tree.Reason ??= "windows_process_identity_unavailable"; continue; }
            // A parent PID can be reused after its original process exits. Its new incarnation
            // cannot own an older child, even though Toolhelp retains that child's original PPID.
            if (item.ParentCreated is { } parentCreated && process.Created < parentCreated) continue;
            tree.Ids.Add(item.Pid);
            if (children.TryGetValue(item.Pid, out var descendants))
                foreach (var child in descendants) pending.Enqueue((child, process.Created));
        }
        return tree;
    }

    private sealed class Tree
    {
        internal HashSet<int> Ids { get; } = [];
        internal string? Reason { get; set; }
    }

    internal sealed record SystemTimes(ulong Idle, ulong Kernel, ulong User);
    internal sealed record MemoryReading(long Total, long Available);
    internal sealed record ProcessReading(int ParentId, long? Created = null, ulong? CpuTicks = null, long? ResidentBytes = null);
    internal sealed record JobReading(bool? InJob, bool QuerySucceeded = true,
        uint? CpuFlags = null, uint? CpuRate = null, ulong? MemoryLimit = null, ulong? Affinity = null)
    {
        // Nearly every Windows process is in a job (Task Scheduler, terminals and IDEs create them) that caps nothing.
        // Host CPU time and free physical memory are whole-machine readings, so a job that sets no CPU rate, job memory
        // limit or affinity leaves the host capacity in effect. A job above this one could still cap CPU or memory
        // without this process being able to see it; that only happens when a tool nests a capped job on purpose.
        internal bool SetsNoLimits => InJob == true && QuerySucceeded
            && ((CpuFlags ?? 0) & CpuRateControlEnable) == 0 && MemoryLimit is null && Affinity is null;

        private const uint CpuRateControlEnable = 0x1;
    }
    internal sealed record Snapshot(int HostLogicalCpus, SystemTimes? System, MemoryReading? Memory,
        JobReading Job, IReadOnlyDictionary<int, ProcessReading> Processes, string? Reason = null);

    private sealed class NativeReader
    {
        private int? _hostCpus;

        internal Snapshot Read(ResourceSamplingContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _hostCpus ??= checked((int)GetActiveProcessorCount(0xffff));
            SystemTimes? times = GetSystemTimes(out var idle, out var kernel, out var user)
                ? new(idle.Value, kernel.Value, user.Value) : null;
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            MemoryReading? ram = GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical <= long.MaxValue
                ? new((long)memory.TotalPhysical, (long)Math.Min(memory.AvailablePhysical, memory.TotalPhysical)) : null;
            var job = ReadJob();
            var processes = new Dictionary<int, ProcessReading>();
            string? reason = null;
            // A process created after the snapshot begins may have reused a captured PID. Do
            // not combine its new handle/identity with the old snapshot's parent relationship.
            GetSystemTimeAsFileTime(out var snapshotStarted);
            using (var snapshot = CreateToolhelp32Snapshot(2, 0))
            {
                if (snapshot.IsInvalid) reason = "windows_process_snapshot_unavailable";
                else
                {
                    // Reuse one native entry rather than marshal an unused executable-name string
                    // per process. Only PID and PPID leave the snapshot; no names/paths are retained.
                    var entrySize = Marshal.SizeOf<ProcessEntry>();
                    var entry = Marshal.AllocHGlobal(entrySize);
                    try
                    {
                        Marshal.WriteInt32(entry, entrySize);
                        var parentOffset = Marshal.OffsetOf<ProcessEntry>(nameof(ProcessEntry.ParentProcessId)).ToInt32();
                        var found = Process32FirstW(snapshot, entry);
                        var visited = 0;
                        while (found && visited++ < MaximumProcesses)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var pid = Marshal.ReadInt32(entry, 8);
                            if (pid > 0) processes[pid] = new(Marshal.ReadInt32(entry, parentOffset));
                            found = Process32NextW(snapshot, entry);
                        }
                        if (found) reason = "windows_process_limit";
                        else if (Marshal.GetLastPInvokeError() != 18) reason = "windows_process_enumeration_incomplete";
                    }
                    finally { Marshal.FreeHGlobal(entry); }
                }
            }
            var children = Children(processes);
            var candidates = SelectTree(processes, children, context.OwnedRunRootProcessIds, cancellationToken, false);
            candidates.Ids.UnionWith(SelectTree(processes, children, [context.RunnerProcessId], cancellationToken, false).Ids);
            foreach (var pid in candidates.Ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var handle = OpenProcess(0x1000, false, (uint)pid); // QUERY_LIMITED_INFORMATION
                if (handle.IsInvalid) continue; // Exit and access denial are normal during a heartbeat.
                if (!GetProcessTimes(handle, out var created, out _, out kernel, out user)) continue;
                if (created.Value > snapshotStarted.Value) continue;
                var counters = new ProcessMemory { Size = (uint)Marshal.SizeOf<ProcessMemory>() };
                long? resident = K32GetProcessMemoryInfo(handle, ref counters, counters.Size) && counters.WorkingSet <= long.MaxValue
                    ? (long)counters.WorkingSet : null;
                // Read creation and counters through the same handle, so PID reuse during this walk
                // cannot pair one process's identity with another process's CPU or resident bytes.
                processes[pid] = new(processes[pid].ParentId, checked((long)created.Value),
                    kernel.Value + user.Value, resident);
            }
            return new(_hostCpus.Value, times, ram, job, processes, reason);
        }

        private static JobReading ReadJob()
        {
            if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob)) return new(null, false);
            if (!inJob) return new(false);
            var extendedOk = QueryExtendedJob(IntPtr.Zero, 9, out var extended,
                (uint)Marshal.SizeOf<ExtendedJobLimits>(), IntPtr.Zero);
            var cpuOk = QueryCpuJob(IntPtr.Zero, 15, out var cpu,
                (uint)Marshal.SizeOf<CpuRateControl>(), IntPtr.Zero);
            return new(true, extendedOk && cpuOk, cpuOk ? cpu.Flags : null, cpuOk ? cpu.Rate : null,
                extendedOk && (extended.Basic.Flags & 0x200) != 0 ? (ulong)extended.JobMemoryLimit : null,
                extendedOk && (extended.Basic.Flags & 0x10) != 0 ? (ulong)extended.Basic.Affinity : null);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low, High;
        internal readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemory
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool,
            NonPagedPool, PagefileUsage, PeakPagefileUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicJobLimits
    {
        public long ProcessTimeLimit, JobTimeLimit;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedJobLimits
    {
        public BasicJobLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateControl { public uint Flags, Rate; }

    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("kernel32.dll")] private static extern void GetSystemTimeAsFileTime(out FileTime time);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, IntPtr entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, IntPtr entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited,
        out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(SafeProcessHandle process, ref ProcessMemory counters, uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryExtendedJob(IntPtr job, int infoClass, out ExtendedJobLimits info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryCpuJob(IntPtr job, int infoClass, out CpuRateControl info, uint length, IntPtr returned);
}
