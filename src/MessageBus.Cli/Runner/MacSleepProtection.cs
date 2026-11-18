using System.Runtime.InteropServices;

namespace MessageBus.Cli.Runner;

/// <summary>macOS idle system sleep policy; display sleep and manual sleep remain available.</summary>
public enum RunnerKeepAwake { Always, Tasks, Off }

/// <summary>A process-owned IOKit assertion: macOS releases it even if the runner crashes.</summary>
internal sealed class MacSleepProtection : IDisposable
{
    private readonly RunnerKeepAwake _mode;
    private readonly Func<IDisposable> _acquire;
    private readonly TextWriter _log;
    private IDisposable? _assertion;
    private bool _disposed;

    internal MacSleepProtection(RunnerKeepAwake mode, Func<IDisposable> acquire, TextWriter log)
    {
        _mode = mode;
        _acquire = acquire;
        _log = log;
        SetWorking(false);
    }

    public static MacSleepProtection? Start(RunnerKeepAwake mode, TextWriter log)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        log.WriteLine(mode switch
        {
            RunnerKeepAwake.Always => "Mac sleep: preventing idle system sleep while the runner is running (--keep-awake always).",
            RunnerKeepAwake.Tasks => "Mac sleep: preventing idle system sleep only while tasks run; queued work may wait while this Mac sleeps (--keep-awake tasks).",
            _ => "Mac sleep: idle system sleep is allowed (--keep-awake off).",
        });
        return new(mode, NativeAssertion.Create, log);
    }

    public void SetWorking(bool working)
    {
        lock (this)
        {
            if (_disposed) return;
            if (_mode == RunnerKeepAwake.Always || (_mode == RunnerKeepAwake.Tasks && working))
            {
                if (_assertion is null)
                {
                    try { _assertion = _acquire(); }
                    catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
                    {
                        _log.WriteLine($"Couldn't prevent Mac idle sleep: {ex.Message}. In System Settings > Energy, prevent automatic sleeping when the display is off.");
                    }
                }
            }
            else
            {
                _assertion?.Dispose();
                _assertion = null;
            }
        }
    }

    public void Dispose()
    {
        lock (this)
        {
            _disposed = true;
            _assertion?.Dispose();
            _assertion = null;
        }
    }

    private sealed class NativeAssertion(uint id) : IDisposable
    {
        private uint _id = id;
        public static IDisposable Create()
        {
            var type = CFStringCreateWithCString(0, "PreventUserIdleSystemSleep", 0x08000100);
            var name = CFStringCreateWithCString(0, "messagebus.ai runner: waiting for or running tasks", 0x08000100);
            try
            {
                if (type == 0 || name == 0) throw new InvalidOperationException("Couldn't create the power assertion name");
                var result = IOPMAssertionCreateWithName(type, 255, name, out var assertion);
                if (result != 0) throw new InvalidOperationException($"IOKit error {result}");
                return new NativeAssertion(assertion);
            }
            finally
            {
                if (type != 0) CFRelease(type);
                if (name != 0) CFRelease(name);
            }
        }
        public void Dispose()
        {
            if (_id == 0) return;
            IOPMAssertionRelease(_id);
            _id = 0;
        }
        [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
        private static extern int IOPMAssertionCreateWithName(nint type, uint level, nint name, out uint id);
        [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
        private static extern int IOPMAssertionRelease(uint id);
        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern void CFRelease(nint value);
    }
}
