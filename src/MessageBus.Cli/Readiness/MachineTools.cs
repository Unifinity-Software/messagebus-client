using System.Text.RegularExpressions;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner;
using MessageBus.Contracts.Machines;

namespace MessageBus.Cli.Readiness;

/// <summary>One toolchain as the scan found it.</summary>
/// <param name="Name">Its capability name (<c>tool:{Name}</c>).</param>
/// <param name="DisplayName">How people know it.</param>
/// <param name="Path">Where its command is, when found.</param>
/// <param name="Version">Its version, when it ran.</param>
/// <param name="Works">It ran and answered: only then is it reported as a capability.</param>
/// <param name="Problem">Why it doesn't work, with the fix (null when it works or isn't installed).</param>
/// <param name="Extra">More capabilities it brings (<c>tool:dotnet-sdk-10.0</c>, one per .NET SDK band).</param>
/// <param name="Details">More versions to show (each .NET SDK).</param>
internal sealed record ToolStatus(string Name, string DisplayName, string? Path, string? Version, bool Works, string? Problem,
    IReadOnlyList<string> Extra, IReadOnlyList<string> Details)
{
    /// <summary>Installed (its command was found), whether or not it works.</summary>
    public bool Found => Path is not null;
}

/// <summary>
/// Checks the toolchains tasks build and test with, by running each one (a command on PATH isn't
/// proof: macOS's <c>/usr/bin/git</c> is only a placeholder until Apple's developer tools are
/// installed, and Windows has a <c>python</c> that opens the Store). Read-only; nothing is installed.
/// </summary>
internal static partial class MachineTools
{
    /// <summary>The capability name of git, which every task with a repository needs.</summary>
    public const string Git = "git";

    private sealed record Probe(string Name, string DisplayName, string Command, string[] Args, Func<CommandResult, (string? Version, IReadOnlyList<string> Extra, IReadOnlyList<string> Details)?> Read);

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The capability name of every toolchain checked (<c>tool:{name}</c>), for the dashboard's capability catalogue test.</summary>
    internal static IEnumerable<string> ToolNames => Probes().Select(p => p.Name);

    private static Probe[] Probes() =>
    [
        new(Git, "git", "git", ["--version"], r => r.ExitCode == 0 && GitVersion().Match(r.Output) is { Success: true } m ? (m.Groups[1].Value, [], []) : null),
        new("gh", "GitHub CLI", "gh", ["--version"], r => Version(r, r.Output)),
        new("dotnet", ".NET SDK", "dotnet", ["--list-sdks"], DotnetSdks),
        new("node", "Node.js", "node", ["--version"], r => Version(r, r.Output)),
        new("npm", "npm", "npm", ["--version"], r => Version(r, r.Output)),
        new("pnpm", "pnpm", "pnpm", ["--version"], r => Version(r, r.Output)),
        new("yarn", "Yarn", "yarn", ["--version"], r => Version(r, r.Output)),
        // Windows calls it python; elsewhere python is often Python 2 or missing, and python3 is the one.
        new("python", "Python", OperatingSystem.IsWindows() ? "python" : "python3", ["--version"], r => Version(r, r.AllOutput)),
        new("go", "Go", "go", ["version"], r => Version(r, r.Output)),
        new("cargo", "Rust (cargo)", "cargo", ["--version"], r => Version(r, r.Output)),
        // java -version writes to stderr; macOS has a placeholder java too, which exits 1.
        new("java", "Java", "java", ["-version"], r => Version(r, r.AllOutput)),
        new("docker", "Docker", "docker", ["--version"], r => Version(r, r.Output)),
    ];

    [GeneratedRegex(@"git version (\S+)")]
    private static partial Regex GitVersion();

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.\-]+)?")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^\s*(\d+)\.(\d+)\.(\S+)\s+\[", RegexOptions.Multiline)]
    private static partial Regex SdkLine();

    /// <summary>Checks every toolchain, at once.</summary>
    /// <param name="env">The environment the tools are looked up in (PATH, PATHEXT).</param>
    /// <param name="home">The user's home folder (per-user installs, <see cref="ToolPaths"/>).</param>
    /// <param name="cancellationToken">Stops the checks.</param>
    public static async Task<IReadOnlyList<ToolStatus>> CheckAsync(Func<string, string?> env, string home, CancellationToken cancellationToken)
    {
        var extra = ToolPaths.Existing(home, env);
        var placeholders = await MacPlaceholdersAsync(cancellationToken).ConfigureAwait(false);
        return await Task.WhenAll(Probes().Select(p => CheckOneAsync(p, env, extra, placeholders, cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>
    /// On macOS, the <c>/usr/bin</c> commands that are only placeholders right now. Running one of them
    /// opens Apple's "install the developer tools" (or "install a JDK") dialog on the Mac's screen, which
    /// a scan repeated by the runner must not do, so they are recognised without being run: the developer
    /// tools are missing when <c>xcode-select -p</c> fails, Java when <c>/usr/libexec/java_home</c> does.
    /// </summary>
    private static async Task<IReadOnlySet<string>> MacPlaceholdersAsync(CancellationToken cancellationToken)
    {
        var placeholders = new HashSet<string>(StringComparer.Ordinal);
        if (!OperatingSystem.IsMacOS())
        {
            return placeholders;
        }
        if (await CommandRunner.RunAsync("/usr/bin/xcode-select", ["-p"], cancellationToken, ToolTimeout).ConfigureAwait(false) is not { ExitCode: 0 })
        {
            placeholders.UnionWith(["/usr/bin/git", "/usr/bin/python3"]);
        }
        if (File.Exists("/usr/libexec/java_home")
            && await CommandRunner.RunAsync("/usr/libexec/java_home", [], cancellationToken, ToolTimeout).ConfigureAwait(false) is not { ExitCode: 0 })
        {
            placeholders.Add("/usr/bin/java");
        }
        return placeholders;
    }

    /// <summary>The capabilities the working tools bring: <c>tool:git</c>, <c>tool:dotnet</c>, <c>tool:dotnet-sdk-10.0</c>, ….</summary>
    public static IEnumerable<string> Capabilities(IEnumerable<ToolStatus> tools) =>
        tools.Where(t => t.Works).SelectMany(t => t.Extra.Prepend(t.Name))
            .Select(name => CapabilityNames.Format(CapabilityNames.ToolPrefix, name));

    private static async Task<ToolStatus> CheckOneAsync(Probe probe, Func<string, string?> env, IReadOnlyList<string> extra, IReadOnlySet<string> placeholders,
        CancellationToken cancellationToken)
    {
        var path = CommandLocator.Find(probe.Command, env, extra);
        if (path is null)
        {
            return new ToolStatus(probe.Name, probe.DisplayName, null, null, false,
                probe.Name == Git ? "git isn't installed on this machine, and every task with a repository needs it. " + GitFix : null, [], []);
        }
        if (placeholders.Contains(path))
        {
            return new ToolStatus(probe.Name, probe.DisplayName, path, null, false, probe.Name == Git
                ? $"git ({path}) is only macOS's placeholder: Apple's command line developer tools aren't installed. " + MacGitFix
                : $"{probe.DisplayName} ({path}) is only macOS's placeholder until it is installed.", [], []);
        }
        var run = await CommandRunner.RunAsync(path, probe.Args, cancellationToken, ToolTimeout).ConfigureAwait(false);
        if (run is not null && probe.Read(run) is { } read)
        {
            return new ToolStatus(probe.Name, probe.DisplayName, path, read.Version, true, null, read.Extra, read.Details);
        }
        return new ToolStatus(probe.Name, probe.DisplayName, path, null, false, Problem(probe, path, run), [], []);
    }

    private const string MacGitFix = "Fix: run xcode-select --install on the Mac (or install git with Homebrew), then mbus connect --rescan.";

    /// <summary>How to get a working git, per OS.</summary>
    private static string GitFix =>
        OperatingSystem.IsMacOS() ? MacGitFix
        : OperatingSystem.IsWindows() ? "Fix: install Git for Windows (winget install Git.Git), then mbus connect --rescan."
        : "Fix: install git with your package manager, then mbus connect --rescan.";

    private static string? Problem(Probe probe, string path, CommandResult? run)
    {
        if (probe.Name == Git)
        {
            if (run is not null && IsDeveloperToolsPlaceholder(run.AllOutput))
            {
                return $"git ({path}) is only macOS's placeholder: Apple's command line developer tools aren't installed. " + MacGitFix;
            }
            return $"git ({path}) doesn't run{Said(run)}. " + GitFix;
        }
        if (probe.Name == "dotnet" && run is { ExitCode: 0 })
        {
            return $"dotnet ({path}) has no SDK, only the runtime, so tasks can't build .NET code. Fix: install a .NET SDK (https://dot.net), then mbus connect --rescan.";
        }
        // A placeholder or broken install: say what it said; it isn't offered as a capability.
        return $"{probe.DisplayName} ({path}) doesn't run{Said(run)}.";
    }

    /// <summary>
    /// macOS's <c>/usr/bin/git</c> (and <c>make</c>, <c>clang</c>, …) before the Command Line Tools are
    /// installed: it prints "xcode-select: note: No developer tools were found, requesting install"
    /// (or "invalid active developer path" after an OS update) and exits 1.
    /// </summary>
    public static bool IsDeveloperToolsPlaceholder(string output) =>
        output.Contains("xcode-select", StringComparison.OrdinalIgnoreCase)
        || output.Contains("No developer tools were found", StringComparison.OrdinalIgnoreCase)
        || output.Contains("invalid active developer path", StringComparison.OrdinalIgnoreCase);

    private static string Said(CommandResult? run)
    {
        if (run is null)
        {
            return " (it didn't start or didn't answer)";
        }
        var text = Runner.Agents.AgentText.OneLine(run.AllOutput.Trim(), 160);
        return text is null ? $" (exit {run.ExitCode})" : $" (exit {run.ExitCode}: {text})";
    }

    private static (string?, IReadOnlyList<string>, IReadOnlyList<string>)? Version(CommandResult run, string output) =>
        run.ExitCode == 0 && VersionPattern().Match(output) is { Success: true } m ? (m.Value, [], []) : null;

    private static (string?, IReadOnlyList<string>, IReadOnlyList<string>)? DotnetSdks(CommandResult run)
    {
        if (run.ExitCode != 0)
        {
            return null;
        }
        var sdks = SdkLine().Matches(run.Output).Select(m => (Band: $"{m.Groups[1].Value}.{m.Groups[2].Value}", Full: $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}")).ToList();
        if (sdks.Count == 0)
        {
            return null; // a runtime without an SDK can't build
        }
        var newest = sdks[^1].Full; // dotnet lists them oldest first
        return (newest, [.. sdks.Select(s => "dotnet-sdk-" + s.Band).Distinct(StringComparer.Ordinal)], [.. sdks.Select(s => s.Full)]);
    }
}
