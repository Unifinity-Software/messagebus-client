using MessageBus.Cli.Hosting;

namespace MessageBus.Cli.Agents;

/// <summary>Where a command was found.</summary>
/// <param name="Path">Full path of the executable.</param>
/// <param name="OnPath">True when it was found through PATH, false when only in a known install folder.</param>
public sealed record LocatedExecutable(string Path, bool OnPath);

/// <summary>
/// Finds a command the way a shell would: each PATH folder in order, with the PATHEXT extensions on
/// Windows (npm installs <c>claude.cmd</c> there) and the executable bit elsewhere; then the known
/// install folders (<see cref="CliHost.ExtraAgentDirectories"/>).
/// </summary>
public static class ExecutableLocator
{
    /// <summary>Finds <paramref name="command"/>, or null.</summary>
    public static LocatedExecutable? Find(CliHost host, string command)
    {
        var path = host.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Probe(host, dir.Trim('"'), command) is { } found)
            {
                return new LocatedExecutable(found, OnPath: true);
            }
        }
        foreach (var dir in host.ExtraAgentDirectories)
        {
            if (Probe(host, dir, command) is { } found)
            {
                return new LocatedExecutable(found, OnPath: false);
            }
        }
        return null;
    }

    private static string? Probe(CliHost host, string dir, string command)
    {
        if (dir.Length == 0)
        {
            return null;
        }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var extensions = (host.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var ext in extensions)
                {
                    var candidate = System.IO.Path.Combine(dir, command + ext.ToLowerInvariant());
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                return null;
            }

            var file = System.IO.Path.Combine(dir, command);
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return File.Exists(file) && (File.GetUnixFileMode(file) & anyExecute) != 0 ? file : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
