using System.Globalization;
using System.Runtime.InteropServices;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner.Resources;

/// <summary>Reads kernel counters directly; calls are serialized by the runner.</summary>
internal sealed class LinuxResourceSampler : ISystemResourceSampler
{
    private const int MaxFileChars = 131072;
    private const int MaxProcesses = 8192;
    private const int MaxAncestors = 128;
    private readonly Func<string, string?> _read;
    private readonly Func<IEnumerable<int>> _processIds;
    private readonly long _ticksPerSecond;
    private readonly long _pageSize;
    private readonly bool _supported;
    private readonly char[] _buffer = new char[MaxFileChars + 1];
    private Baseline? _previous;
    private Dictionary<int, long> _rootIdentities = [];
    private bool _readFailure;
    private bool _lastReadFailure;
    private CancellationToken _cancellation;

    public LinuxResourceSampler()
    {
        _supported = OperatingSystem.IsLinux();
        _read = ReadFile;
        _processIds = EnumerateProcessIds;
        // Linux sysconf constants, identical on glibc and musl. Never called on other OSes.
        _ticksPerSecond = _supported ? Sysconf(2) : 0; // _SC_CLK_TCK
        _pageSize = _supported ? Sysconf(30) : 0; // _SC_PAGESIZE
    }

    // Fixtures exercise the complete sampler on any OS, without invoking native APIs.
    internal LinuxResourceSampler(Func<string, string?> read, Func<IEnumerable<int>> processIds,
        long ticksPerSecond = 100, long pageSize = 4096)
    {
        _read = read;
        _processIds = processIds;
        _ticksPerSecond = ticksPerSecond;
        _pageSize = pageSize;
        _supported = true;
    }

    public ValueTask<ResourceSamplingResult> SampleAsync(ResourceSamplingContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_supported)
            return ValueTask.FromResult(new ResourceSamplingResult(context.TimeProvider.GetUtcNow(), null,
                ResourceSampleStatus.Unsupported, "platform-unsupported"));
        _cancellation = cancellationToken;
        _readFailure = false;
        var host = ReadHostCpu(Read("/proc/stat"));
        var meminfo = Read("/proc/meminfo");
        var hostTotal = Kilobytes(meminfo, "MemTotal:");
        var hostAvailable = Kilobytes(meminfo, "MemAvailable:");
        var limits = ReadLimits(host.LogicalCpus, hostTotal, hostAvailable);
        var processes = ReadProcesses(out var processPartial);
        var stamp = context.TimeProvider.GetTimestamp();
        var previous = _previous;
        var seconds = previous is not null && ReferenceEquals(previous.Clock, context.TimeProvider)
            ? context.TimeProvider.GetElapsedTime(previous.Timestamp, stamp).TotalSeconds : 0;
        if (seconds <= 0 || !double.IsFinite(seconds)) seconds = 0;
        var window = seconds > 0 ? (long?)(seconds >= long.MaxValue / 1000d ? long.MaxValue : (long)(seconds * 1000)) : null;

        double? utilization = null;
        if (seconds > 0 && previous is not null && limits.Cpus is > 0 && limits.Cpus == previous.Cpus)
        {
            if (limits.CpuSource != ResourceLimitSource.Host)
            {
                if (limits.UsageSeconds is { } usage && previous.UsageSeconds is { } before &&
                    limits.UsageKey == previous.UsageKey && usage >= before)
                    utilization = Percent((usage - before) / seconds / limits.Cpus.Value);
            }
            else if (host.Total is { } total && previous.Host.Total is { } oldTotal && total > oldTotal &&
                host.Idle is { } idle && previous.Host.Idle is { } oldIdle && idle >= oldIdle && idle - oldIdle <= total - oldTotal &&
                host.Counters is { } counters && previous.Host.Counters is { } oldCounters &&
                counters.Length == oldCounters.Length && host.LogicalCpus == previous.Host.LogicalCpus &&
                counters.Zip(oldCounters).All(pair => pair.First >= pair.Second))
                utilization = Percent(1 - (double)(idle - oldIdle) / (total - oldTotal));
        }

        var nextIdentities = new Dictionary<int, long>();
        var reusedRoots = new HashSet<int>();
        foreach (var root in context.OwnedRunRootProcessIds.Take(MaxProcesses).Prepend(context.RunnerProcessId))
        {
            if (_rootIdentities.TryGetValue(root, out var identity))
            {
                nextIdentities[root] = identity;
                if (processes.TryGetValue(root, out var observed) && observed.Start != identity)
                    reusedRoots.Add(root);
            }
            else if (processes.TryGetValue(root, out var observed)) nextIdentities[root] = observed.Start;
        }
        var taskSet = Descendants(processes, context.OwnedRunRootProcessIds, reusedRoots);
        var runnerSet = Descendants(processes, [context.RunnerProcessId], reusedRoots);
        runnerSet.ExceptWith(taskSet);
        var tasks = Aggregate(taskSet, context.OwnedRunRootProcessIds, processes, previous, seconds, limits.Cpus, processPartial);
        var runner = Aggregate(runnerSet, [context.RunnerProcessId], processes, previous, seconds, limits.Cpus, processPartial);
        if (context.OwnedRunRootProcessIds.Any(reusedRoots.Contains))
        {
            tasks = new RunnerProcessTreeSample { Reason = "process-root-reused" };
            runner = new RunnerProcessTreeSample { Reason = "process-task-exclusion-incomplete" };
        }
        else if (reusedRoots.Contains(context.RunnerProcessId))
            runner = new RunnerProcessTreeSample { Reason = "process-root-reused" };
        var partial = _readFailure || processPartial || host.Total is null || host.LogicalCpus is null ||
            hostTotal is null || hostAvailable is null || limits.Unknown || runner.Reason is not null || tasks.Reason is not null ||
            (limits.CpuSource != ResourceLimitSource.Host && limits.UsageSeconds is null);
        cancellationToken.ThrowIfCancellationRequested();
        _previous = new Baseline(context.TimeProvider, stamp, host, limits.Cpus, limits.UsageSeconds, limits.UsageKey, processes);
        _rootIdentities = nextIdentities;
        return ValueTask.FromResult(new ResourceSamplingResult(context.TimeProvider.GetUtcNow(), window,
            partial ? ResourceSampleStatus.Partial : ResourceSampleStatus.Ok, partial ? "read-partial" : null,
            new RunnerCpuSample { HostLogicalCpus = host.LogicalCpus, EffectiveCpus = limits.Cpus,
                LimitSource = limits.CpuSource, UtilizationPercent = utilization },
            new RunnerMemorySample { HostTotalBytes = hostTotal, TotalBytes = limits.Memory,
                AvailableBytes = limits.Available, LimitSource = limits.MemorySource }, runner, tasks));
    }

    private Limits ReadLimits(int? hostCpus, long? hostMemory, long? hostAvailable)
    {
        var result = new Limits { Cpus = hostCpus, Memory = hostMemory, Available = hostAvailable,
            CpuSource = hostCpus is null ? null : ResourceLimitSource.Host,
            MemorySource = hostMemory is null ? null : ResourceLimitSource.Host };
        var memberships = Read("/proc/self/cgroup");
        var mounts = Read("/proc/self/mountinfo");
        if (memberships is null || mounts is null) result.CpuUnknown = result.MemoryUnknown = true;
        else
        {
            var groups = ResolveGroups(memberships, mounts);
            foreach (var group in groups)
            {
                var source = group.V2 ? ResourceLimitSource.CgroupV2 : ResourceLimitSource.CgroupV1;
                var cpu = group.V2 || group.Controllers.Contains("cpu");
                var cpuset = group.V2 || group.Controllers.Contains("cpuset");
                var memory = group.V2 || group.Controllers.Contains("memory");
                foreach (var directory in Ancestors(group.Path, group.Mount, result))
                {
                    _cancellation.ThrowIfCancellationRequested();
                    if (cpu)
                    {
                        double? quota = null;
                        if (group.V2)
                        {
                            var text = ReadLimit(directory + "/cpu.max", result, cpu: true);
                            if (text is not null)
                            {
                                var fields = Fields(text);
                                if (fields.Length == 2 && fields[0] == "max" && Number(fields[1]) is > 0) { }
                                else if (fields.Length == 2 && Number(fields[0]) is > 0 and var q && Number(fields[1]) is > 0 and var p)
                                    quota = (double)q / p;
                                else result.CpuUnknown = true;
                            }
                        }
                        else
                        {
                            var q = ReadLimit(directory + "/cpu.cfs_quota_us", result, cpu: true);
                            var p = ReadLimit(directory + "/cpu.cfs_period_us", result, cpu: true);
                            if (q is not null && q.Trim() != "-1")
                            {
                                if (Number(q) is > 0 and var qn && Number(p) is > 0 and var pn) quota = (double)qn / pn;
                                else result.CpuUnknown = true;
                            }
                        }
                        if (quota is > 0 && (result.Cpus is null || quota < result.Cpus))
                        { result.Cpus = quota; result.CpuSource = source; }
                    }
                    if (cpuset)
                    {
                        var set = ReadLimit(directory + (group.V2 ? "/cpuset.cpus.effective" : "/cpuset.effective_cpus"), result, cpu: true)
                            ?? ReadLimit(directory + "/cpuset.cpus", result, cpu: true);
                        if (!string.IsNullOrWhiteSpace(set))
                        {
                            var count = CpuSetCount(set);
                            if (count is null) result.CpuUnknown = true;
                            else if (result.Cpus is null || count < result.Cpus)
                            { result.Cpus = count; result.CpuSource = source; }
                        }
                    }
                    if (memory)
                    {
                        var text = ReadLimit(directory + (group.V2 ? "/memory.max" : "/memory.limit_in_bytes"), result, cpu: false);
                        if (text is not null && text.Trim() != "max")
                        {
                            var limit = Number(text);
                            if (limit is null) result.MemoryUnknown = true;
                            // v1's unlimited sentinel is near LONG_MAX (rounded down to page size).
                            else if (group.V2 || limit < long.MaxValue - 1048576)
                            {
                                if (result.Memory is null || limit < result.Memory)
                                { result.Memory = limit; result.MemorySource = source; }
                                var usage = Number(Read(directory + (group.V2 ? "/memory.current" : "/memory.usage_in_bytes")));
                                if (usage is null) result.AvailableUnknown = true;
                                else if (result.Available is not null)
                                    result.Available = Math.Min(result.Available.Value, Math.Max(0, limit.Value - usage.Value));
                            }
                        }
                    }
                }
                // Usage is scoped to the current group; parent constraints still bound its capacity.
                if (group.V2)
                { result.UsageSeconds = KeyNumber(Read(group.Path + "/cpu.stat"), "usage_usec") / 1_000_000d; result.UsageKey = "v2:" + group.Path; }
                else if (group.Controllers.Contains("cpuacct"))
                { result.UsageSeconds = Number(Read(group.Path + "/cpuacct.usage")) / 1_000_000_000d; result.UsageKey = "v1:" + group.Path; }
            }
            // Any resource controller membership must have a visible matching mount.
            foreach (var line in Lines(memberships))
            {
                var fields = line.Split(':', 3);
                if (fields.Length != 3) { result.CpuUnknown = result.MemoryUnknown = true; continue; }
                foreach (var controller in fields[1].Length == 0 ? new[] { "" } : fields[1].Split(','))
                    if ((controller is "" or "cpu" or "cpuacct" or "cpuset" or "memory") &&
                        !groups.Any(g => controller == "" ? g.V2 : !g.V2 && g.Controllers.Contains(controller)))
                    {
                        if (controller is "" or "cpu" or "cpuacct" or "cpuset") result.CpuUnknown = true;
                        if (controller is "" or "memory") result.MemoryUnknown = true;
                    }
            }
        }
        if (result.CpuUnknown)
        {
            result.Cpus = null; result.CpuSource = null;
        }
        if (result.MemoryUnknown)
        {
            result.Memory = null; result.Available = null; result.MemorySource = null;
        }
        if (result.AvailableUnknown) result.Available = null;
        if (hostCpus is null) { result.Cpus = null; result.CpuSource = null; }
        if (hostMemory is null) { result.Memory = null; result.Available = null; result.MemorySource = null; }
        if (result.Available is { } available && result.Memory is { } total) result.Available = Math.Clamp(available, 0, total);
        return result;
    }

    private static List<Group> ResolveGroups(string memberships, string mountinfo)
    {
        var result = new List<Group>();
        foreach (var membership in Lines(memberships))
        {
            var parts = membership.Split(':', 3);
            if (parts.Length != 3 || !SafeAbsolute(parts[2])) continue;
            var controllers = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
            var v2 = parts[0] == "0" && controllers.Length == 0;
            Group? best = null;
            var bestRootLength = -1;
            foreach (var line in Lines(mountinfo))
            {
                var separator = line.IndexOf(" - ", StringComparison.Ordinal);
                if (separator < 0) continue;
                var left = Fields(line[..separator]);
                var right = Fields(line[(separator + 3)..]);
                if (left.Length < 6 || right.Length < 3 || right[0] != (v2 ? "cgroup2" : "cgroup")) continue;
                if (!v2 && !controllers.Any(c => right[2].Split(',').Contains(c))) continue;
                var root = UnescapeMount(left[3]);
                var mount = UnescapeMount(left[4]).TrimEnd('/');
                if (mount.Length == 0) mount = "/";
                if (!SafeAbsolute(root) || !SafeAbsolute(mount) || !Within(parts[2], root)) continue;
                if (root.Length <= bestRootLength) continue;
                var suffix = root == "/" ? parts[2] : parts[2][root.Length..];
                var path = mount.TrimEnd('/') + "/" + suffix.TrimStart('/');
                best = new Group(v2, controllers, path.TrimEnd('/') is "" ? "/" : path.TrimEnd('/'), mount);
                bestRootLength = root.Length;
            }
            if (best is not null) result.Add(best);
        }
        return result;
    }

    private IEnumerable<string> Ancestors(string path, string mount, Limits limits)
    {
        for (var depth = 0; depth < MaxAncestors; depth++)
        {
            yield return path;
            if (path == mount) yield break;
            var slash = path.LastIndexOf('/');
            path = slash <= 0 ? "/" : path[..slash];
            if (!Within(path, mount)) { limits.CpuUnknown = limits.MemoryUnknown = true; yield break; }
        }
        limits.CpuUnknown = limits.MemoryUnknown = true;
    }

    private Dictionary<int, ProcessCounters> ReadProcesses(out bool partial)
    {
        var result = new Dictionary<int, ProcessCounters>();
        var priorFailure = _readFailure;
        _readFailure = false;
        partial = false;
        try
        {
            var count = 0;
            foreach (var pid in _processIds())
            {
                _cancellation.ThrowIfCancellationRequested();
                if (++count > MaxProcesses) { partial = true; break; }
                if (pid <= 0) continue;
                var stat = Read("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
                if (ParseProcess(stat, pid) is { } process) result[pid] = process;
                // Exits during enumeration are ordinary; unreadable roots are detected below.
                else if (stat is not null) partial = true;
            }
        }
        catch (Exception e) when (ExpectedReadError(e)) { partial = true; }
        partial |= _readFailure;
        _readFailure |= priorFailure;
        return result;
    }

    private RunnerProcessTreeSample Aggregate(HashSet<int> ids, IReadOnlyList<int> roots,
        Dictionary<int, ProcessCounters> processes, Baseline? previous, double seconds, double? cpus, bool enumerationPartial)
    {
        if (roots.Count == 0)
            return new RunnerProcessTreeSample { CpuPercent = seconds > 0 ? 0 : null, ResidentBytes = 0, ProcessCount = 0 };
        var missing = roots.Any(root => !processes.ContainsKey(root));
        if (ids.Count == 0 && missing) return new RunnerProcessTreeSample { Reason = "process-unavailable" };
        long resident = 0;
        double ticks = 0;
        var memoryKnown = _pageSize > 0;
        var cpuKnown = seconds > 0 && cpus is > 0 && _ticksPerSecond > 0;
        var partial = missing || enumerationPartial || _ticksPerSecond <= 0 || _pageSize <= 0;
        var counterUnknown = false;
        foreach (var pid in ids)
        {
            var process = processes[pid];
            var prefix = "/proc/" + pid.ToString(CultureInfo.InvariantCulture);
            var statm = Read(prefix + "/statm");
            var fields = statm is null ? [] : Fields(statm);
            var pages = fields.Length >= 2 ? Number(fields[1]) : null;
            // statm and stat must describe the same process, even if a PID was reused mid-read.
            var identity = ParseProcess(Read(prefix + "/stat"), pid);
            if (identity?.Start != process.Start) cpuKnown = false;
            if (identity?.Start != process.Start || pages is null || _pageSize <= 0 ||
                pages > (long.MaxValue - resident) / _pageSize)
            { memoryKnown = false; partial = true; }
            else resident += pages.Value * _pageSize;
            if (previous is null || !previous.Processes.TryGetValue(pid, out var before) ||
                before.Start != process.Start || process.Ticks < before.Ticks)
            { cpuKnown = false; counterUnknown = previous is not null && seconds > 0; }
            else ticks += process.Ticks - before.Ticks;
        }
        if (missing || enumerationPartial) { memoryKnown = false; cpuKnown = false; }
        return new RunnerProcessTreeSample
        {
            CpuPercent = cpuKnown ? Percent(ticks / _ticksPerSecond / seconds / cpus!.Value) : null,
            ResidentBytes = memoryKnown ? resident : null,
            ProcessCount = ids.Count == 0 && enumerationPartial ? null : ids.Count,
            Reason = partial ? "process-partial" : counterUnknown ? "process-warmup" : null
        };
    }

    private static HashSet<int> Descendants(Dictionary<int, ProcessCounters> processes, IReadOnlyList<int> roots,
        HashSet<int> reusedRoots)
    {
        // Build one adjacency map instead of repeatedly walking every process's parent chain.
        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, process) in processes)
        {
            if (!children.TryGetValue(process.Parent, out var list)) children[process.Parent] = list = [];
            list.Add(pid);
        }
        var result = new HashSet<int>();
        var pending = new Stack<int>(roots.Take(MaxProcesses));
        while (pending.TryPop(out var pid))
        {
            if (reusedRoots.Contains(pid) || !processes.TryGetValue(pid, out var process) || !result.Add(pid)) continue;
            if (children.TryGetValue(pid, out var list))
                foreach (var child in list)
                    // The new process that reused a parent PID cannot own its older children.
                    if (processes[child].Start >= process.Start) pending.Push(child);
        }
        return result;
    }

    private static ProcessCounters? ParseProcess(string? text, int pid)
    {
        if (text is null) return null;
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')'); // comm may itself contain spaces and parentheses.
        if (open <= 0 || close <= open || !int.TryParse(text.AsSpan(0, open).Trim(), out var actual) || actual != pid) return null;
        var rest = text.AsSpan(close + 1).Trim();
        Span<Range> fields = stackalloc Range[64];
        var count = rest.Split(fields, ' ', StringSplitOptions.RemoveEmptyEntries);
        if (count < 20 || !int.TryParse(rest[fields[1]], out var parent) ||
            !long.TryParse(rest[fields[11]], out var user) || !long.TryParse(rest[fields[12]], out var system) ||
            !long.TryParse(rest[fields[19]], out var start) || user < 0 || system < 0 || start < 0 || user > long.MaxValue - system) return null;
        return new ProcessCounters(parent, start, user + system);
    }

    private static HostCpu ReadHostCpu(string? text)
    {
        long? total = null, idle = null;
        long[]? counters = null;
        var cpus = 0;
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                var values = Fields(line);
                // guest and guest_nice are already included in user and nice.
                if (values.Length < 5) continue;
                long sum = 0, idleSum = 0;
                var parsed = new long[Math.Min(8, values.Length - 1)];
                var valid = true;
                for (var i = 1; i <= Math.Min(8, values.Length - 1); i++)
                {
                    var value = Number(values[i]);
                    if (value is null || sum > long.MaxValue - value) { valid = false; break; }
                    sum += value.Value;
                    parsed[i - 1] = value.Value;
                    if (i is 4 or 5) idleSum += value.Value;
                }
                if (valid) { total = sum; idle = idleSum; counters = parsed; }
            }
            else if (line.Length > 3 && line.StartsWith("cpu", StringComparison.Ordinal) && char.IsAsciiDigit(line[3])) cpus++;
        }
        return new HostCpu(total, idle, cpus > 0 ? cpus : null, counters);
    }

    private static int? CpuSetCount(string text)
    {
        // Kernel cpulists are sorted, non-overlapping ranges. Reject malformed/overlapping input.
        var count = 0;
        var previous = -1;
        foreach (var part in text.Trim().Split(','))
        {
            var bounds = part.Split('-');
            if (bounds.Length is < 1 or > 2 || !int.TryParse(bounds[0], out var first)) return null;
            var last = first;
            if (bounds.Length == 2 && !int.TryParse(bounds[1], out last)) return null;
            if (first <= previous || last < first || last > 1048575) return null;
            count += last - first + 1;
            previous = last;
        }
        return count > 0 ? count : null;
    }

    private string? Read(string path)
    {
        _cancellation.ThrowIfCancellationRequested();
        _lastReadFailure = false;
        try
        {
            var text = _read(path);
            if (text?.Length > MaxFileChars) { _readFailure = _lastReadFailure = true; return null; }
            return text;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception e) when (ExpectedReadError(e)) { _readFailure = _lastReadFailure = true; return null; }
    }

    private string? ReadLimit(string path, Limits limits, bool cpu)
    {
        var text = Read(path);
        if (_lastReadFailure)
        {
            if (cpu) limits.CpuUnknown = true;
            else limits.MemoryUnknown = true;
        }
        return text;
    }

    private string? ReadFile(string path)
    {
        using var reader = new StreamReader(path);
        var length = 0;
        while (length < _buffer.Length)
        {
            _cancellation.ThrowIfCancellationRequested();
            var read = reader.Read(_buffer, length, _buffer.Length - length);
            if (read == 0) return new string(_buffer, 0, length);
            length += read;
        }
        _readFailure = _lastReadFailure = true;
        return null;
    }

    private static IEnumerable<int> EnumerateProcessIds()
    {
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) yield return pid;
    }

    private static IEnumerable<string> Lines(string? text)
    {
        if (text is null) yield break;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) if (line.Length > 0) yield return line;
    }

    private static string[] Fields(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static long? Number(string? text) => long.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
    private static long? KeyNumber(string? text, string key)
    {
        foreach (var line in Lines(text))
        {
            var fields = Fields(line);
            if (fields.Length >= 2 && fields[0] == key) return Number(fields[1]);
        }
        return null;
    }
    private static long? Kilobytes(string? text, string key) => KeyNumber(text, key) is { } n && n <= long.MaxValue / 1024 ? n * 1024 : null;
    private static double Percent(double fraction) => Math.Clamp(fraction * 100, 0, 100);
    private static bool ExpectedReadError(Exception e) => e is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private static bool Within(string path, string root) => root == "/" || path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
    private static bool SafeAbsolute(string path) => path.StartsWith('/') && !path.Split('/').Any(p => p is "." or "..");
    private static string UnescapeMount(string path) => path.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

    [DllImport("libc", EntryPoint = "sysconf")]
    private static extern nint Sysconf(int name);

    private sealed record ProcessCounters(int Parent, long Start, long Ticks);
    private sealed record HostCpu(long? Total, long? Idle, int? LogicalCpus, long[]? Counters);
    private sealed record Group(bool V2, string[] Controllers, string Path, string Mount);
    private sealed record Baseline(TimeProvider Clock, long Timestamp, HostCpu Host, double? Cpus,
        double? UsageSeconds, string? UsageKey, Dictionary<int, ProcessCounters> Processes);
    private sealed class Limits
    {
        public double? Cpus;
        public ResourceLimitSource? CpuSource;
        public long? Memory;
        public long? Available;
        public ResourceLimitSource? MemorySource;
        public double? UsageSeconds;
        public string? UsageKey;
        public bool CpuUnknown;
        public bool MemoryUnknown;
        public bool AvailableUnknown;
        public bool Unknown => CpuUnknown || MemoryUnknown || AvailableUnknown;
    }
}
