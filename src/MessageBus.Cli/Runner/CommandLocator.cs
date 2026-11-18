namespace MessageBus.Cli.Runner;

/// <summary>
/// Finds a command the way a shell would, on Windows (PATH plus PATHEXT, so npm's <c>codex.cmd</c>
/// is found) and on macOS and Linux (PATH plus the executable bit), then in known install folders.
/// A launchd agent on macOS starts with a short PATH, which is why the extra folders matter there.
/// </summary>
public static class CommandLocator
{
    /// <summary>Finds <paramref name="command"/>, or null.</summary>
    /// <param name="command">A bare command name (<c>claude</c>) or a full path.</param>
    /// <param name="getEnvironmentVariable">Reads PATH and PATHEXT.</param>
    /// <param name="extraDirectories">Folders searched after PATH.</param>
    public static string? Find(string command, Func<string, string?> getEnvironmentVariable, IEnumerable<string> extraDirectories)
    {
        if (Path.IsPathFullyQualified(command))
        {
            return IsRunnable(command) ? command : null;
        }
        var path = getEnvironmentVariable("PATH") ?? "";
        var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('"'))
            .Concat(extraDirectories);
        var extensions = OperatingSystem.IsWindows()
            ? (getEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [""];
        foreach (var dir in dirs)
        {
            if (dir.Length == 0)
            {
                continue;
            }
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir, command + ext.ToLowerInvariant());
                    if (IsRunnable(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry.
                }
            }
        }
        return null;
    }

    /// <summary>Where agent installers put their command when it isn't on PATH.</summary>
    public static IReadOnlyList<string> DefaultExtraDirectories(string home)
    {
        var dirs = new List<string>
        {
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".claude", "local"),
            Path.Combine(home, ".npm-global", "bin"),
            Path.Combine(home, ".bun", "bin"),
            Path.Combine(home, ".opencode", "bin"),
        };
        if (OperatingSystem.IsMacOS())
        {
            dirs.Add("/opt/homebrew/bin");
            dirs.Add("/usr/local/bin");
        }
        else if (OperatingSystem.IsWindows() && Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } appData)
        {
            dirs.Add(Path.Combine(appData, "npm"));
        }
        return dirs;
    }

    private static bool IsRunnable(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return false;
            }
            if (OperatingSystem.IsWindows())
            {
                return true;
            }
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(file) & anyExecute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
