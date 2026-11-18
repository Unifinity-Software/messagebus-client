using System.Text;
using MessageBus.Cli.Runner.Service;

namespace MessageBus.Cli.Update;

/// <summary>A process holding a dotnet-tool install; safe to include in the local update log.</summary>
public sealed record ToolInstallHolder(int Pid, string Name);

/// <summary>
/// Finds install holders from a process snapshot without inspecting or ending real processes.
/// Owner values must match the runner's (including null on platforms where ProcessTable cannot read owners).
/// </summary>
public static class ToolInstallHolders
{
    /// <summary>Leaves out the runner and its descendants while its tasks are still running.</summary>
    public static IReadOnlyList<ToolInstallHolder> WhileWaiting(DotnetToolInstall install, ProcessTable table, int runnerPid)
    {
        var records = table.All();
        var excluded = new HashSet<int> { runnerPid };
        var children = records.ToLookup(p => p.ParentPid);
        var pending = new Queue<int>([runnerPid]);
        while (pending.TryDequeue(out var pid))
        {
            var parent = records.FirstOrDefault(p => p.Pid == pid);
            foreach (var child in children[pid])
            {
                // A parent id reused after this child started does not establish descent.
                if (parent is not null && parent.StartTicks != 0 && child.StartTicks != 0 && parent.StartTicks > child.StartTicks)
                    continue;
                if (excluded.Add(child.Pid))
                    pending.Enqueue(child.Pid);
            }
        }
        return Find(install, records, table.Get(runnerPid)?.Owner, excluded);
    }

    /// <summary>
    /// Leaves out exactly the supplied ids after draining. Include the runner and processes that exit with it;
    /// ancestors and any other descendants still count. The runner's record supplies the user to compare.
    /// </summary>
    public static IReadOnlyList<ToolInstallHolder> FinalCheck(DotnetToolInstall install, ProcessTable table, int runnerPid,
        IReadOnlySet<int> excludedProcessIds) => Find(install, table.All(), table.Get(runnerPid)?.Owner, excludedProcessIds);

    private static IReadOnlyList<ToolInstallHolder> Find(DotnetToolInstall install, IReadOnlyList<ProcessRecord> records,
        string? owner, IReadOnlySet<int> excluded)
    {
        if (Canonical(install.ToolsPath) is not { } tools)
            return [];
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var shim = Path.Combine(tools, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus");
        var store = Path.Combine(tools, ".store", "messagebus.ai.cli") + Path.DirectorySeparatorChar;
        bool InStore(string? path) => Canonical(path) is { } full && full.StartsWith(store, comparison);
        return records.Where(p => !excluded.Contains(p.Pid) && string.Equals(p.Owner, owner, StringComparison.Ordinal)
                && Canonical(p.ExecutablePath) is { } image
                && (string.Equals(image, shim, comparison) || InStore(image)
                    || (string.Equals(Path.GetFileName(image), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet", comparison)
                        && InStore(AssemblyArgument(p.CommandLine)))))
            .Select(p => new ToolInstallHolder(p.Pid, p.Name)).ToArray();
    }

    private static string? Canonical(string? path)
    {
        // A process's working directory is unknown: never resolve its relative arguments against ours.
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
            return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? AssemblyArgument(string? line)
    {
        if (line is null)
            return null;
        var args = new List<string>();
        var token = new StringBuilder();
        bool quoted = false, started = false;
        // ProcessTable reconstructs Unix argv with double quotes around arguments containing spaces.
        // Apostrophes are literal path characters, not shell quoting (no shell is involved here).
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (OperatingSystem.IsWindows() && c == '\\')
            {
                var start = i;
                while (i < line.Length && line[i] == '\\') i++;
                var count = i - start;
                if (i < line.Length && line[i] == '"')
                {
                    token.Append('\\', count / 2);
                    if (count % 2 != 0) token.Append('"');
                    else quoted = !quoted;
                }
                else
                {
                    token.Append('\\', count);
                    i--;
                }
                started = true;
            }
            else if (c == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (!started) continue;
                args.Add(token.ToString());
                token.Clear();
                started = false;
            }
            else
            {
                token.Append(c);
                started = true;
            }
        }
        if (quoted) return null;
        if (started) args.Add(token.ToString());
        var valueOptions = new HashSet<string>(StringComparer.Ordinal)
        { "--roll-forward-on-no-candidate-fx", "--roll-forward", "--runtimeconfig", "--depsfile", "--additionalprobingpath", "--fx-version", "--additional-deps" };
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (i == 1 && arg == "exec") continue;
            if (valueOptions.Contains(arg)) { if (++i >= args.Count) return null; continue; }
            if (arg.StartsWith('-')) continue;
            return arg;
        }
        return null;
    }
}
