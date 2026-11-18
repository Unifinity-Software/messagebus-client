using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>
/// A Windows job object holding every process of one run. Processes an agent starts join its job
/// automatically, even ones whose parent has exited (which a process-tree kill can't find), so
/// ending the job at the end of the run ends everything the run left behind. Closing the handle does
/// the same, so a crashed runner leaves nothing running either. No admin rights needed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsJob : IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private const uint DieOnUnhandledException = 0x400;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;

    private readonly SafeFileHandle _job;

    private WindowsJob(SafeFileHandle job)
    {
        _job = job;
    }

    /// <summary>A new job that ends its processes when closed.</summary>
    /// <exception cref="Win32Exception">Windows refused.</exception>
    public static WindowsJob Create()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        var limits = new JobObjectExtendedLimitInformation
        {
            // No crash dialog waiting for someone to click it: a crashing process just exits.
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobClose | DieOnUnhandledException },
        };
        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }
        return new WindowsJob(job);
    }

    /// <summary>Puts a running process (and whatever it starts from now on) in the job.</summary>
    /// <exception cref="Win32Exception">The process is gone, or Windows refused.</exception>
    public void Add(int processId)
    {
        using var process = OpenProcess(ProcessSetQuota | ProcessTerminate, false, processId);
        if (process.IsInvalid || !AssignProcessToJobObject(_job, process))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Ends every process in the job.</summary>
    public void Terminate() => TerminateJobObject(_job, 1);

    /// <inheritdoc />
    public void Dispose() => _job.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
