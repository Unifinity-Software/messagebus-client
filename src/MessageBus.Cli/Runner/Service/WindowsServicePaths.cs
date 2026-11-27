using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using MessageBus.Contracts.Releases;

namespace MessageBus.Cli.Runner.Service;

internal sealed record WindowsServiceRegistration(string Account, string AccountSid, string Version, string Assembly, string CommandLine, Guid ControlId)
{
    internal string ControlPipe => $"messagebus.ai.runner.admin.{ControlId:N}";
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsServicePaths(string programFiles, string programData, string dotnet)
{
    internal static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    // The SID of the fixed SCM name mbus-runner (sc.exe showsid mbus-runner).
    internal static readonly SecurityIdentifier ServiceSid = new("S-1-5-80-3824446841-3136964026-233876363-534571713-912637471");
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    internal string Dotnet { get; } = dotnet;
    internal string Root { get; } = Path.Combine(programFiles, "MessageBus.Runner");
    internal string HostTools => Path.Combine(Root, "host");
    internal string HostPackageSource => Path.Combine(HostTools, "seed");
    internal string State { get; } = Path.Combine(programData, "MessageBus.Runner");
    internal string WorkerTools => Path.Combine(State, "tool");
    internal string Settings => Path.Combine(State, "mbus");
    internal string Home => Path.Combine(State, "home");
    internal string Runs => Path.Combine(State, "runs");
    internal string MaintenancePath => Path.Combine(Root, "uninstalling");
    private string RegistrationPath => Path.Combine(Root, "registration.json");

    internal static WindowsServicePaths ForMachine()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (programFiles.Length == 0 || programData.Length == 0)
            throw new CliException("The Windows Program Files and ProgramData directories are required.");
        return new(programFiles, programData, Path.Combine(programFiles, "dotnet", "dotnet.exe"));
    }

    internal static void DemandAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(Administrators))
            throw new CliException("Open an elevated terminal as Administrator to manage the Windows runner service.");
    }

    internal static void DemandServiceIdentity(string accountSid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var principal = new WindowsPrincipal(identity);
        if (process.SessionId != 0 || identity.IsSystem || principal.IsInRole(Administrators)
            || principal.IsInRole(new SecurityIdentifier(WellKnownSidType.BuiltinBackupOperatorsSid, null))
            || identity.User != ServiceSid && !principal.IsInRole(ServiceSid)
            || !string.Equals(identity.User?.Value, accountSid, StringComparison.Ordinal))
            throw new CliException("The runner dispatcher and worker require the configured service identity in session 0.");
    }

    internal void CreateRoot()
    {
        DemandTrustedPath(programFiles);
        CreatePrivateDirectory(Root, Security(writable: false));
    }

    internal void CreateState()
    {
        DemandTrustedPath(programData, allowChildCreation: true);
        CreatePrivateDirectory(State, Security(writable: true));
    }

    internal void VerifyState()
    {
        DemandTrustedPath(programData, allowChildCreation: true);
        VerifyPrivateDirectory(State, Security(writable: true));
    }

    internal void VerifyRoot() => VerifyPrivateDirectory(Root, Security(writable: false));

    internal void VerifyDotnet()
    {
        if (!File.Exists(Dotnet)) throw new CliException("Install a machine-wide .NET SDK in Program Files before installing the service.");
        DemandTrustedPath(Dotnet);
    }

    internal void SaveRegistration(WindowsServiceRegistration registration)
    {
        using var stream = new FileStream(RegistrationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, registration);
    }

    internal WindowsServiceRegistration ReadRegistration()
    {
        DemandTrustedPath(programFiles);
        VerifyRoot();
        DemandTrustedPath(RegistrationPath);
        var registration = JsonSerializer.Deserialize<WindowsServiceRegistration>(File.ReadAllText(RegistrationPath));
        if (registration is null || registration.ControlId == Guid.Empty || !ReleaseFeed.IsPinnableVersion(registration.Version)
            || WindowsServicePlan.Create(registration.Account == WindowsServicePlan.VirtualAccount ? null : registration.Account).Account != registration.Account
            || !RunnerProcesses.IsInside(registration.Assembly, HostTools)
            || registration.CommandLine != HostCommandLine(registration.Assembly))
            throw new CliException("The Windows runner service registration file is invalid.");
        DemandTrustedPath(registration.Assembly);
        VerifyDotnet();
        return registration;
    }

    internal string HostCommandLine(string assembly) => $"\"{Dotnet}\" \"{assembly}\" {WindowsRunnerService.HostCommand}";

    // The process looking up tools starts with a private home and an explicit machine PATH.
    internal ProcessStartInfo ProcessStart(string command, string workingDirectory, string home, bool worker)
    {
        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory,
        };
        var system = Environment.SystemDirectory;
        var windows = Path.GetDirectoryName(system)!;
        var machinePath = worker ? MachinePath() : null;
        start.Environment.Clear();
        start.Environment["PATH"] = string.Join(';', new[] { Path.GetDirectoryName(Dotnet)!, system, windows }.Concat(machinePath ?? []));
        start.Environment["SystemRoot"] = windows;
        start.Environment["WINDIR"] = windows;
        start.Environment["ComSpec"] = Path.Combine(system, "cmd.exe");
        start.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        start.Environment["ProgramFiles"] = programFiles;
        start.Environment["ProgramData"] = programData;
        start.Environment["PROCESSOR_ARCHITECTURE"] = RuntimeInformation.ProcessArchitecture.ToString().Replace("X64", "AMD64");
        start.Environment["HOME"] = home;
        start.Environment["USERPROFILE"] = home;
        start.Environment["APPDATA"] = Directory.CreateDirectory(Path.Combine(home, "Roaming")).FullName;
        start.Environment["LOCALAPPDATA"] = Directory.CreateDirectory(Path.Combine(home, "Local")).FullName;
        start.Environment["TEMP"] = start.Environment["TMP"] = Directory.CreateDirectory(Path.Combine(home, "Temp")).FullName;
        start.Environment["DOTNET_CLI_HOME"] = home;
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(Dotnet)!;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["NUGET_PACKAGES"] = Path.Combine(home, "packages");
        start.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(home, "nuget-http");
        start.Environment["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(home, "nuget-plugins");
        start.Environment["NUGET_SCRATCH"] = Path.Combine(home, "nuget-scratch");
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        return start;
    }

    private static IEnumerable<string> MachinePath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        return (key?.GetValue("Path") as string ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Trim('"'))
            .Where(path => Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal));
    }

    // An existing reparse point is an error, even when its target is missing.
    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    internal static void DemandRealDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new CliException($"The Windows runner service requires a real directory at {path}.");
    }

    internal static void DemandTrustedPath(string path, bool allowChildCreation = false)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new CliException("The Windows runner service requires a local, absolute installation path.");
        for (string? item = path; item is not null; item = Path.GetDirectoryName(item))
        {
            var attributes = File.GetAttributes(item);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new CliException($"The Windows runner service will not use a reparse point at {item}.");
            var security = attributes.HasFlag(FileAttributes.Directory)
                ? (FileSystemSecurity)new DirectoryInfo(item).GetAccessControl()
                : new FileInfo(item).GetAccessControl();
            var volumeRoot = Path.GetDirectoryName(item) is null;
            var write = ~FileSystemRights.ReadAndExecute & ~FileSystemRights.Synchronize;
            // ProgramData and volume roots can grant create rights. The fixed child is created atomically
            // with a protected ACL. Parent permissions may never allow an untrusted user to replace it.
            if (volumeRoot || (item == path && allowChildCreation))
                write &= ~(FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes);
            if (new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null
                || !Trusted(security.GetOwner(typeof(SecurityIdentifier))) || security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>().Any(rule => rule.AccessControlType == AccessControlType.Allow
                    && !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) && (rule.FileSystemRights & write) != 0
                    && !Trusted(rule.IdentityReference)))
                throw new CliException($"The Windows runner service requires administrator-controlled ownership and permissions at {item}.");
        }
    }

    internal static void OwnPayload(string path)
    {
        // The installer creates this tree inside the protected parent. Normalize the owner in case
        // Windows policy sets new object owners to the installing user instead of Administrators.
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new CliException("The NuGet host payload contains a reparse point.");
            if (attributes.HasFlag(FileAttributes.Directory)) OwnPayload(entry);
            else
            {
                var security = new FileSecurity();
                security.SetOwner(Administrators);
                new FileInfo(entry).SetAccessControl(security);
            }
        }
        var directory = new DirectorySecurity();
        directory.SetOwner(Administrators);
        new DirectoryInfo(path).SetAccessControl(directory);
    }

    private static bool Trusted(IdentityReference? sid) => sid == Administrators || sid == LocalSystem || sid == TrustedInstaller;

    internal static DirectorySecurity Security(bool writable)
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(true, false);
        const InheritanceFlags children = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, children, PropagationFlags.None, AccessControlType.Allow));
        if (writable)
        {
            // Allow creation inside state, while keeping ownership and deletion of state itself with Administrators.
            security.AddAccessRule(new FileSystemAccessRule(ServiceSid, FileSystemRights.ReadAndExecute | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(ServiceSid, FileSystemRights.Modify | FileSystemRights.DeleteSubdirectoriesAndFiles,
                children, PropagationFlags.InheritOnly, AccessControlType.Allow));
        }
        else security.AddAccessRule(new FileSystemAccessRule(ServiceSid, FileSystemRights.ReadAndExecute, children, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void VerifyPrivateDirectory(string path, DirectorySecurity expected)
    {
        DemandRealDirectory(path);
        var actual = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        static IEnumerable<string> Rules(DirectorySecurity directory) => directory.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Select(rule => $"{rule.IdentityReference}:{(int)rule.FileSystemRights}:{rule.AccessControlType}:{rule.InheritanceFlags}:{rule.PropagationFlags}:{rule.IsInherited}")
            .Order(StringComparer.Ordinal);
        if (!actual.AreAccessRulesProtected || actual.GetOwner(typeof(SecurityIdentifier)) != Administrators
            || !Rules(actual).SequenceEqual(Rules(expected)))
            throw new CliException($"The Windows runner service directory has unexpected ownership or permissions at {path}.");
    }

    private static void CreatePrivateDirectory(string path, DirectorySecurity security)
    {
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            if (!CreateDirectoryW(path, ref attributes))
                throw new CliException($"Could not create a new protected Windows runner directory at {path}. {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }
        finally { pinned.Free(); }
        VerifyPrivateDirectory(path, security);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr Descriptor;
        internal int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);
}
