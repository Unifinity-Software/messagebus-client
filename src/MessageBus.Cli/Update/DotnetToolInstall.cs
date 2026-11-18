namespace MessageBus.Cli.Update;

/// <summary>
/// An mbus installed with <c>dotnet tool install</c> (the original issue). dotnet keeps each version in its own folder under the tools
/// path's <c>.store</c>, with <see cref="SettingsFile"/> beside <c>mbus.exe</c>, and starts it through a small
/// <c>mbus.cmd</c> in the tools path. So mbus never replaces its own file there (dotnet would no longer know which version
/// is installed): <c>dotnet tool update</c> does, and anything that starts mbus later goes through <see cref="Shim"/>,
/// because the versioned folder is deleted by the next update.
/// </summary>
/// <param name="ToolsPath">The folder holding <c>.store</c> and the shim; null for a local tool (a tool manifest's).</param>
/// <param name="Global">Whether <see cref="ToolsPath"/> is the global tools folder (<c>dotnet tool install -g</c>).</param>
public sealed record DotnetToolInstall(string? ToolsPath, bool Global)
{
    /// <summary>The installed tool containing this entry assembly, including framework-dependent tools hosted by dotnet.</summary>
    public static DotnetToolInstall? Running => Find(Environment.ProcessPath) ?? Find(System.Reflection.Assembly.GetEntryAssembly()?.Location);

    /// <summary>The package on nuget.org (the project's <c>PackageId</c>).</summary>
    public const string PackageId = "MessageBus.Ai.Cli";

    /// <summary>The file dotnet's tool packages carry beside the program.</summary>
    public const string SettingsFile = "DotnetToolSettings.xml";

    /// <summary>The script dotnet starts mbus with, in <see cref="ToolsPath"/>; null when there is none.</summary>
    public string? Shim => ToolsPath is null ? null :
        new[] { OperatingSystem.IsWindows() ? "mbus.exe" : "mbus", "mbus.cmd" }
            .Select(name => Path.Combine(ToolsPath, name)).FirstOrDefault(File.Exists);

    /// <summary>The command that updates this install; with <paramref name="version"/>, to exactly that version.</summary>
    /// <param name="version">A pinned version, or null for the latest.</param>
    /// <param name="currentVersion">The running version; defaults to the CLI assembly version.</param>
    public string UpdateCommand(string? version = null, string? currentVersion = null)
    {
        var where = ToolsPath is null ? "" : Global ? "-g " : $"--tool-path {Quote(ToolsPath)} ";
        var suffix = version is not null ? $" --version {version}"
            : (currentVersion ?? Hosting.CliHost.Version).Split('+')[0].Contains('-') ? " --prerelease" : "";
        return $"dotnet tool update {where}{PackageId}{suffix}";
    }

    /// <summary>The command that removes this install (the original issue: an old one ahead of another mbus on PATH).</summary>
    public string UninstallCommand()
    {
        var where = ToolsPath is null ? "" : Global ? "-g " : $"--tool-path {Quote(ToolsPath)} ";
        return $"dotnet tool uninstall {where}{PackageId}";
    }

    /// <summary>Why mbus doesn't install a release over itself here, and what to run instead.</summary>
    /// <param name="version">A pinned version, or null for the latest.</param>
    public string RefusalMessage(string? version = null) =>
        $"This mbus was installed with dotnet tool, so dotnet updates it. Run: {UpdateCommand(version)}";

    /// <summary>The dotnet tool install <paramref name="mbusPath"/> belongs to, or null when it isn't one.</summary>
    /// <param name="mbusPath">The mbus program (the running one, or <c>MBUS_UPDATE_TARGET</c>).</param>
    public static DotnetToolInstall? Find(string? mbusPath)
    {
        if (string.IsNullOrEmpty(mbusPath) || Path.GetDirectoryName(Path.GetFullPath(mbusPath)) is not { } folder
            || !File.Exists(Path.Combine(folder, SettingsFile)))
        {
            return null;
        }
        // <tools>\.store\<id>\<version>\<id>.<rid>\<version>\tools\<tfm>\<rid>\mbus.exe; a local tool runs from the NuGet cache.
        for (var dir = new DirectoryInfo(folder); dir.Parent is { } parent; dir = parent)
        {
            if (string.Equals(dir.Name, ".store", StringComparison.OrdinalIgnoreCase))
            {
                return new DotnetToolInstall(parent.FullName, SamePath(parent.FullName, GlobalToolsPath()));
            }
        }
        return new DotnetToolInstall(null, false);
    }

    /// <summary>Where <c>dotnet tool install -g</c> puts tools: <c>DOTNET_CLI_HOME</c>, else the user's home, then <c>.dotnet\tools</c>.</summary>
    private static string GlobalToolsPath() =>
        GlobalToolsPath(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The same, for an environment and home folder.</summary>
    internal static string GlobalToolsPath(Func<string, string?> env, string home) =>
        Path.Combine(env("DOTNET_CLI_HOME") is { Length: > 0 } cliHome ? cliHome : home, ".dotnet", "tools");

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}
