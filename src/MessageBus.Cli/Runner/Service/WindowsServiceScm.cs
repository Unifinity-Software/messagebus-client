using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;

namespace MessageBus.Cli.Runner.Service;

internal sealed record WindowsServicePlan(string Account)
{
    internal const string Name = "mbus-runner";
    internal const string VirtualAccount = @"NT SERVICE\mbus-runner";
    internal const string RequiredPrivilege = "SeChangeNotifyPrivilege";
    internal static void Configure(Action<uint, uint> setting, Action<string> privileges, Action<uint[]> recovery, Action<uint> startup)
    {
        setting(5, 1); // SERVICE_CONFIG_SERVICE_SID_INFO / SERVICE_SID_TYPE_UNRESTRICTED
        privileges(RequiredPrivilege);
        recovery([15_000, 60_000, 0]); // The final action is SC_ACTION_NONE.
        setting(4, 1); // Recovery also applies to a stopped service with a nonzero exit code.
        startup(2); // SERVICE_AUTO_START
        setting(3, 1); // SERVICE_CONFIG_DELAYED_AUTO_START_INFO
    }

    internal static WindowsServicePlan Create(string? account)
    {
        if (account is null) return new(VirtualAccount);
        if (!System.Text.RegularExpressions.Regex.IsMatch(account, @"\A[A-Za-z0-9][A-Za-z0-9.-]{0,254}\\[A-Za-z0-9][A-Za-z0-9_-]{0,14}\$\z")
            || account.StartsWith("BUILTIN\\", StringComparison.OrdinalIgnoreCase))
            throw new CliException(@"Use --account DOMAIN\name$ for a provisioned group Managed Service Account.");
        return new(account);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceScm : IDisposable
{
    private const uint QueryConfig = 0x1, ChangeConfig = 0x2, QueryStatus = 0x4, Start = 0x10, Stop = 0x20, Delete = 0x10000;
    private const uint ServiceOwnProcess = 0x10, DemandStart = 3, Disabled = 4;
    private const uint ErrorNormal = 1, NoChange = uint.MaxValue;
    private readonly ScHandle _manager;

    internal WindowsServiceScm(bool canCreate = false)
    {
        _manager = OpenSCManagerW(null, null, canCreate ? 3u : 1u);
        DemandHandle(_manager, "Open Windows Service Control Manager");
    }

    internal ScHandle? Find() => Find(QueryConfig | QueryStatus);

    private ScHandle? Find(uint access)
    {
        var service = OpenServiceW(_manager, WindowsServicePlan.Name, access);
        if (!service.IsInvalid) return service;
        var error = Marshal.GetLastPInvokeError();
        service.Dispose();
        return error == 1060 ? null : throw Error("Open the Windows runner service", error);
    }

    internal ScHandle OpenOwned(WindowsServiceRegistration registration, bool manage)
    {
        var service = Find(QueryConfig | QueryStatus | (manage ? ChangeConfig | Start | Stop | Delete : 0))
            ?? throw new CliException("The Windows runner service is not installed.");
        try
        {
            var config = Configuration(service);
            if (config.Type != ServiceOwnProcess || config.Image != registration.CommandLine
                || !string.Equals(config.Account, registration.Account, StringComparison.OrdinalIgnoreCase))
                throw new CliException("The registered mbus-runner service does not match this installation.");
            return service;
        }
        catch
        {
            service.Dispose();
            throw;
        }
    }

    internal ScHandle Register(WindowsServiceRegistration registration)
    {
        // Register as demand-start until the SID, privilege and recovery settings are in place.
        var service = CreateServiceW(_manager, WindowsServicePlan.Name, "messagebus.ai runner", QueryConfig | ChangeConfig | QueryStatus | Start | Stop | Delete,
            ServiceOwnProcess, DemandStart, ErrorNormal, registration.CommandLine, null, IntPtr.Zero, null, registration.Account, null);
        DemandHandle(service, "Register the Windows runner service");
        try
        {
            WindowsServicePlan.Configure((level, value) => SetValue(service, level, value), value => SetPrivileges(service, value),
                delays => SetRecovery(service, delays), start => SetStartup(service, start));
            return service;
        }
        catch
        {
            _ = DeleteService(service);
            service.Dispose();
            throw;
        }
    }

    internal static void Disable(ScHandle service) => SetStartup(service, Disabled);

    internal static void AllowMaintenanceStart(ScHandle service)
    {
        if (Configuration(service).Startup == Disabled) SetStartup(service, DemandStart);
    }

    private static void SetStartup(ScHandle service, uint start) =>
        Check(ChangeServiceConfigW(service, NoChange, start, NoChange, null, null, IntPtr.Zero, null, null, null, null),
            "Set Windows runner service startup");

    private static void SetPrivileges(ScHandle service, string privilege)
    {
        var privileges = Marshal.StringToHGlobalUni(privilege + "\0"); // MULTI_SZ
        try { Check(ChangeServiceConfig2W(service, 6, ref privileges), "Limit the Windows runner service privileges"); }
        finally { Marshal.FreeHGlobal(privileges); }
    }

    private static void SetRecovery(ScHandle service, uint[] delays)
    {
        var size = Marshal.SizeOf<RecoveryAction>();
        var actions = Marshal.AllocHGlobal(size * delays.Length);
        try
        {
            for (var i = 0; i < delays.Length; i++)
                Marshal.StructureToPtr(new RecoveryAction { Type = delays[i] == 0 ? 0u : 1u, Delay = delays[i] }, actions + i * size, false);
            var settings = new FailureActions { ResetPeriod = 86400, Count = (uint)delays.Length, Actions = actions };
            Check(ChangeServiceConfig2W(service, 2, ref settings), "Set Windows runner service recovery");
        }
        finally { Marshal.FreeHGlobal(actions); }
    }

    private static void SetValue(ScHandle service, uint level, uint value) =>
        Check(ChangeServiceConfig2W(service, level, ref value), "Configure the Windows runner service");

    private static (string Image, string Account, uint Type, uint Startup) Configuration(ScHandle service)
    {
        _ = QueryServiceConfigW(service, IntPtr.Zero, 0, out var size);
        if (size == 0) throw Error("Read Windows runner service configuration");
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            Check(QueryServiceConfigW(service, buffer, size, out _), "Read Windows runner service configuration");
            var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
            return (Marshal.PtrToStringUni(config.Image)!, Marshal.PtrToStringUni(config.Account)!, config.Type, config.Startup);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static ScmStatus Status(ScHandle service)
    {
        Check(QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ScmStatus>(), out _), "Read Windows runner service status");
        return status;
    }

    internal static async Task StartAsync(ScHandle service, TimeProvider time, CancellationToken ct)
    {
        if (Status(service).State == ServiceControllerStatus.Stopped
            && !StartServiceW(service, 0, IntPtr.Zero) && Marshal.GetLastPInvokeError() != 1056)
            throw Error("Start the Windows runner service");
        await WaitAsync(service, ServiceControllerStatus.Running, time, ct);
    }

    internal static async Task StopAsync(ScHandle service, TimeProvider time, CancellationToken ct)
    {
        var state = Status(service).State;
        if (state == ServiceControllerStatus.Stopped) return;
        if (state != ServiceControllerStatus.StopPending
            && !ControlService(service, 1, out _) && Marshal.GetLastPInvokeError() != 1062)
            throw Error("Stop the Windows runner service");
        await WaitAsync(service, ServiceControllerStatus.Stopped, time, ct);
    }

    private static async Task WaitAsync(ScHandle service, ServiceControllerStatus desired, TimeProvider time, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        while (true)
        {
            var status = Status(service);
            if (status.State == desired) return;
            if (status.State == ServiceControllerStatus.Stopped || time.GetElapsedTime(started) >= TimeSpan.FromSeconds(30))
                throw new CliException($"The Windows runner service did not reach {desired} (state {status.State}, exit {status.ExitCode}).");
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }

    internal static void Remove(ScHandle service) => Check(DeleteService(service), "Delete the Windows runner service registration");

    public void Dispose() => _manager.Dispose();

    private static void DemandHandle(ScHandle handle, string operation)
    {
        if (!handle.IsInvalid) return;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw Error(operation, error);
    }

    private static void Check(bool ok, string operation)
    {
        if (!ok) throw Error(operation);
    }

    private static CliException Error(string operation, int? error = null) =>
        new($"{operation} failed. {new Win32Exception(error ?? Marshal.GetLastPInvokeError()).Message}");

    internal sealed class ScHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ScmStatus
    {
        internal uint Type;
        internal ServiceControllerStatus State;
        internal uint Accepted, ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint Type, State, Accepted, ExitCode, ServiceExitCode, CheckPoint, WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        internal uint Type, Startup, Error;
        internal IntPtr Image, LoadOrderGroup;
        internal uint Tag;
        internal IntPtr Dependencies, Account, DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FailureActions
    {
        internal uint ResetPeriod;
        internal IntPtr RebootMessage, Command;
        internal uint Count;
        internal IntPtr Actions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecoveryAction
    {
        internal uint Type, Delay;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern ScHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern ScHandle OpenServiceW(ScHandle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern ScHandle CreateServiceW(ScHandle manager, string name, string displayName, uint access, uint type, uint start,
        uint error, string image, string? group, IntPtr tag, string? dependencies, string account, string? password);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr service);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(ScHandle service);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(ScHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(ScHandle service, int level, out ScmStatus status, int length, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool QueryServiceConfigW(ScHandle service, IntPtr buffer, uint length, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool StartServiceW(ScHandle service, uint count, IntPtr arguments);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool ChangeServiceConfigW(ScHandle service, uint type, uint startup, uint error, string? image, string? group,
        IntPtr tag, string? dependencies, string? account, string? password, string? displayName);
    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool ChangeServiceConfig2W(ScHandle service, uint level, ref uint value);
    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool ChangeServiceConfig2W(ScHandle service, uint level, ref IntPtr value);
    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool ChangeServiceConfig2W(ScHandle service, uint level, ref FailureActions value);
}
