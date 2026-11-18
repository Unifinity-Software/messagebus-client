using MessageBus.Cli.Runner;
using MessageBus.Tests.Shared;
using Xunit;

namespace MessageBus.Cli.Tests.Infrastructure;

/// <summary>Probe restricted Windows run capabilities; ordinary runs and CI still exercise every test.</summary>
internal static class WindowsRunCapabilities
{
    private static readonly Lazy<Task<string?>> GitShell = new(ProbeGitShellAsync);
    private static readonly Lazy<Task<string?>> Junctions = new(ProbeJunctionsAsync);

    public static Task<string?> GitShellUnavailableAsync() => GitShell.Value;

    public static async Task RequireGitShellAsync()
    {
        var reason = await GitShellUnavailableAsync();
        Assert.SkipWhen(reason is not null, reason ?? "");
    }

    public static async Task RequireJunctionsAsync()
    {
        var reason = await Junctions.Value;
        Assert.SkipWhen(reason is not null, reason ?? "");
    }

    private static async Task<string?> ProbeGitShellAsync()
    {
        if (!OperatingSystem.IsWindows() || !InsideRun.Now) return null;
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        if (git is null) return null; // The test handles a missing Git installation.
        using var probe = AgentProcess.Start(new ProcessLaunch { FileName = git, Arguments = ["--exec-path"], WorkingDirectory = Path.GetTempPath() });
        Assert.Equal(0, await probe.WaitForExitAsync());
        var execPath = Assert.Single(probe.Tail()).Trim();
        var shell = Path.GetFullPath(Path.Combine(execPath, "..", "..", "..", "usr", "bin", "sh.exe"));
        if (!File.Exists(shell)) return null;
        using var process = AgentProcess.Start(new ProcessLaunch { FileName = shell, Arguments = ["-c", "exit 0"], WorkingDirectory = Path.GetTempPath() });
        var exit = await process.WaitForExitAsync();
        var error = string.Join('\n', process.Tail());
        // the original issue: local fetch/clone and shell credential helpers need MSYS named objects. Only these
        // known denials justify a skip; other shell or Git failures must remain visible.
        return exit == unchecked((int)0xC0000142)
            || (error.Contains("NtCreateDirectoryObject", StringComparison.Ordinal) && error.Contains("0xC0000022", StringComparison.OrdinalIgnoreCase))
            ? "Windows run sandbox cannot initialize Git's MSYS sh (named-object access denied / STATUS_DLL_INIT_FAILED), needed for local transport and shell helpers; runs outside this sandbox and in CI."
            : null;
    }

    private static async Task<string?> ProbeJunctionsAsync()
    {
        if (!OperatingSystem.IsWindows() || !InsideRun.Now) return null;
        var root = Directory.CreateTempSubdirectory("mbus-junction-probe-");
        var target = Directory.CreateDirectory(Path.Combine(root.FullName, "target"));
        var link = Path.Combine(root.FullName, "link");
        try
        {
            using var process = AgentProcess.Start(new ProcessLaunch
            {
                FileName = "cmd.exe", Arguments = ["/d", "/c", "mklink", "/J", link, target.FullName], WorkingDirectory = root.FullName,
            });
            var exit = await process.WaitForExitAsync();
            var error = string.Join('\n', process.Tail());
            if (exit == 1 && error.Contains("Access is denied.", StringComparison.OrdinalIgnoreCase))
                return "Windows run sandbox denies directory junction creation, needed to test escaping folder links; runs outside this sandbox and in CI.";
            Assert.True(exit == 0, error);
            return null;
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            target.Delete();
            root.Delete();
        }
    }
}
