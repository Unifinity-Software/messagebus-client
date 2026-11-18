using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>
/// Ends every process a run started on macOS and Linux (the original issue), the way the job object does on Windows. Each run's
/// processes carry the run's own marker variable (<see cref="MarkerVariable"/>), which every program they start
/// inherits, also one that leaves its process group or session, or whose parent has exited (a daemon, a
/// <c>nohup</c> server, a watcher left running). When the run is over the runner finds this user's processes whose
/// environment holds the marker, and their children, and ends them. No admin rights; nothing changes on the machine.
/// </summary>
/// <remarks>
/// A program can escape only by starting another with its environment cleared on purpose (<c>env -i</c>) and
/// leaving it behind once its parent has gone. Linux runs under bubblewrap can't even do that: their PID namespace
/// ends with the run.
/// </remarks>
internal static class PosixRunProcesses
{
    /// <summary>The variable that marks a run's processes; its value is unique to the run.</summary>
    public const string MarkerVariable = "MBUS_RUN_MARKER";

    private const int SigKill = 9;

    /// <summary>A new marker value.</summary>
    public static string NewMarker() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Ends every process of this user whose environment has <c><see cref="MarkerVariable"/>=<paramref name="marker"/></c>,
    /// and everything they started. Repeats until a pass finds none (a process may start another while the
    /// first pass runs), at most a few times. Never throws; does nothing where processes can't be listed.
    /// </summary>
    /// <returns>How many processes were ended.</returns>
    public static int EndMarked(string marker, IProcessTable? table = null)
    {
        table ??= Current;
        if (table is null)
        {
            return 0;
        }
        var entry = Encoding.UTF8.GetBytes($"{MarkerVariable}={marker}");
        var self = Environment.ProcessId;
        var ended = 0;
        try
        {
            for (var pass = 0; pass < 5; pass++)
            {
                var processes = table.List();
                var marked = processes.Where(p => p.Pid != self && table.HasEnvironmentEntry(p.Pid, entry)).Select(p => p.Pid).ToHashSet();
                foreach (var pid in Descendants(processes, marked).Where(pid => pid != self))
                {
                    marked.Add(pid);
                }
                if (marked.Count == 0)
                {
                    break;
                }
                foreach (var pid in marked)
                {
                    if (table.Kill(pid))
                    {
                        ended++;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return ended;
    }

    /// <summary>Every process below <paramref name="roots"/>.</summary>
    internal static IEnumerable<int> Descendants(IReadOnlyList<ProcessEntry> processes, IReadOnlySet<int> roots)
    {
        var children = processes.ToLookup(p => p.ParentPid, p => p.Pid);
        var seen = new HashSet<int>(roots);
        var queue = new Queue<int>(roots);
        while (queue.Count > 0)
        {
            foreach (var child in children[queue.Dequeue()])
            {
                if (seen.Add(child))
                {
                    queue.Enqueue(child);
                    yield return child;
                }
            }
        }
    }

    /// <summary>This OS's process table; null on Windows (which uses a job object) and where it can't be read.</summary>
    internal static IProcessTable? Current =>
        OperatingSystem.IsLinux() ? new LinuxProcessTable()
        : OperatingSystem.IsMacOS() ? new MacProcessTable()
        : null;

    /// <summary>A process and its parent.</summary>
    internal readonly record struct ProcessEntry(int Pid, int ParentPid);

    /// <summary>Lists processes, reads their environment and ends them.</summary>
    internal interface IProcessTable
    {
        IReadOnlyList<ProcessEntry> List();

        /// <summary>Whether the process's environment has exactly this <c>NAME=value</c> entry; false when it can't be read.</summary>
        bool HasEnvironmentEntry(int pid, byte[] entry);

        /// <summary>SIGKILL; false when the process was already gone or isn't this user's.</summary>
        bool Kill(int pid);
    }

    /// <summary>Whether the NUL-separated <paramref name="block"/> holds <paramref name="entry"/> as one whole entry.</summary>
    internal static bool ContainsEntry(ReadOnlySpan<byte> block, ReadOnlySpan<byte> entry)
    {
        while (block.Length > 0)
        {
            var end = block.IndexOf((byte)0);
            var item = end < 0 ? block : block[..end];
            if (item.SequenceEqual(entry))
            {
                return true;
            }
            if (end < 0)
            {
                break;
            }
            block = block[(end + 1)..];
        }
        return false;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private static bool Signal(int pid) => kill(pid, SigKill) == 0;

    /// <summary>Linux: <c>/proc/&lt;pid&gt;/stat</c> for the parent, <c>/proc/&lt;pid&gt;/environ</c> (this user's processes only).</summary>
    private sealed class LinuxProcessTable : IProcessTable
    {
        public IReadOnlyList<ProcessEntry> List()
        {
            var list = new List<ProcessEntry>();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                {
                    continue;
                }
                try
                {
                    // pid (comm) state ppid …; comm may hold spaces and parentheses, so read after the last ')'.
                    var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length > 1 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ppid))
                    {
                        list.Add(new ProcessEntry(pid, ppid));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone, or not ours.
                }
            }
            return list;
        }

        public bool HasEnvironmentEntry(int pid, byte[] entry)
        {
            try
            {
                return ContainsEntry(File.ReadAllBytes($"/proc/{pid}/environ"), entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public bool Kill(int pid) => Signal(pid);
    }

    /// <summary>
    /// macOS: <c>proc_listallpids</c> and <c>proc_pidinfo</c> (libproc) for processes and parents, and the
    /// <c>KERN_PROCARGS2</c> sysctl for a process's arguments and environment (this user's processes only).
    /// </summary>
    [SupportedOSPlatform("macos")]
    private sealed class MacProcessTable : IProcessTable
    {
        private const int CtlKern = 1;
        private const int KernProcArgs2 = 49;
        private const int ProcPidTbsdInfo = 3;
        private const int BsdInfoSize = 136;
        private const int BsdInfoParentOffset = 16;
        private const int ArgsBufferSize = 1 << 20;

        public IReadOnlyList<ProcessEntry> List()
        {
            var count = proc_listallpids(null, 0);
            if (count <= 0)
            {
                return [];
            }
            var pids = new int[count + 64];
            count = proc_listallpids(pids, pids.Length * sizeof(int));
            var list = new List<ProcessEntry>(Math.Max(count, 0));
            var info = new byte[BsdInfoSize];
            for (var i = 0; i < count; i++)
            {
                if (pids[i] > 0 && proc_pidinfo(pids[i], ProcPidTbsdInfo, 0, info, info.Length) == info.Length)
                {
                    list.Add(new ProcessEntry(pids[i], BitConverter.ToInt32(info, BsdInfoParentOffset)));
                }
            }
            return list;
        }

        public bool HasEnvironmentEntry(int pid, byte[] entry)
        {
            var buffer = new byte[ArgsBufferSize];
            var size = (nuint)buffer.Length;
            if (sysctl([CtlKern, KernProcArgs2, pid], 3, buffer, ref size, IntPtr.Zero, 0) != 0 || size < sizeof(int))
            {
                return false;
            }
            // argc, the executable path, padding NULs, argc arguments, then the environment, NUL-separated.
            var data = buffer.AsSpan(0, (int)size);
            var argc = BitConverter.ToInt32(buffer, 0);
            var rest = data[sizeof(int)..];
            var pathEnd = rest.IndexOf((byte)0);
            if (pathEnd < 0)
            {
                return false;
            }
            rest = rest[pathEnd..];
            var start = rest.IndexOfAnyExcept((byte)0);
            if (start < 0)
            {
                return false;
            }
            rest = rest[start..];
            for (var i = 0; i < argc && rest.Length > 0; i++)
            {
                var end = rest.IndexOf((byte)0);
                rest = end < 0 ? [] : rest[(end + 1)..];
            }
            return ContainsEntry(rest, entry);
        }

        public bool Kill(int pid) => Signal(pid);

        [DllImport("/usr/lib/libproc.dylib")]
        private static extern int proc_listallpids([Out] int[]? buffer, int buffersize);

        [DllImport("/usr/lib/libproc.dylib")]
        private static extern int proc_pidinfo(int pid, int flavor, ulong arg, [Out] byte[] buffer, int buffersize);

        [DllImport("libc")]
        private static extern int sysctl([In] int[] name, uint namelen, [Out] byte[] oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);
    }
}
