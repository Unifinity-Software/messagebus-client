using System.Text.Json;
using System.Text.Json.Nodes;

namespace MessageBus.Mcp;

/// <summary>
/// the original issue: the newest commit this machine's mbus sent to each target (a role, an agent) for each repository, in
/// <c>sent-code.json</c> in mbus's settings folder. The next git bundle for that target starts there, so only the new
/// commits travel; a runner that lacks the earlier ones asks for them (<see cref="LocalTools"/> answers). Entries no send
/// has touched for <see cref="KeepFor"/> are dropped: runners delete repository copies unused for that long.
/// </summary>
/// <param name="configDirectory">mbus's settings folder.</param>
/// <param name="time">Clock.</param>
/// <param name="fileName">Local commit record file.</param>
public sealed class SentCode(string configDirectory, TimeProvider time, string fileName = "sent-code.json")
{
    /// <summary>How long a record is kept after its last send (a runner's repository copies go after 30 days unused).</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    private static readonly object Gate = new();

    /// <summary>The file.</summary>
    public string File { get; } = Path.Combine(configDirectory, fileName);

    /// <summary>The newest commit sent to <paramref name="target"/> for the repository whose first commit is <paramref name="rootCommit"/>, or null.</summary>
    public string? LastSent(string workspaceId, string target, string rootCommit)
    {
        lock (Gate)
        {
            return Read().TryGetValue(Key(workspaceId, target, rootCommit), out var entry) && Fresh(entry) ? entry.Commit : null;
        }
    }

    /// <summary>Records that <paramref name="commit"/> went to <paramref name="target"/> (best effort: a file that can't be written is left as it was).</summary>
    public void Record(string workspaceId, string target, string rootCommit, string commit)
    {
        lock (Gate)
        {
            try
            {
                var entries = Read();
                entries[Key(workspaceId, target, rootCommit)] = new Entry(commit, time.GetUtcNow());
                var json = new JsonObject();
                foreach (var (key, entry) in entries.Where(e => Fresh(e.Value)).OrderBy(e => e.Key, StringComparer.Ordinal))
                    json[key] = new JsonObject { ["commit"] = entry.Commit, ["at"] = entry.At };
                Directory.CreateDirectory(configDirectory);
                // Written whole then moved into place, so another mbus never reads half a file.
                var temp = File + "." + Environment.ProcessId + ".tmp";
                System.IO.File.WriteAllText(temp, json.ToJsonString());
                System.IO.File.Move(temp, File, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private bool Fresh(Entry entry) => time.GetUtcNow() - entry.At < KeepFor;

    private static string Key(string workspaceId, string target, string rootCommit) => $"{workspaceId}|{target}|{rootCommit}";

    private Dictionary<string, Entry> Read()
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            if (!System.IO.File.Exists(File) || JsonNode.Parse(System.IO.File.ReadAllText(File)) is not JsonObject root) return entries;
            foreach (var (key, value) in root)
            {
                if (value is JsonObject o && o["commit"] is JsonValue c && c.TryGetValue<string>(out var commit)
                    && o["at"] is JsonValue a && a.TryGetValue<DateTimeOffset>(out var at))
                    entries[key] = new Entry(commit, at);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
        }
        return entries;
    }

    private sealed record Entry(string Commit, DateTimeOffset At);
}
