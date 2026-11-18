using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Security;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>Installs one exact Apple-offered Command Line Tools package, with authorization owned by macOS.</summary>
internal sealed partial class MacGitSetup(CliHost host, IMacToolSetupSystem? system = null) : IToolSetupPlatform
{
    private readonly IMacToolSetupSystem _system = system ?? new NativeMacToolSetupSystem();
    public MachineOs Os => MachineOs.MacOs;
    public string? DisabledReason
    {
        get
        {
            if (!_system.IsMacOs) return "Apple Command Line Tools setup is available on macOS only.";
            if (RunContext.IsInsideRun(host.GetEnvironmentVariable, _system.InSandbox)) return "Tool setup cannot run inside an agent task or sandbox.";
            if (!string.Equals(host.GetEnvironmentVariable(WindowsGitSetup.OptInVariable)?.Trim(), "on", StringComparison.OrdinalIgnoreCase))
                return $"On this Mac, set {WindowsGitSetup.OptInVariable}=on and restart the runner to allow owner-approved Git setup.";
            if (DeploymentTargets.Current(host.GetEnvironmentVariable) != DeploymentTarget.Pc
                || host.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") is "true") return "Tool setup is unavailable in containers and shared runner images.";
            if (_system.EffectiveUid == 0 || _system.ConsoleUid != _system.EffectiveUid)
                return "Start the runner as the signed-in Mac console user, without sudo. macOS asks for administrator permission separately.";
            return null;
        }
    }

    public async Task<string?> GitVersionAsync(CancellationToken ct)
    {
        var git = await InstalledGitPathAsync(ExecutableLocator.Find(host, "git")?.Path ?? "/usr/bin/git", ct, _system).ConfigureAwait(false);
        if (git is null) return null;
        var result = await _system.RunAsync(git, ["--version"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return result is { ExitCode: 0 } && GitVersion().Match(result.Output) is { Success: true } match ? match.Groups[1].Value : null;
    }

    /// <summary>Never hand Apple's installer-launching Git stub to startup probes or housekeeping.</summary>
    internal static async Task<string?> InstalledGitPathAsync(string? git, CancellationToken ct, IMacToolSetupSystem? system = null)
    {
        if (git is null) return null;
        system ??= new NativeMacToolSetupSystem();
        if (!system.IsMacOs || git != "/usr/bin/git") return git;

        var selected = await system.RunAsync("/usr/bin/xcode-select", ["-p"], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (selected is not { ExitCode: 0 }) return null;
        var developerPath = selected.Output.Trim();
        if (!developerPath.StartsWith("/", StringComparison.Ordinal)) return null;
        var installed = developerPath.TrimEnd('/') + "/usr/bin/git";
        return installed != "/usr/bin/git" && system.FileExists(installed) ? installed : null;
    }

    public async Task<string> ResolveVersionAsync(CancellationToken ct) =>
        (await ResolvePlanAsync(ct).ConfigureAwait(false)).Version;

    public async Task<ToolSetupPlan> ResolvePlanAsync(CancellationToken ct)
    {
        if (DisabledReason is { } why) throw new CliException(why);
        using var marker = _system.CreateCatalogMarker();
        var result = await _system.RunAsync("/usr/sbin/softwareupdate", ["--list"], TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        if (result is not { ExitCode: 0 }) throw new CliException("Apple Software Update could not list available Command Line Tools. Check Software Update on this Mac, then request a new plan.");
        return ReadPlan(result.Output) ?? throw new CliException("Apple Software Update offers no supported stable Command Line Tools package for this Mac. No installer was opened. Check Software Update or Apple's developer downloads, then try again.");
    }

    internal static ToolSetupPlan? ReadPlan(string output) => output.Split('\n')
        .Select(line => line.Trim()).Where(line => line.StartsWith('*'))
        .Select(line => line[1..].Trim()).Select(line => line.StartsWith("Label:", StringComparison.Ordinal) ? line[6..].Trim() : line)
        .Select(label => ToolSetupCatalog.AppleVersion(label) is { } version ? new ToolSetupPlan { Os = MachineOs.MacOs, Version = version, PackageLabel = label } : null)
        .OfType<ToolSetupPlan>().OrderByDescending(plan => Version.Parse(plan.Version)).FirstOrDefault();

    public async Task<int> InstallAsync(ToolSetupPlan plan, CancellationToken ct)
    {
        if (DisabledReason is { } why) throw new CliException(why);
        if (!ToolSetupCatalog.Valid(plan) || plan.Os != MachineOs.MacOs) throw new CliException("The approved plan is not an exact Apple Command Line Tools installation.");
        // softwareupdate rescans after authorization. CLT must remain offered through that scan and installation.
        using var marker = _system.CreateCatalogMarker();
        // No sudo, credential arguments, stdin password, downloaded script, or task-provided command.
        // Apple's own authorization dialog grants this fixed command permission outside every agent run.
        return await _system.InstallAsync("/usr/bin/osascript", ["-e", ToolSetupCatalog.AppleAuthorizationScript(plan)], TimeSpan.FromMinutes(45), ct).ConfigureAwait(false);
    }

    public async Task<bool> VerifyInstalledAsync(ToolSetupPlan plan, string? gitVersion, CancellationToken ct)
    {
        if (!ToolSetupCatalog.Valid(plan) || plan.Os != MachineOs.MacOs || string.IsNullOrWhiteSpace(gitVersion)) return false;
        var receipt = await _system.RunAsync("/usr/sbin/pkgutil", ["--pkg-info", ToolSetupCatalog.ApplePackageId], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return receipt is { ExitCode: 0 } && ReceiptMatches(plan.Version, receipt.Output);
    }

    internal static bool ReceiptMatches(string expected, string output)
    {
        var match = ReceiptVersion().Match(output);
        if (!match.Success || match.Groups[1].Value.Length > 100) return false;
        var installed = match.Groups[1].Value.Split('.');
        var approved = expected.Split('.');
        return GitToolSetup.ValidVersion(expected) && installed.Length >= approved.Length && installed.Length <= 10
            && installed.All(part => part.Length > 0 && part.All(char.IsAsciiDigit))
            && approved.SequenceEqual(installed.Take(approved.Length));
    }

    public void RefreshPath() { } // Apple installs its selected developer tools behind the existing /usr/bin commands.

    [GeneratedRegex(@"git version (\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex GitVersion();
    [GeneratedRegex(@"^version:\s*([0-9.]+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ReceiptVersion();
}

/// <summary>The native boundaries used by the Mac installer, kept injectable for security regression tests.</summary>
internal interface IMacToolSetupSystem
{
    bool IsMacOs { get; }
    /// <summary>Whether this process is in a macOS sandbox (a run's, the original issue): the real Mac's answer; a fake Mac in tests says no.</summary>
    bool InSandbox { get; }
    uint EffectiveUid { get; }
    uint? ConsoleUid { get; }
    bool FileExists(string path);
    IDisposable CreateCatalogMarker();
    Task<CommandResult?> RunAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken ct);
    Task<int> InstallAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken ct);
}

internal sealed class NativeMacToolSetupSystem : IMacToolSetupSystem
{
    public bool IsMacOs => OperatingSystem.IsMacOS();
    public bool InSandbox => OperatingSystem.IsMacOS() && MacSecurity.InSandbox();
    public uint EffectiveUid => GetEffectiveUid();
    public uint? ConsoleUid
    {
        get
        {
            var name = CopyConsoleUser(IntPtr.Zero, out var uid, out _);
            if (name == IntPtr.Zero) return null;
            try
            {
                var text = new byte[1024];
                if (!GetString(name, text, text.Length, 0x08000100)) return null;
                var end = Array.IndexOf(text, (byte)0);
                var user = Encoding.UTF8.GetString(text, 0, end < 0 ? text.Length : end);
                return user is "" or "root" or "loginwindow" || uid == 0 ? null : uid;
            }
            finally { Release(name); }
        }
    }
    public bool FileExists(string path) => File.Exists(path);
    public IDisposable CreateCatalogMarker() => new CatalogMarker("/tmp/.com.apple.dt.CommandLineTools.installondemand.in-progress");
    public Task<CommandResult?> RunAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken ct) => CommandRunner.RunAsync(executable, arguments, ct, timeout);
    public Task<int> InstallAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken ct) => ToolInstallerProcess.RunAsync(executable, arguments, timeout, ct);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();
    [DllImport("/System/Library/Frameworks/SystemConfiguration.framework/SystemConfiguration", EntryPoint = "SCDynamicStoreCopyConsoleUser")]
    private static extern IntPtr CopyConsoleUser(IntPtr store, out uint uid, out uint gid);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFStringGetCString")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GetString(IntPtr value, [Out] byte[] buffer, nint size, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
    private static extern void Release(IntPtr value);

    internal sealed class CatalogMarker : IDisposable
    {
        private readonly string _path;
        private readonly bool _owned;
        public CatalogMarker(string path)
        {
            _path = path;
            try
            {
                using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                _owned = true;
            }
            catch (IOException) when (File.Exists(path)) { } // Never change or remove a marker owned by someone else.
        }
        public void Dispose() { if (_owned) File.Delete(_path); }
    }
}
