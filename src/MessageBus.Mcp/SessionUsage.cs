using System.Text.Json;
using System.Text.RegularExpressions;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;

namespace MessageBus.Mcp;

/// <summary>
/// The orchestrating session's own usage, for <c>complete_project</c> (the original issue, the original issue). No MCP client tells its
/// servers how many tokens the session used, but Claude Code and Codex keep a log of each session on this
/// machine. Claude Code: <c>~/.claude/projects/&lt;working directory&gt;/&lt;session&gt;.jsonl</c>, one line per
/// message, each model answer with its <c>usage</c>. Codex: <c>~/.codex/sessions/yyyy/MM/dd/rollout-….jsonl</c>,
/// whose first line names the session's working directory and whose <c>token_count</c> events carry running
/// totals. The newest log for the server's working directory is the session that started it. Only the counts
/// (and the model) are read. Callers subtract their persisted checkpoint from these cumulative totals.
/// </summary>
internal static partial class SessionUsage
{
    /// <summary>Claude Code's settings folder: <c>CLAUDE_CONFIG_DIR</c>, else <c>~/.claude</c>.</summary>
    public static string ClaudeHome(Func<string, string?> environment) =>
        environment("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>The usage of Claude Code's newest session in <paramref name="workingDirectory"/> (cumulative), or null.</summary>
    public static Usage? FromClaudeCode(string claudeHome, string workingDirectory, string? sessionId = null, DateTimeOffset? before = null, DateTimeOffset? after = null)
    {
        var log = sessionId is null ? ClaudeLog(claudeHome, workingDirectory)
            : ClaudeFolder(claudeHome, workingDirectory) is { } folder ? new FileInfo(Path.Combine(folder.FullName, sessionId + ".jsonl")) : null;
        return log is null ? null : Read(log.FullName, after ?? DateTimeOffset.MinValue, before);
    }

    /// <summary>Claude Code's folder of session logs for <paramref name="workingDirectory"/> (it may not exist).</summary>
    internal static DirectoryInfo? ClaudeFolder(string claudeHome, string workingDirectory)
    {
        try
        {
            // Claude Code names a project's folder after its absolute path, every other character a dash.
            var path = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return new DirectoryInfo(Path.Combine(claudeHome, "projects", NotAlphanumeric().Replace(path, "-")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The newest Claude Code session log for <paramref name="workingDirectory"/> (named after its session id), or null.</summary>
    internal static FileInfo? ClaudeLog(string claudeHome, string workingDirectory)
    {
        try
        {
            var folder = ClaudeFolder(claudeHome, workingDirectory);
            return folder is { Exists: true } ? folder.EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Usage? Read(string file, DateTimeOffset since, DateTimeOffset? before = null)
    {
        // A streamed answer is logged once per content block with the same message id and usage: count each id once.
        var answers = new Dictionary<string, (long In, long Cached, long Written, long WrittenHour, long Out, string? Model, DateTimeOffset At)>(StringComparer.Ordinal);
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (Text(root, "type") != "assistant" || !root.TryGetProperty("message", out var message)
                    || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
                if (!DateTimeOffset.TryParse(Text(root, "timestamp"), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var at) || at < since || at >= before) continue;
                var model = Text(message, "model");
                if (model == "<synthetic>") continue; // the CLI's own notices, not model answers
                if ((Text(message, "id") ?? Text(root, "uuid")) is not { } id) continue;
                // Cache writes cost more than input, an hour's write more than five minutes' (the original issue).
                var written = Number(usage, "cache_creation_input_tokens");
                var hour = usage.TryGetProperty("cache_creation", out var tiers) && tiers.ValueKind == JsonValueKind.Object
                    ? Math.Min(Number(tiers, "ephemeral_1h_input_tokens"), written) : 0;
                answers[id] = (Number(usage, "input_tokens") + written + Number(usage, "cache_read_input_tokens"),
                    Number(usage, "cache_read_input_tokens"), written, hour, Number(usage, "output_tokens"), model, at);
            }
            catch (JsonException)
            {
                // a line still being written
            }
        }
        if (answers.Count == 0) return null;
        var values = answers.Values.ToList();
        return new Usage
        {
            AgentKind = AgentKind.ClaudeCode,
            Vendor = "anthropic",
            Model = values.Where(v => v.Model is not null).GroupBy(v => v.Model).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(),
            Models = values.GroupBy(v => v.Model).Select(g => new Usage
            {
                Model = g.Key, TokensIn = g.Sum(v => v.In), TokensOut = g.Sum(v => v.Out),
                CachedTokensIn = g.Sum(v => v.Cached), CacheWriteTokensIn = g.Sum(v => v.Written),
                CacheWrite1hTokensIn = g.Sum(v => v.WrittenHour),
            }).ToList(),
            TokensIn = values.Sum(v => v.In),
            CachedTokensIn = values.Sum(v => v.Cached),
            CacheWriteTokensIn = values.Sum(v => v.Written),
            CacheWrite1hTokensIn = values.Sum(v => v.WrittenHour),
            TokensOut = values.Sum(v => v.Out),
            ActiveSeconds = Math.Round((values.Max(v => v.At) - values.Min(v => v.At)).TotalSeconds, 1),
        };
    }

    /// <summary>Codex's home folder: <c>CODEX_HOME</c>, else <c>~/.codex</c>.</summary>
    public static string CodexHome(Func<string, string?> environment) =>
        environment("CODEX_HOME") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>
    /// The cumulative usage of Codex's session in <paramref name="workingDirectory"/>, or null:
    /// the session's running total at its last <c>token_count</c>.
    /// </summary>
    public static Usage? FromCodex(string codexHome, string workingDirectory, DateTimeOffset since, DateTimeOffset now, string? sessionId = null, DateTimeOffset? before = null, DateTimeOffset? after = null)
    {
        var log = sessionId is null ? CodexLog(codexHome, workingDirectory, since, now)
            : CodexLogs(codexHome, now.AddDays(-30), now, includeUnchanged: true).FirstOrDefault(f => f.Name.EndsWith("-" + sessionId + ".jsonl", StringComparison.OrdinalIgnoreCase));
        return log is null ? null : ReadCodex(log.FullName, after ?? DateTimeOffset.MinValue, before);
    }

    /// <summary>Codex's session logs written since <paramref name="since"/> (filed under the local date each session started).</summary>
    internal static List<FileInfo> CodexLogs(string codexHome, DateTimeOffset since, DateTimeOffset now, bool includeUnchanged = false)
    {
        var logs = new List<FileInfo>();
        try
        {
            var sessions = new DirectoryInfo(Path.Combine(codexHome, "sessions"));
            if (!sessions.Exists) return logs;
            // From the day before `since` to today.
            var first = since.ToLocalTime().Date.AddDays(-1);
            var days = Math.Clamp((int)(now.ToLocalTime().Date - first).TotalDays, 0, 31);
            for (var d = 0; d <= days; d++)
            {
                var day = new DirectoryInfo(Path.Combine(sessions.FullName,
                    first.AddDays(d).ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture)));
                if (day.Exists) logs.AddRange(day.EnumerateFiles("rollout-*.jsonl").Where(f => includeUnchanged || f.LastWriteTimeUtc >= since.UtcDateTime));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return logs;
    }

    /// <summary>The newest Codex session log whose session works in <paramref name="workingDirectory"/>, or null.</summary>
    internal static FileInfo? CodexLog(string codexHome, string workingDirectory, DateTimeOffset since, DateTimeOffset now)
    {
        try
        {
            var directory = Comparable(workingDirectory);
            return CodexLogs(codexHome, since, now).OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault(log => SessionMeta(log.FullName, "cwd") is { } cwd && string.Equals(Comparable(cwd), directory, PathComparison));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The session id in a Codex log's first line (<c>session_meta</c>'s <c>id</c>), or null.</summary>
    internal static string? CodexSessionId(string file)
    {
        try
        {
            return SessionMeta(file, "id");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string Comparable(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>A field of the log's first line (<c>session_meta</c>): its <c>cwd</c> or <c>id</c>.</summary>
    private static string? SessionMeta(string file, string field)
    {
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        if (reader.ReadLine() is not { } line) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return Text(doc.RootElement, "type") == "session_meta" && doc.RootElement.TryGetProperty("payload", out var meta) ? Text(meta, field) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Usage? ReadCodex(string file, DateTimeOffset since, DateTimeOffset? until = null)
    {
        (long In, long Cached, long Out) before = default, last = default;
        DateTimeOffset? from = null, to = null;
        var models = new List<string>();
        var mix = new Dictionary<string, Usage>();
        string currentModel = "";
        (long In, long Cached, long Out) prior = default;
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("\"token_count\"", StringComparison.Ordinal) && !line.Contains("\"turn_context\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                    || !DateTimeOffset.TryParse(Text(root, "timestamp"), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) continue;
                if (at >= until) continue;
                if (Text(root, "type") == "turn_context")
                {
                    if (Text(payload, "model") is { Length: > 0 } model) { currentModel = model; if (at >= since) models.Add(model); }
                    continue;
                }
                if (Text(payload, "type") != "token_count" || !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object
                    || !info.TryGetProperty("total_token_usage", out var total) || total.ValueKind != JsonValueKind.Object) continue;
                // Running totals; input_tokens includes cached_input_tokens, as in codex exec's own report.
                var counts = (Number(total, "input_tokens"), Number(total, "cached_input_tokens"), Number(total, "output_tokens"));
                if (at < since)
                {
                    before = counts;
                    prior = counts;
                    continue;
                }
                var part = mix.GetValueOrDefault(currentModel) ?? new Usage { Model = currentModel };
                mix[currentModel] = part with
                {
                    TokensIn = part.TokensIn + Math.Max(0, counts.Item1 - prior.In),
                    CachedTokensIn = part.CachedTokensIn + Math.Max(0, counts.Item2 - prior.Cached),
                    TokensOut = part.TokensOut + Math.Max(0, counts.Item3 - prior.Out),
                };
                prior = counts;
                last = counts;
                from ??= at;
                to = at;
            }
            catch (JsonException)
            {
                // a line still being written
            }
        }
        if (from is null || (last.In <= before.In && last.Out <= before.Out)) return null;
        return new Usage
        {
            Models = mix.Values.ToList(),
            AgentKind = AgentKind.Codex,
            Vendor = "openai",
            Model = models.GroupBy(m => m).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(),
            TokensIn = last.In - before.In,
            CachedTokensIn = Math.Max(0, last.Cached - before.Cached),
            TokensOut = last.Out - before.Out,
            ActiveSeconds = Math.Round((to!.Value - from.Value).TotalSeconds, 1),
        };
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NotAlphanumeric();
}
