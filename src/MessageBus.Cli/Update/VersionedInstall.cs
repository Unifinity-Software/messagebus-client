using System.Runtime.InteropServices;
using System.Text;

namespace MessageBus.Cli.Update;

/// <summary>
/// Where an updated <c>mbus</c> lives (the original issue): each version in a folder of its own beside the <c>mbus</c> on PATH, which
/// only ever names the current one. The file a running mbus started from never changes while it runs.
/// </summary>
/// <remarks>
/// <para>
/// A single-file .NET program keeps the code it has loaded, but opens its own file by path again each time it needs
/// more of itself. When an update replaced the file at that path (a rename over it on macOS and Linux, a rename to
/// <c>mbus.exe.old</c> on Windows), every mbus still running the old version, such as an agent's <c>mbus mcp</c>, read
/// the new file at the old one's offsets and failed ("Could not load file or assembly") until it was restarted.
/// </para>
/// <code>
/// ~/.local/bin/mbus                         a symlink to .mbus-versions/0.3.0/mbus (macOS, Linux)
/// ~/.local/bin/.mbus-versions/0.3.0/mbus    the current version
/// ~/.local/bin/.mbus-versions/0.2.0/mbus    the one before it
/// </code>
/// <para>
/// macOS and Linux: <c>mbus</c> is a symlink, switched with one rename. The .NET host resolves it when it starts, so a
/// running mbus reads its own version's file. Windows can't make symlinks without Developer Mode, so there <c>mbus.exe</c>
/// is another name for (a hard link to, else a copy of) the current version's file, and <c>mbus mcp</c> and
/// <c>mbus runner</c> started from it start that file and wait for it (<see cref="HandOff"/>): the process doing the work
/// never runs from a path that changes. An install that is still one plain file moves into this layout during its first
/// update. Versions other than the current one and the one before are deleted by a later update once nothing runs them.
/// </para>
/// </remarks>
public static class VersionedInstall
{
    /// <summary>The folder beside <c>mbus</c> holding one folder per version.</summary>
    public const string FolderName = ".mbus-versions";

    /// <summary>The commands that run for long, which Windows starts from the current version's own file.</summary>
    private static readonly string[] LongRunning = ["mcp", "runner"];

    /// <summary>This process's own file, held open with a shared lock so an update doesn't delete it (<see cref="MarkRunning"/>).</summary>
    private static FileStream? _running;

    /// <summary>The folder of versions beside <paramref name="stable"/>.</summary>
    /// <param name="stable">The <c>mbus</c> on PATH.</param>
    public static string VersionsFolder(string stable) => Path.Combine(Path.GetDirectoryName(stable)!, FolderName);

    /// <summary>
    /// The <c>mbus</c> on PATH that <paramref name="path"/>, a version's file (<c>&lt;dir&gt;/.mbus-versions/0.3.0/mbus</c>),
    /// belongs to: <c>&lt;dir&gt;/mbus</c>, when it exists. Null for any other path. Agents' MCP settings, the runner's service and
    /// its restart use that path, which keeps working after a later update deletes this version.
    /// </summary>
    /// <param name="path">A running mbus (<see cref="Environment.ProcessPath"/>).</param>
    public static string? StableFor(string path) =>
        StableCandidate(path) is { } stable && File.Exists(stable) ? stable : null;

    /// <summary>
    /// The version's file <paramref name="stable"/> runs now: the symlink's target on macOS and Linux; on Windows the
    /// version whose file is <paramref name="stable"/> (same size and time: a hard link or a copy). Null when
    /// <paramref name="stable"/> is a plain install, not yet in this layout.
    /// </summary>
    /// <param name="stable">The <c>mbus</c> on PATH.</param>
    public static string? Current(string stable)
    {
        try
        {
            var self = new FileInfo(stable);
            if (!OperatingSystem.IsWindows())
            {
                return self.LinkTarget is not null && self.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true } target
                    && StableCandidate(target.FullName) is { } owner && SamePath(owner, stable)
                    ? target.FullName
                    : null;
            }
            var versions = VersionsFolder(stable);
            if (!self.Exists || !Directory.Exists(versions))
            {
                return null;
            }
            foreach (var folder in Directory.EnumerateDirectories(versions))
            {
                var candidate = new FileInfo(Path.Combine(folder, self.Name));
                if (candidate.Exists && candidate.Length == self.Length && candidate.LastWriteTimeUtc == self.LastWriteTimeUtc)
                {
                    return candidate.FullName;
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes a new folder for <paramref name="version"/> (<c>0.3.0</c>, or <c>0.3.0_2</c> when that is taken) and returns the
    /// path the version's file goes to. The download is written and started there, and never moves afterwards.
    /// </summary>
    /// <param name="stable">The <c>mbus</c> on PATH.</param>
    /// <param name="version">The version being installed.</param>
    public static string NewVersionFile(string stable, string version)
    {
        var folder = FreeFolder(VersionsFolder(stable), FolderNameFor(version));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, Path.GetFileName(stable));
    }

    /// <summary>On macOS and Linux, gives the new file the installed one's mode (and the owner's execute bit) so it can be started.</summary>
    /// <param name="file">The verified download.</param>
    /// <param name="stable">The <c>mbus</c> it is for.</param>
    public static void MakeRunnable(string file, string stable)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, File.GetUnixFileMode(stable) | UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Deletes a version that wasn't installed after all (its folder, and the versions folder if that is now empty).</summary>
    /// <param name="file">The file <see cref="NewVersionFile"/> gave.</param>
    public static void Discard(string file)
    {
        var folder = Path.GetDirectoryName(file)!;
        TryDeleteFolder(folder);
        try
        {
            var versions = Path.GetDirectoryName(folder)!;
            if (Directory.Exists(versions) && !Directory.EnumerateFileSystemEntries(versions).Any())
            {
                Directory.Delete(versions);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Makes <paramref name="stable"/> name <paramref name="file"/>, never leaving it missing on macOS and Linux (one
    /// rename of a new symlink over it). Returns the version it named before, which stays where it is: a plain install
    /// first moves into a folder of its own (named after <paramref name="currentVersion"/>); null when it couldn't be kept.
    /// </summary>
    /// <param name="stable">The <c>mbus</c> on PATH.</param>
    /// <param name="file">The new version's checked file (<see cref="NewVersionFile"/>).</param>
    /// <param name="currentVersion">The version installed now, to name its folder when it is a plain install.</param>
    /// <exception cref="IOException">Nothing changed.</exception>
    public static string? Switch(string stable, string file, string currentVersion)
    {
        var previous = Current(stable);
        return OperatingSystem.IsWindows() ? SwitchWindows(stable, file, previous, currentVersion) : SwitchUnix(stable, file, previous, currentVersion);
    }

    /// <summary>
    /// Deletes versions other than <paramref name="keep"/> that nothing runs any more, and what earlier updates left
    /// beside <paramref name="stable"/> (<c>mbus.old</c>, abandoned downloads). Best effort: what is still running stays for
    /// the next update to try again.
    /// </summary>
    /// <param name="stable">The <c>mbus</c> on PATH.</param>
    /// <param name="keep">The versions' files to keep (the current one and the one before).</param>
    public static void CleanUp(string stable, params string?[] keep)
    {
        var name = Path.GetFileName(stable);
        var versions = VersionsFolder(stable);
        try
        {
            if (Directory.Exists(versions))
            {
                foreach (var folder in Directory.EnumerateDirectories(versions))
                {
                    if (!keep.Any(k => k is not null && SamePath(Path.GetDirectoryName(k)!, folder)) && !InUse(Path.Combine(folder, name)))
                    {
                        TryDeleteFolder(folder);
                    }
                }
                // Windows: other names of earlier versions' files, moved aside while they ran.
                foreach (var file in Directory.EnumerateFiles(versions, ".retired.*"))
                {
                    TryDelete(file);
                }
            }
            // The layout before the original issue left mbus.old (mbus.old-2, … on Windows) and, when interrupted, downloads beside mbus.
            var dir = Path.GetDirectoryName(stable)!;
            var downloads = $".{Path.GetFileNameWithoutExtension(stable)}.*.download{Path.GetExtension(stable)}";
            foreach (var file in Directory.EnumerateFiles(dir, name + ".old*").Concat(Directory.EnumerateFiles(dir, downloads))
                .Concat(Directory.EnumerateFiles(dir, $".{name}.*.link")))
            {
                TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Program.cs, first thing: on macOS and Linux an mbus running from a version's folder holds its own file open with a
    /// shared lock for as long as it runs, which tells a later update's <see cref="CleanUp"/> not to delete it (it can't
    /// get the exclusive lock). Windows needs nothing: it refuses to delete a running program.
    /// </summary>
    public static void MarkRunning()
    {
        if (OperatingSystem.IsWindows() || Environment.ProcessPath is not { } self)
        {
            return;
        }
        try
        {
            // The host resolved the symlink it was started through; ProcessPath may still name the symlink.
            var file = new FileInfo(self).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? self;
            if (StableCandidate(file) is not null)
            {
                _running = new FileStream(file, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read | FileShare.Delete, BufferSize = 0 });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Program.cs, first thing: on Windows, <c>mbus mcp</c> and <c>mbus runner</c> started from <c>mbus.exe</c> on PATH
    /// start the current version's own file with the same arguments, wait for it and return its exit code, so the process
    /// doing the work never runs from a path the next update changes. Null when this process should do the work itself
    /// (any other command or platform, a plain install, or the version's file couldn't be started).
    /// </summary>
    /// <param name="args">The command line.</param>
    public static int? HandOff(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length == 0 || !LongRunning.Contains(args[0], StringComparer.Ordinal)
            || Environment.ProcessPath is not { } self || StableCandidate(self) is not null || Current(self) is not { } file)
        {
            return null;
        }
        return VersionLauncher.Run(file, args);
    }

    private static string? SwitchUnix(string stable, string file, string? previous, string currentVersion)
    {
        var dir = Path.GetDirectoryName(stable)!;
        if (previous is null && new FileInfo(stable) is { Exists: true, LinkTarget: null })
        {
            previous = KeepPlainInstall(stable, currentVersion);
        }
        var link = Path.Combine(dir, $".{Path.GetFileName(stable)}.{Guid.NewGuid():N}.link");
        // Relative, so the folder still works when moved or mounted elsewhere.
        File.CreateSymbolicLink(link, Path.GetRelativePath(dir, file));
        try
        {
            File.Move(link, stable, overwrite: true);
        }
        catch
        {
            TryDelete(link);
            throw;
        }
        return previous;
    }

    private static string? SwitchWindows(string stable, string file, string? previous, string currentVersion)
    {
        // A running program can be renamed but not overwritten or deleted: mbus.exe moves aside, into the versions folder.
        // A plain install becomes the previous version's file; otherwise it already is another name for that file.
        string moved;
        if (previous is null)
        {
            var folder = FreeFolder(VersionsFolder(stable), FolderNameFor(currentVersion));
            Directory.CreateDirectory(folder);
            moved = previous = Path.Combine(folder, Path.GetFileName(stable));
        }
        else
        {
            moved = Path.Combine(VersionsFolder(stable), $".retired.{Guid.NewGuid():N}{Path.GetExtension(stable)}");
        }
        File.Move(stable, moved);
        try
        {
            if (!CreateHardLinkW(stable, file, IntPtr.Zero))
            {
                // Not NTFS, say: a copy, which keeps the file's time (Current matches on size and time).
                File.Copy(file, stable);
            }
        }
        catch
        {
            File.Move(moved, stable);
            throw;
        }
        return previous;
    }

    /// <summary>The plain file a first update replaces, kept as its version (a hard link, else a copy); null if it can't be.</summary>
    private static string? KeepPlainInstall(string stable, string version)
    {
        var folder = FreeFolder(VersionsFolder(stable), FolderNameFor(version));
        var kept = Path.Combine(folder, Path.GetFileName(stable));
        try
        {
            Directory.CreateDirectory(folder);
            if (Link(stable, kept) != 0)
            {
                File.Copy(stable, kept);
            }
            return kept;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFolder(folder);
            return null;
        }
    }

    /// <summary>Whether a running mbus holds <paramref name="file"/> (<see cref="MarkRunning"/>). Windows: deleting it fails instead.</summary>
    private static bool InUse(string file)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(file))
        {
            return false;
        }
        try
        {
            // The exclusive lock .NET takes for FileShare.None can't be had while any process holds a shared one.
            using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary><c>&lt;dir&gt;/mbus</c> for <c>&lt;dir&gt;/.mbus-versions/&lt;version&gt;/mbus</c>, whether or not it exists; else null.</summary>
    private static string? StableCandidate(string path)
    {
        var folder = Path.GetDirectoryName(path);
        var versions = folder is null ? null : Path.GetDirectoryName(folder);
        return versions is not null && string.Equals(Path.GetFileName(versions), FolderName, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(versions) is { } dir
            ? Path.Combine(dir, Path.GetFileName(path))
            : null;
    }

    /// <summary>A version as a folder name: letters, digits and <c>. _ - +</c>, never starting with a dot.</summary>
    private static string FolderNameFor(string version)
    {
        var name = new StringBuilder(version.Length);
        foreach (var c in version.Trim())
        {
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+' ? c : '_');
        }
        return name.Length == 0 || name[0] == '.' ? "v" + name : name.ToString();
    }

    private static string FreeFolder(string versions, string name)
    {
        var folder = Path.Combine(versions, name);
        for (var n = 2; Directory.Exists(folder) || File.Exists(folder); n++)
        {
            folder = Path.Combine(versions, $"{name}_{n}");
        }
        return folder;
    }

    private static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still running (Windows), or not ours to delete.
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still running (Windows), or not ours to delete: the next update tries again.
        }
    }

    private static int Link(string existing, string link)
    {
        try
        {
            return LinkUnix(existing, link);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr securityAttributes);
}
