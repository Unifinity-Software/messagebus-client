using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MessageBus.Cli.Runner.Service;

/// <summary>One process as mbus sees it when deciding whether it may end it (the original issue).</summary>
/// <param name="Pid">Its id.</param>
/// <param name="Name">Its program's name without extension (<c>cmd</c>, <c>conhost</c>, <c>mbus</c>).</param>
/// <param name="ParentPid">Its parent's id (0 when unknown).</param>
/// <param name="CommandLine">Its command line, when it can be read.</param>
/// <param name="ExecutablePath">Its program's file, when it can be read.</param>
/// <param name="StartTicks">When it started (UTC ticks; on Linux the kernel's start time), so a reused id is told apart.</param>
/// <param name="Owner">The user it runs as (the numeric user id on macOS and Linux), when it can be read.</param>
public sealed record ProcessRecord(int Pid, string Name, int ParentPid, string? CommandLine, string? ExecutablePath, long StartTicks, string? Owner = null)
{
    /// <summary>What <see cref="RunnerInstance"/> writes down to recognise this process later.</summary>
    public ProcessIdentity? Identity => ExecutablePath is { Length: > 0 } path && StartTicks != 0 ? new ProcessIdentity(Pid, StartTicks, path) : null;
}

/// <summary>A process as recorded in <see cref="RunnerInstance.PidFile"/>: ended only while all three still match.</summary>
/// <param name="Pid">Its id.</param>
/// <param name="StartTicks">When it started.</param>
/// <param name="ExecutablePath">Its program's file.</param>
public sealed record ProcessIdentity(int Pid, long StartTicks, string ExecutablePath);

/// <summary>The processes on this machine (tests give a fixed list, so nothing real is looked at or ended).</summary>
/// <param name="get">One process by id; null when it isn't running or can't be read.</param>
/// <param name="all">Every process that can be read.</param>
public sealed class ProcessTable(Func<int, ProcessRecord?> get, Func<IReadOnlyList<ProcessRecord>> all)
{
    /// <summary>No processes (a service manager in tests that doesn't say).</summary>
    public static readonly ProcessTable Empty = new(_ => null, () => []);

    /// <summary>One process by id; null when it isn't running or can't be read.</summary>
    public ProcessRecord? Get(int pid) => pid > 0 ? get(pid) : null;

    /// <summary>Every process that can be read.</summary>
    public IReadOnlyList<ProcessRecord> All() => all();

    /// <summary>A fixed list (tests).</summary>
    public static ProcessTable Of(IEnumerable<ProcessRecord> records)
    {
        var list = records.ToList();
        return new ProcessTable(pid => list.FirstOrDefault(r => r.Pid == pid), () => list);
    }

    /// <summary>This machine's.</summary>
    public static ProcessTable ForThisMachine() => new(NativeProcesses.Get, () =>
    {
        var records = new List<ProcessRecord>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (NativeProcesses.Get(process.Id) is { } record)
                {
                    records.Add(record);
                }
            }
        }
        return records;
    });
}

/// <summary>
/// One process held open while mbus decides whether to end it (the original issue): <see cref="Read"/> and <see cref="EndTree"/> act on
/// the same process (a Windows handle, a Linux pidfd), so one that exits and whose id goes to another in between is never
/// ended by mistake. Tests give delegates.
/// </summary>
/// <param name="read">The held process's record now; null when it can't be read.</param>
/// <param name="endTree">Ends it and everything it started; true when it is gone.</param>
/// <param name="resource">Released when done (the handle).</param>
public sealed class HeldProcess(Func<ProcessRecord?> read, Func<bool> endTree, IDisposable? resource = null) : IDisposable
{
    /// <summary>The held process's record now; null when it can't be read.</summary>
    public ProcessRecord? Read() => read();

    /// <summary>Ends it and everything it started; true when it is gone.</summary>
    public bool EndTree() => endTree();

    /// <inheritdoc />
    public void Dispose() => resource?.Dispose();
}

/// <summary>How <see cref="ServiceManager.EndProcessTree"/> ended.</summary>
public enum EndOutcome
{
    /// <summary>It was gone already.</summary>
    Gone,

    /// <summary>It was ended.</summary>
    Ended,

    /// <summary>mbus tried and couldn't end it (another user's, say).</summary>
    NotEnded,

    /// <summary>It isn't the process mbus wrote down any more, so it was left alone.</summary>
    Refused,
}

/// <summary>
/// What mbus may end when a runner doesn't stop (the original issue), decided over <see cref="ProcessTable"/> records only, so it is
/// tested without real processes. mbus ends only a process it can prove is its own runner or restart loop, never one of
/// the caller's own ancestors.
/// </summary>
public static class RunnerProcesses
{
    /// <summary>The <c>mbus</c> subcommands of <c>runner</c> that aren't a runner themselves.</summary>
    private static readonly HashSet<string> RunnerSubcommands = new(StringComparer.OrdinalIgnoreCase) { "install", "uninstall", "stop", "env" };

    /// <summary>Asking for help: never a runner.</summary>
    private static readonly HashSet<string> HelpTokens = new(StringComparer.OrdinalIgnoreCase) { "--help", "-h", "-?", "/?" };

    /// <summary>How deep a parent walk goes (a cycle or a very deep tree stops it).</summary>
    private const int MaxDepth = 64;

    /// <summary>How a recorded process compares with what runs under its id now.</summary>
    public enum Match
    {
        /// <summary>Nothing runs under that id.</summary>
        Gone,

        /// <summary>The same process: id, program and start time match.</summary>
        Same,

        /// <summary>Another program, or one started later, has that id now.</summary>
        Changed,

        /// <summary>It is the caller or one of its parents.</summary>
        Ancestor,
    }

    /// <summary><paramref name="pid"/>'s record, then its parent's, and so on, as far as they can be read.</summary>
    public static IReadOnlyList<ProcessRecord> Ancestry(int pid, ProcessTable table)
    {
        var chain = new List<ProcessRecord>();
        var seen = new HashSet<int>();
        var current = table.Get(pid);
        while (current is not null && chain.Count < MaxDepth && seen.Add(current.Pid))
        {
            chain.Add(current);
            if (current.ParentPid <= 0 || current.ParentPid == current.Pid)
            {
                break;
            }
            // A parent that started after its child is another process that got the id of one that exited.
            var parent = table.Get(current.ParentPid);
            if (parent is not null && parent.StartTicks != 0 && current.StartTicks != 0 && parent.StartTicks > current.StartTicks)
            {
                break;
            }
            current = parent;
        }
        return chain;
    }

    /// <summary>The ids of <paramref name="pid"/> and every parent of it that can be read: never ended.</summary>
    public static HashSet<int> Protected(int pid, ProcessTable table)
    {
        var ids = Ancestry(pid, table).Select(r => r.Pid).ToHashSet();
        ids.Add(pid);
        return ids;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is one of <paramref name="installs"/> (the <c>mbus</c> on PATH) or a version's file
    /// beside one (<c>&lt;dir&gt;/.mbus-versions/&lt;version&gt;/mbus</c>).
    /// </summary>
    public static bool IsMbusFile(string? path, IReadOnlyCollection<string> installs)
    {
        if (path is not { Length: > 0 })
        {
            return false;
        }
        var file = Normalize(path);
        foreach (var install in installs)
        {
            var stable = Normalize(install);
            if (string.Equals(file, stable, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var slash = stable.LastIndexOf('/');
            if (slash < 0)
            {
                continue;
            }
            var prefix = stable[..slash] + "/" + Update.VersionedInstall.FolderName + "/";
            var name = stable[(slash + 1)..];
            if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && file[prefix.Length..].Split('/') is [{ Length: > 0 }, var fileName]
                && string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether <paramref name="record"/> is an <c>mbus runner</c> (not <c>runner stop</c> and the like) from <paramref name="installs"/>.</summary>
    public static bool IsRunner(ProcessRecord record, IReadOnlyCollection<string> installs) =>
        IsMbusFile(record.ExecutablePath, installs) && record.CommandLine is { } line && RunnerArguments(line) is { } args && !NamesSubcommand(args);

    /// <summary>The <c>--log-file</c> an <c>mbus runner</c> command line passes (a runner a service manager starts has one).</summary>
    public static string? LogFile(string commandLine)
    {
        if (RunnerArguments(commandLine) is not { } args)
        {
            return null;
        }
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--log-file" && i + 1 < args.Count)
            {
                return args[i + 1];
            }
            if (args[i].StartsWith("--log-file=", StringComparison.Ordinal))
            {
                return args[i]["--log-file=".Length..];
            }
        }
        return null;
    }

    /// <summary>
    /// The arguments after <c>runner</c> in an <c>mbus</c> command line, past any options before it (<c>--server X</c>);
    /// null when it isn't <c>mbus runner</c>.
    /// </summary>
    private static List<string>? RunnerArguments(string line)
    {
        var args = Arguments(line);
        var i = 0;
        while (i < args.Count && args[i].StartsWith('-') && !HelpTokens.Contains(args[i]))
        {
            i += args[i].Contains('=') ? 1 : 2; // every option before a subcommand takes a value
        }
        return i < args.Count && args[i] == "runner" ? args[(i + 1)..] : null;
    }

    /// <summary>Whether <paramref name="args"/> (after <c>runner</c>) name a subcommand or ask for help, past the options and their values.</summary>
    private static bool NamesSubcommand(List<string> args)
    {
        for (var i = 0; i < args.Count;)
        {
            if (HelpTokens.Contains(args[i]))
            {
                return true;
            }
            if (!args[i].StartsWith('-'))
            {
                return RunnerSubcommands.Contains(args[i]);
            }
            // The runner's own options (--work-dir, --log-file, --slots) and --server all take a value.
            i += args[i].Contains('=') ? 1 : 2;
        }
        return false;
    }

    /// <summary>
    /// The Windows restart loop that started the runner <paramref name="self"/>: past its parents that are this mbus (the
    /// <c>mbus.exe</c> on PATH waiting for the version's file), the first <c>cmd</c>. It counts only when the runner is
    /// supervised (started with <c>--log-file</c>), the <c>cmd</c> runs mbus's own loop (<c>for /l</c> and this mbus's
    /// path) and its parent is <c>conhost</c>, as the logon task starts it. Never an ordinary <c>cmd</c> you typed in.
    /// </summary>
    public static ProcessRecord? RestartLoop(int self, ProcessTable table, IReadOnlyCollection<string> installs, bool supervised)
    {
        if (!supervised)
        {
            return null;
        }
        var chain = Ancestry(self, table);
        var i = 0;
        while (i < chain.Count && IsMbusFile(chain[i].ExecutablePath, installs))
        {
            i++;
        }
        if (i == 0 || i + 1 >= chain.Count)
        {
            return null;
        }
        return IsRestartLoop(chain[i], chain[i + 1], installs) ? chain[i] : null;
    }

    /// <summary>
    /// Whether <paramref name="loop"/> has the restart loop's shape: a <c>cmd</c> running mbus's own loop (<c>for /l</c>
    /// and this mbus's path) whose parent is <c>conhost</c>.
    /// </summary>
    public static bool IsRestartLoop(ProcessRecord loop, ProcessRecord? parent, IReadOnlyCollection<string> installs) =>
        Named(loop, "cmd") && loop.CommandLine is { } line && line.Contains("for /l", StringComparison.OrdinalIgnoreCase)
        && installs.Any(p => p.Length > 0 && line.Contains(p, StringComparison.OrdinalIgnoreCase))
        && parent is not null && parent.Pid == loop.ParentPid && Named(parent, "conhost");

    /// <summary>Whether what runs as <paramref name="recorded"/>'s id now is that same process, and not one mbus must leave alone.</summary>
    /// <param name="recorded">From <see cref="RunnerInstance.PidFile"/>.</param>
    /// <param name="table">The processes now.</param>
    /// <param name="callers">The caller and its parents (<see cref="Protected"/>).</param>
    public static Match Check(ProcessIdentity recorded, ProcessTable table, IReadOnlySet<int> callers)
    {
        if (callers.Contains(recorded.Pid))
        {
            return Match.Ancestor;
        }
        if (table.Get(recorded.Pid) is not { } now)
        {
            return Match.Gone;
        }
        return SameProcess(recorded, now) ? Match.Same : Match.Changed;
    }

    /// <summary>Whether <paramref name="now"/> is the process <paramref name="recorded"/> wrote down: same id, start time and file.</summary>
    public static bool SameProcess(ProcessIdentity recorded, ProcessRecord now) =>
        now.Pid == recorded.Pid && now.StartTicks == recorded.StartTicks && now.ExecutablePath is { } path
        && string.Equals(Normalize(path), Normalize(recorded.ExecutablePath), StringComparison.OrdinalIgnoreCase)
        && string.Equals(now.Name, Path.GetFileNameWithoutExtension(Normalize(recorded.ExecutablePath).Split('/')[^1]), StringComparison.OrdinalIgnoreCase);

    /// <summary>What <see cref="OtherRunners"/> found.</summary>
    /// <param name="Here">Runners that belong to this settings folder: they block a stop.</param>
    /// <param name="Untraced">Runners started without <c>--log-file</c>, so their folder can't be told: only named.</param>
    public sealed record OtherRunnerScan(IReadOnlyList<ProcessRecord> Here, IReadOnlyList<ProcessRecord> Untraced);

    /// <summary>
    /// Runners from <paramref name="installs"/> other than <paramref name="excluded"/>: one from before the original issue holds no lock and
    /// ignores the stop request, so a free lock doesn't prove it stopped. Only one that belongs to
    /// <paramref name="directory"/> counts: a service-started runner's <c>--log-file</c> is inside its settings folder, and
    /// it runs as the same user where that can be read. A runner with another settings folder (<c>MBUS_HOME</c>) or
    /// another user's is none of this folder's business.
    /// </summary>
    public static OtherRunnerScan OtherRunners(ProcessTable table, IReadOnlyCollection<string> installs, IReadOnlySet<int> excluded, string directory)
    {
        var owner = table.Get(Environment.ProcessId)?.Owner;
        List<ProcessRecord> here = [], untraced = [];
        foreach (var record in table.All().Where(r => !excluded.Contains(r.Pid) && IsRunner(r, installs)).OrderBy(r => r.Pid))
        {
            if (LogFile(record.CommandLine!) is not { } log)
            {
                untraced.Add(record);
            }
            else if (IsInside(log, directory) && (owner is null || record.Owner is null || record.Owner == owner))
            {
                here.Add(record);
            }
        }
        return new OtherRunnerScan(here, untraced);
    }

    /// <summary>Whether <paramref name="path"/> is inside <paramref name="directory"/>.</summary>
    internal static bool IsInside(string path, string directory)
    {
        try
        {
            var folder = Normalize(Path.GetFullPath(directory)).TrimEnd('/') + "/";
            return Normalize(Path.GetFullPath(path, directory)).StartsWith(folder, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The arguments after the program in <paramref name="line"/>: split at spaces outside quotes, quotes removed.</summary>
    private static List<string> Arguments(string line)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                    any = false;
                }
            }
            else
            {
                token.Append(c);
                any = true;
            }
        }
        if (any)
        {
            tokens.Add(token.ToString());
        }
        return tokens.Count > 0 ? tokens[1..] : tokens;
    }

    private static bool Named(ProcessRecord record, string name) => string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/');
}

/// <summary>Reads one process's record from the operating system.</summary>
internal static class NativeProcesses
{
    public static ProcessRecord? Get(int pid)
    {
        try
        {
            return OperatingSystem.IsWindows() ? Windows(pid) : OperatingSystem.IsMacOS() ? MacOs(pid) : OperatingSystem.IsLinux() ? Linux(pid) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or FormatException
            or OverflowException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Holds process <paramref name="pid"/> open so what is read is what is ended: on Windows its handle, on Linux a pidfd
    /// where the kernel has them. macOS has neither, so there the record is read again just before the end. Null when it
    /// isn't running.
    /// </summary>
    public static HeldProcess? Open(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            return new HeldProcess(() => null, () => false);
        }
        if (OperatingSystem.IsLinux() && LinuxPidFd.Open(pid) is { } pidfd)
        {
            return pidfd;
        }
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
        if (!OperatingSystem.IsWindows())
        {
            return new HeldProcess(() => Get(pid), () => EndTree(process), process);
        }
        try
        {
            var handle = process.SafeHandle; // opened once and kept: Kill below uses this same handle
            return new HeldProcess(() => FromWindowsHandle(handle, pid), () => EndTree(process), process);
        }
        catch (InvalidOperationException)
        {
            process.Dispose();
            return null; // exited meanwhile
        }
        catch (System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            return new HeldProcess(() => null, () => false); // not ours to end
        }
    }

    private static bool EndTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException)
        {
            return true; // exited meanwhile (or it is this process's parent, refused by .NET)
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        // Part of the tree couldn't be ended: Kill throws an AggregateException. It reaches the stop, which names the
        // processes, checks the lock again and carries on (the original issue).
    }

    /// <summary>A Linux process held by a pidfd (Linux 5.3 and later): a signal sent through it reaches only that process.</summary>
    private static class LinuxPidFd
    {
        private const long PidFdOpen = 434, PidFdSendSignal = 424; // the same number on every architecture
        private const int SigKill = 9;

        public static HeldProcess? Open(int pid)
        {
            var fd = (int)syscall(PidFdOpen, pid, 0, 0, 0);
            if (fd < 0)
            {
                return null; // no pidfds here (or it is gone): the caller falls back
            }
            var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true);
            // Read after the pidfd is open: had the id gone to another process before, its start time differs.
            return new HeldProcess(() => Get(pid), () => EndTree(pid, handle), handle);
        }

        private static bool EndTree(int pid, Microsoft.Win32.SafeHandles.SafeFileHandle root)
        {
            var start = Get(pid)?.StartTicks ?? 0;
            var descendants = Descendants(pid, start);
            if (syscall(PidFdSendSignal, (int)root.DangerousGetHandle(), SigKill, 0, 0) != 0)
            {
                return false;
            }
            foreach (var child in descendants)
            {
                // Each through its own pidfd, and only while it is the one that was found.
                var fd = (int)syscall(PidFdOpen, child.Pid, 0, 0, 0);
                if (fd < 0)
                {
                    continue;
                }
                using var held = new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true);
                if (Get(child.Pid)?.StartTicks == child.StartTicks)
                {
                    syscall(PidFdSendSignal, fd, SigKill, 0, 0);
                }
            }
            var poll = new PollFd { Fd = (int)root.DangerousGetHandle(), Events = 1 /* POLLIN: it exited */ };
            return Poll(ref poll, 1, 5000) > 0;
        }

        private static List<ProcessRecord> Descendants(int pid, long start)
        {
            var all = ProcessTable.ForThisMachine().All();
            var found = new List<ProcessRecord>();
            var parents = new Queue<(int Pid, long Start)>([(pid, start)]);
            while (parents.Count > 0 && found.Count < 4096)
            {
                var (parent, started) = parents.Dequeue();
                foreach (var child in all.Where(r => r.ParentPid == parent && r.Pid != parent && r.StartTicks >= started && r.Pid != Environment.ProcessId))
                {
                    found.Add(child);
                    parents.Enqueue((child.Pid, child.StartTicks));
                }
            }
            return found;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern long syscall(long number, long a, long b, long c, long d);

        [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
        private static extern int Poll(ref PollFd fds, ulong count, int timeout);
    }

    // Windows

    private const uint QueryLimitedInformation = 0x1000;

    private static ProcessRecord? Windows(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            return WindowsRecord(handle, pid);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>The record of the process <paramref name="handle"/> is open on (so it can't be another one that got its id).</summary>
    public static ProcessRecord? FromWindowsHandle(Microsoft.Win32.SafeHandles.SafeProcessHandle handle, int pid)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            return WindowsRecord(handle.DangerousGetHandle(), pid);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    private const int ErrorInsufficientBuffer = 122;

    private static ProcessRecord WindowsRecord(IntPtr handle, int pid)
    {
        string? exe = null;
        for (var capacity = 1024; capacity <= 32768; capacity *= 2)
        {
            var size = capacity;
            var path = new StringBuilder(capacity);
            if (QueryFullProcessImageName(handle, 0, path, ref size))
            {
                exe = path.ToString(0, size);
                break;
            }
            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            {
                break;
            }
        }
        long start = GetProcessTimes(handle, out var created, out _, out _, out _) ? DateTime.FromFileTimeUtc(created).Ticks : 0;
        var info = default(ProcessBasicInformation);
        var parent = NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0 ? (int)info.InheritedFromUniqueProcessId : 0;
        return new ProcessRecord(pid, exe is null ? "" : Path.GetFileNameWithoutExtension(exe), parent, WindowsCommandLine(handle), exe, start);
    }

    private static string? WindowsCommandLine(IntPtr handle)
    {
        // ProcessCommandLineInformation (60): a UNICODE_STRING followed by its text.
        NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out var needed);
        if (needed <= 0 || needed > 1 << 20)
        {
            return null;
        }
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (NtQueryInformationProcess(handle, 60, buffer, needed, out _) != 0)
            {
                return null;
            }
            var length = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref ProcessBasicInformation information, int length, out int returned);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returned);

    // macOS

    private const int ProcPidTbsdInfo = 3;
    private const int BsdInfoSize = 136;

    private static ProcessRecord? MacOs(int pid)
    {
        var info = new byte[BsdInfoSize];
        if (proc_pidinfo(pid, ProcPidTbsdInfo, 0, info, info.Length) != BsdInfoSize)
        {
            return null;
        }
        var parent = BitConverter.ToInt32(info, 16);
        var uid = BitConverter.ToUInt32(info, 20);
        var seconds = (long)BitConverter.ToUInt64(info, 120);
        var micros = (long)BitConverter.ToUInt64(info, 128);
        var start = DateTime.UnixEpoch.Ticks + seconds * TimeSpan.TicksPerSecond + micros * 10;
        var path = new byte[4096];
        var length = proc_pidpath(pid, path, (uint)path.Length);
        string? exe = length > 0 ? Encoding.UTF8.GetString(path, 0, length) : null;
        var name = exe is null ? CString(info, 64, 32) : Path.GetFileNameWithoutExtension(exe);
        return new ProcessRecord(pid, name, parent, MacCommandLine(pid), exe, start, uid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string? MacCommandLine(int pid)
    {
        int[] mib = [1 /* CTL_KERN */, 49 /* KERN_PROCARGS2 */, pid];
        var size = (IntPtr)0;
        if (sysctl(mib, 3, null, ref size, IntPtr.Zero, IntPtr.Zero) != 0 || size <= 0)
        {
            return null;
        }
        var buffer = new byte[(int)size];
        if (sysctl(mib, 3, buffer, ref size, IntPtr.Zero, IntPtr.Zero) != 0)
        {
            return null;
        }
        // argc, the executable's path, padding, then argv.
        var argc = BitConverter.ToInt32(buffer, 0);
        var i = 4;
        while (i < (int)size && buffer[i] != 0) i++;
        while (i < (int)size && buffer[i] == 0) i++;
        var args = new List<string>();
        while (args.Count < argc && i < (int)size)
        {
            var end = Array.IndexOf(buffer, (byte)0, i);
            if (end < 0) end = (int)size;
            args.Add(Encoding.UTF8.GetString(buffer, i, end - i));
            i = end + 1;
        }
        return string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
    }

    private static string CString(byte[] bytes, int offset, int length)
    {
        var end = Array.IndexOf(bytes, (byte)0, offset, length);
        return Encoding.UTF8.GetString(bytes, offset, (end < 0 ? offset + length : end) - offset);
    }

    [DllImport("libproc.dylib")]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[] buffer, int size);

    [DllImport("libproc.dylib")]
    private static extern int proc_pidpath(int pid, byte[] buffer, uint size);

    [DllImport("libc")]
    private static extern int sysctl(int[] name, uint length, byte[]? oldValue, ref IntPtr oldLength, IntPtr newValue, IntPtr newLength);

    // Linux

    private static ProcessRecord? Linux(int pid)
    {
        var folder = $"/proc/{pid}";
        if (!Directory.Exists(folder))
        {
            return null;
        }
        // stat: "pid (comm) state ppid ... starttime(22nd)"; comm may hold spaces and parentheses.
        var stat = File.ReadAllText(folder + "/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        var parent = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
        var start = long.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture);
        string? exe = null;
        try
        {
            exe = new FileInfo(folder + "/exe").LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        var line = string.Join(' ', File.ReadAllText(folder + "/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        var name = exe is null ? File.ReadAllText(folder + "/comm").Trim() : Path.GetFileNameWithoutExtension(exe);
        // status: "Uid:\treal\teffective\tsaved\tfs".
        var owner = File.ReadLines(folder + "/status").FirstOrDefault(l => l.StartsWith("Uid:", StringComparison.Ordinal))?
            .Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ElementAtOrDefault(1);
        return new ProcessRecord(pid, name, parent, line, exe, start + 1, owner);
    }
}
