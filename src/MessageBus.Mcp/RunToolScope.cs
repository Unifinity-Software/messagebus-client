using System.Text.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Runner;

namespace MessageBus.Mcp;

/// <summary>
/// What one runner task's mbus tools may do (the original issue). The runner serves a task's tools itself, as this machine,
/// and the agent reaches them with a credential that is good for that task only and only while it runs; the
/// machine's key never enters the run. This is the scope the runner enforces on each call.
/// </summary>
public sealed record RunToolScope
{
    /// <summary>The task's delivery.</summary>
    public required string DeliveryId { get; init; }

    /// <summary>The task's thread.</summary>
    public required string ThreadId { get; init; }

    /// <summary>The run's folder: every path a tool reads or writes must be inside it.</summary>
    public required string Root { get; init; }

    /// <summary>The tools the run may call; null for all of them (a resumed orchestrator, or a role that allows every mbus tool).</summary>
    public IReadOnlySet<string>? Tools { get; init; }

    /// <summary>Whether calls may name other threads than <see cref="ThreadId"/> (and threads this run started); true when <see cref="Tools"/> is null.</summary>
    public bool OtherThreads => Tools is null;

    /// <summary>The scope for <paramref name="delivery"/> run in <paramref name="root"/>: the tools its agent is shown (<see cref="LocalServer.ShownTools"/>).</summary>
    public static RunToolScope For(TaskDelivery delivery, string root) => new()
    {
        DeliveryId = delivery.DeliveryId,
        ThreadId = delivery.Envelope.ThreadId,
        Root = Path.GetFullPath(root),
        Tools = LocalServer.ShownTools(delivery),
    };

    /// <summary>Why <paramref name="tool"/> with <paramref name="arguments"/> is outside this scope, or null when it is inside.</summary>
    /// <param name="tool">The tool name.</param>
    /// <param name="arguments">The call's arguments, as the agent sent them.</param>
    /// <param name="started">Threads this run started (its own sub-tasks), which it may also name.</param>
    public McpError? Check(string tool, IDictionary<string, JsonElement>? arguments, IReadOnlySet<string> started)
    {
        if (Tools is not null && !Tools.Contains(tool))
            return Refuse(null, $"This task's run can't use {tool}: its role doesn't allow it.");
        if (OtherThreads || arguments is null) return null;

        bool Mine(string? thread) => thread is not null && (thread == ThreadId || started.Contains(thread));
        McpError? Fields(IDictionary<string, JsonElement> args, string at)
        {
            if (Text(args, "deliveryId") is { } delivery && delivery != DeliveryId)
                return Refuse(at + "deliveryId", $"This task's run can only act on its own delivery ({DeliveryId}).");
            if (Text(args, "threadId") is { } thread && !Mine(thread))
                return Refuse(at + "threadId", $"This task's run can only act on its own thread ({ThreadId}) and the threads it started.");
            if (Text(args, "parentThreadId") is { } parent && !Mine(parent))
                return Refuse(at + "parentThreadId", $"This task's run can only start threads under its own ({ThreadId}).");
            if (args.TryGetValue("threadIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                && ids.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String || !Mine(t.GetString())))
                return Refuse(at + "threadIds", $"This task's run can only follow its own thread ({ThreadId}) and the threads it started.");
            return null;
        }

        if (Fields(arguments, "") is { } refused) return refused;
        if (tool == McpToolNames.Send && !SendsWithin(arguments))
            return Refuse("threadId", $"This task's run can only send within its own thread: give threadId {ThreadId} (a follow-up) or parentThreadId {ThreadId} (a sub-task).");
        if (tool == McpToolNames.SendMany && arguments.TryGetValue("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            var parentForAll = Text(arguments, "parentThreadId") is not null;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object) continue;
                var fields = message.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                if (Fields(fields, $"messages[{i}].") is { } inner) return inner;
                if (!parentForAll && !SendsWithin(fields))
                    return Refuse($"messages[{i}].parentThreadId", $"This task's run can only send within its own thread: give parentThreadId {ThreadId}.");
                i++;
            }
        }
        return null;
    }

    /// <summary>Whether <paramref name="path"/> (full) is inside the run's folder, with no symbolic link or junction on the way that could lead out.</summary>
    public bool Contains(string path) => IsInside(Root, path);

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="folder"/> (full) or inside it, with no symbolic link, junction or other
    /// reparse point below <paramref name="folder"/> on the way that could lead out. Parts that don't exist yet aren't checked.
    /// </summary>
    public static bool IsInside(string folder, string path)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        if (!full.Equals(root, comparison) && !full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) return false;
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            // A link counts even when what it points at is missing (it could be created later, outside).
            if (info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))) return false;
        }
        return true;
    }

    /// <summary>
    /// Why the git repository at <paramref name="path"/> (or the one it is in, inside the run folder) mustn't be packaged, or
    /// null: its <c>.git</c> must be a real folder in the run, or a <c>gitdir:</c> file naming one; its <c>commondir</c>, its
    /// object store (with every folder and file in it) and every alternate it borrows from, followed through their own
    /// alternates, must be real folders in the run with no link on the way. The runner packages it as this machine, outside
    /// the task's sandbox, so a repository that borrows another's objects could send that one's code.
    /// </summary>
    public string? RepositoryProblem(string path)
    {
        var start = Path.GetFullPath(path);
        string? gitDir = null;
        for (var dir = start; dir is not null && IsInside(Root, dir); dir = Path.GetDirectoryName(dir))
        {
            var dotGit = Path.Combine(dir, ".git");
            if (File.Exists(dotGit))
            {
                var line = SafeRead(dotGit)?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
                if (line is null) return $"{dotGit} isn't a repository link git can read";
                gitDir = Path.GetFullPath(line["gitdir:".Length..].Trim(), dir);
                if (!IsInside(Root, gitDir)) return $"{dotGit} points at a repository outside this task's run folder ({gitDir})";
                break;
            }
            if (Directory.Exists(dotGit))
            {
                gitDir = dotGit;
                break;
            }
        }
        if (gitDir is null) return null; // not a repository: git says so itself
        if (!IsInside(Root, gitDir)) return $"{gitDir} is a link out of this task's run folder";
        var commonDir = gitDir;
        if (SafeRead(Path.Combine(gitDir, "commondir")) is { } common)
        {
            commonDir = Path.GetFullPath(common.Trim(), gitDir);
            if (!IsInside(Root, commonDir)) return $"{gitDir} shares a repository outside this task's run folder ({commonDir})";
        }
        // A linked worktree keeps its objects in the common folder; both are checked, as git may read either.
        var visited = new HashSet<string>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var objects in new[] { Path.Combine(gitDir, "objects"), Path.Combine(commonDir, "objects") }.Distinct(visited.Comparer))
        {
            if (Directory.Exists(objects) || File.Exists(objects) || new FileInfo(objects).LinkTarget is not null)
            {
                if (ObjectsProblem(objects, 0) is { } problem) return problem;
            }
        }
        return null;

        string? ObjectsProblem(string objects, int depth)
        {
            if (!visited.Add(objects)) return null; // a cycle of alternates: already checked
            if (!IsInside(Root, objects) || !Directory.Exists(objects) || LinkIn(objects) is not null)
                return $"{gitDir} borrows objects from outside this task's run folder ({LinkIn(objects) ?? objects})";
            if (SafeRead(Path.Combine(objects, "info", "alternates")) is not { } alternates) return null;
            foreach (var alternate in alternates.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
            {
                var full = Path.GetFullPath(alternate, objects);
                // git itself follows alternates five deep.
                if (depth >= MaxAlternateDepth) return $"{gitDir} borrows objects through more than {MaxAlternateDepth} alternates ({full})";
                if (ObjectsProblem(full, depth + 1) is { } problem) return problem;
            }
            return null;
        }

        static string? SafeRead(string file)
        {
            try
            {
                return File.Exists(file) ? File.ReadAllText(file) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private const int MaxAlternateDepth = 5;

    /// <summary>The first symbolic link, junction or other reparse point at or beneath <paramref name="folder"/>, or null.</summary>
    private static string? LinkIn(string folder)
    {
        var pending = new Stack<string>([folder]);
        while (pending.TryPop(out var current))
        {
            var info = new DirectoryInfo(current);
            if (IsLink(info)) return current;
            List<FileSystemInfo> entries;
            try
            {
                entries = [.. info.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false })];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return current;
            }
            foreach (var entry in entries)
            {
                if (IsLink(entry)) return entry.FullName;
                if (entry is DirectoryInfo dir) pending.Push(dir.FullName);
            }
        }
        return null;

        static bool IsLink(FileSystemInfo info) => info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    private static bool SendsWithin(IDictionary<string, JsonElement> args) =>
        Text(args, "threadId") is not null || Text(args, "parentThreadId") is not null;

    private static string? Text(IDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static McpError Refuse(string? field, string message) => new() { Code = McpErrorCodes.Forbidden, Field = field, Message = message };
}
