using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Security;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>The fixed Windows platform implementation. It never accepts command text from an agent or the service.</summary>
internal sealed partial class WindowsGitSetup(CliHost host) : IToolSetupPlatform
{
    public const string OptInVariable = "MBUS_RUNNER_TOOL_INSTALLER";

    public string? DisabledReason
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return "Git setup is available on Windows only.";
            if (RunContext.IsInsideRun(host.GetEnvironmentVariable)) return "Tool setup cannot run inside an agent task.";
            if (!string.Equals(host.GetEnvironmentVariable(OptInVariable)?.Trim(), "on", StringComparison.OrdinalIgnoreCase))
                return $"On this machine, set {OptInVariable}=on and restart the runner to allow owner-approved Git setup.";
            if (DeploymentTargets.Current(host.GetEnvironmentVariable) != DeploymentTarget.Pc
                || host.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") is "true")
                return "Tool setup is unavailable in containers and shared runner images.";
            if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
                return "Start the runner in your signed-in Windows session so you can answer its permission prompt.";
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                return "Start the runner without administrator rights. Windows asks for permission separately when Git is installed.";
            return null;
        }
    }

    public async Task<string?> GitVersionAsync(CancellationToken ct)
    {
        var path = ExecutableLocator.Find(host, "git")?.Path;
        if (path is null) return null;
        var result = await CommandRunner.RunAsync(path, ["--version"], ct).ConfigureAwait(false);
        return result is { ExitCode: 0 } && GitVersion().Match(result.Output) is { Success: true } match ? match.Groups[1].Value : null;
    }

    public async Task<string> ResolveVersionAsync(CancellationToken ct)
    {
        if (DisabledReason is { } why) throw new CliException(why);
        var result = await CommandRunner.RunAsync(Winget(),
            ["show", "--id", GitToolSetup.PackageId, "--exact", "--source", GitToolSetup.Source, "--versions", "--accept-source-agreements", "--disable-interactivity"],
            ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (result is not { ExitCode: 0 }) throw new CliException("winget couldn't resolve Git. Open a terminal on this machine and check winget show --id Git.Git --exact --source winget.");
        return ReadVersionList(result.Output) ?? throw new CliException("winget did not list a numeric Git package version.");
    }

    internal static string? ReadVersionList(string output) => output.Split('\n').Select(l => l.Trim()).FirstOrDefault(GitToolSetup.ValidVersion);

    public Task<int> InstallAsync(ToolSetupPlan plan, CancellationToken ct)
    {
        if (DisabledReason is { } why) throw new CliException(why);
        if (!ToolSetupCatalog.Valid(plan) || plan.Os != MachineOs.Windows) throw new CliException("The approved plan is not an exact Windows Git installation.");
        var executable = Winget();
        // No shell, RunAs verb, stored credentials or privilege helper. winget owns any native UAC request.
        // The bounded, validated version is the only variable in this fixed argument list.
        return ToolInstallerProcess.RunAsync(executable, GitToolSetup.Arguments(plan.Version), TimeSpan.FromMinutes(10), ct);
    }

    public void RefreshPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Installers change the registry PATH, not the environment of an already-running runner.
        // Keep its existing paths and add the OS/user paths without writing anything back to the registry.
        var entries = new[] { host.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) }
            .Where(p => !string.IsNullOrWhiteSpace(p)).SelectMany(p => p!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        Environment.SetEnvironmentVariable("PATH", string.Join(';', entries));
    }

    private string Winget() => ExecutableLocator.Find(host, "winget")?.Path
        ?? throw new CliException("winget isn't available in this Windows session. Install or update Microsoft's App Installer, then restart the runner.");

    [GeneratedRegex(@"git version (\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex GitVersion();
}
