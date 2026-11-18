using System.Text.RegularExpressions;

namespace MessageBus.Cli.Hosting;

/// <summary>
/// Whether the first <c>mbus</c> on PATH is this mbus (the original issue). When it is another file (an old <c>dotnet tool</c> install
/// ahead of the installer's mbus, which isn't on PATH), advice that says "run mbus …" would start that one, so mbus says
/// which one is on PATH and writes its own full path into such advice.
/// </summary>
/// <param name="Running">
/// This mbus's program, or null when it isn't an install: it runs through the dotnet host, or from a build of the source
/// (<c>dotnet run</c>: a program under a project's <c>bin</c> or <c>obj</c>), so a developer isn't warned at every start.
/// </param>
/// <param name="OnPath">The first <c>mbus</c> on PATH, or null when there is none.</param>
internal sealed partial record MbusOnPath(string? Running, string? OnPath)
{
    /// <summary>Whether the owner's shell is Windows' (PowerShell), which decides how a path with a space is written.</summary>
    public bool Windows { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Where <c>dotnet tool install -g</c> puts tools, to tell a global install from a <c>--tool-path</c> one; null: this user's.</summary>
    public string? GlobalToolsPath { get; init; }

    /// <summary>This mbus and the one on PATH, for <paramref name="host"/>.</summary>
    public static MbusOnPath For(CliHost host) =>
        new(RunningProgram(host.MbusCommand), Runner.CommandLocator.Find("mbus", host.GetEnvironmentVariable, []))
        {
            GlobalToolsPath = Update.DotnetToolInstall.GlobalToolsPath(host.GetEnvironmentVariable, host.HomeDirectory),
        };

    /// <summary>Whether a bare <c>mbus</c> would start another program than this one (also when none is on PATH).</summary>
    public bool Differs => Running is not null && (OnPath is null || !SameFile(Running, OnPath));

    /// <summary>How the owner starts this mbus: <c>mbus</c>, or its full path when that would start another one.</summary>
    public string Command => Differs ? ShellCommand(Running!, Windows) : "mbus";

    /// <summary>
    /// The one line that says another mbus is first on PATH, with its version when that is cheap to know and the fix;
    /// null when PATH has this mbus or none.
    /// </summary>
    public string? Warning()
    {
        if (!Differs || OnPath is null)
        {
            return null;
        }
        var tool = ToolInstallAt(OnPath);
        var version = tool is not null ? ToolVersion(OnPath) : null;
        var folder = Path.GetDirectoryName(Running!);
        var fix = tool is not null
            ? $"{tool.UninstallCommand()}, or put {folder} before it on PATH"
            : $"put {folder} before it on PATH, or remove the other one";
        return $"The mbus on PATH is another one ({OnPath}{(version is null ? "" : $", {version}")}), so a command that starts with mbus runs that one. Fix: {fix}.";
    }

    /// <summary><paramref name="text"/> with each <c>mbus &lt;command&gt;</c> it tells the owner to run starting with <see cref="Command"/>.</summary>
    public string Advice(string text) => Differs ? MbusCommandText().Replace(text, m => Command + m.Groups[1].Value) : text;

    /// <summary>The same, for text that may be null.</summary>
    public string? AdviceOrNull(string? text) => text is null ? null : Advice(text);

    private static string? RunningProgram(Mcp.McpLaunchCommand command)
    {
        var program = command.CommandLineStart is [var first, ..] ? first : command.Command;
        return string.Equals(Path.GetFileNameWithoutExtension(program), "dotnet", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(program) || IsSourceBuild(program) ? null : program;
    }

    /// <summary>A program built from the source: under a <c>bin</c> or <c>obj</c> folder beside a project file.</summary>
    internal static bool IsSourceBuild(string program)
    {
        try
        {
            for (var dir = new FileInfo(program).Directory; dir?.Parent is { } parent; dir = parent)
            {
                if ((string.Equals(dir.Name, "bin", StringComparison.OrdinalIgnoreCase) || string.Equals(dir.Name, "obj", StringComparison.OrdinalIgnoreCase))
                    && parent.Exists && parent.EnumerateFiles("*.csproj").Any())
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return false;
    }

    private static bool SameFile(string a, string b) =>
        string.Equals(Resolve(a), Resolve(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>A path with any links on it followed (Homebrew's <c>bin/mbus</c> links into its Cellar).</summary>
    private static string Resolve(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }

    /// <summary>A dotnet tool's shim in its tools folder (<c>.store</c> beside it): the global one, or a <c>--tool-path</c> one; else null.</summary>
    private Update.DotnetToolInstall? ToolInstallAt(string shim) =>
        Path.GetDirectoryName(Path.GetFullPath(shim)) is { } folder && Directory.Exists(Path.Combine(folder, ".store", StorePackage))
            ? new Update.DotnetToolInstall(folder, GlobalToolsPath is { } global && Update.DotnetToolInstall.SamePath(folder, global))
            : null;

    /// <summary>The installed version of a dotnet tool shim, from its tools folder's <c>.store</c> (one version is installed at a time).</summary>
    private static string? ToolVersion(string shim)
    {
        try
        {
            var store = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(shim))!, ".store", StorePackage);
            return Directory.Exists(store) && Directory.GetDirectories(store) is [var only] ? Path.GetFileName(only) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string StorePackage => Update.DotnetToolInstall.PackageId.ToLowerInvariant();

    /// <summary>
    /// <paramref name="path"/> as the start of a command the owner can paste: as it is when it needs no quoting; else in
    /// PowerShell (Windows' shell) with its call operator, <c>&amp; "C:\…\mbus.exe"</c>, since a quoted path alone is only a
    /// string there (a backtick or <c>$</c> in it escaped; Windows paths have no <c>"</c>); elsewhere single-quoted for POSIX shells.
    /// </summary>
    internal static string ShellCommand(string path, bool windows)
    {
        if (PlainPath().IsMatch(path))
        {
            return path;
        }
        return windows ? $"& \"{path.Replace("`", "``", StringComparison.Ordinal).Replace("$", "`$", StringComparison.Ordinal)}\"" : $"'{path.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    [GeneratedRegex(@"^[A-Za-z0-9_./\\:+,@=-]+$")]
    private static partial Regex PlainPath();

    /// <summary>"mbus" followed by one of its commands, not part of a longer word or path.</summary>
    [GeneratedRegex(@"(?<![\w./\\-])mbus( (?:approvals|connect|deadletters|doctor|keys|login|logout|machine|runner|update)\b)")]
    private static partial Regex MbusCommandText();
}
