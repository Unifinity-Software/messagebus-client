using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MessageBus.Cli.Mcp;

namespace MessageBus.Cli.Connect;

/// <summary>The MCP server name mbus registers in each agent.</summary>
internal static class McpServerName
{
    public const string Value = "mbus";
}

/// <summary>Reads and edits JSON settings files that agents also write (Claude Code, Copilot CLI, OpenCode).</summary>
internal static class JsonConfigFile
{
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The file's root object; null when the file is missing or isn't a JSON object.</summary>
    public static JsonObject? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether <c>root[section][mbus]</c> is an object.</summary>
    public static bool HasEntry(string path, string section) =>
        TryRead(path)?[section] is JsonObject servers && servers[McpServerName.Value] is JsonObject;

    /// <summary>
    /// Sets <c>root[section][mbus]</c> to <paramref name="entry"/>, keeping everything else in the file.
    /// Refuses to touch a file that exists but can't be parsed, rather than overwrite it.
    /// </summary>
    public static void SetEntry(string path, string section, JsonObject entry)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            var raw = File.ReadAllText(path);
            root = string.IsNullOrWhiteSpace(raw)
                ? []
                : TryRead(path) ?? throw new CliException($"{path} isn't valid JSON, so mbus left it alone. Fix it and run mbus connect again.");
        }
        else
        {
            root = [];
        }
        if (root[section] is not JsonObject servers)
        {
            servers = [];
            root[section] = servers;
        }
        servers[McpServerName.Value] = entry;
        FileReplacer.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }
}

/// <summary>
/// Codex's <c>config.toml</c>: mbus adds or replaces only its own <c>[mcp_servers.mbus]</c> table and
/// leaves every other line as it was (no TOML library rewrites the rest of the file).
/// </summary>
internal static partial class CodexTomlConfig
{
    [GeneratedRegex("""^\s*\[\s*mcp_servers\s*\.\s*(?:mbus|"mbus"|'mbus')\s*(?:\.[^\]]*)?\]\s*(?:#.*)?$""")]
    private static partial Regex MbusTableHeader();

    [GeneratedRegex("""^\s*\[\s*mcp_servers\s*\.\s*(?:mbus|"mbus"|'mbus')\s*\]\s*(?:#.*)?$""")]
    private static partial Regex MbusMainHeader();

    [GeneratedRegex("""^\s*\[""")]
    private static partial Regex AnyTableHeader();

    /// <summary>How long Codex lets one mbus tool call take (its default, 60 s, is shorter than a long <c>wait</c>).</summary>
    public const int ToolTimeoutSeconds = 120;

    /// <summary>Whether the file has a <c>[mcp_servers.mbus]</c> table.</summary>
    public static bool HasMbus(string path)
    {
        try
        {
            return File.Exists(path) && File.ReadLines(path).Any(line => MbusMainHeader().IsMatch(line));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Writes the mbus table, replacing an earlier one (and its sub-tables).</summary>
    public static void SetMbus(string path, McpLaunchCommand command)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var kept = new List<string>();
        var skipping = false;
        foreach (var line in text.Split('\n'))
        {
            var bare = line.TrimEnd('\r');
            if (AnyTableHeader().IsMatch(bare))
            {
                skipping = MbusTableHeader().IsMatch(bare);
            }
            if (!skipping)
            {
                kept.Add(bare);
            }
        }
        while (kept.Count > 0 && kept[^1].Trim().Length == 0)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        var result = new StringBuilder();
        foreach (var line in kept)
        {
            result.Append(line).Append(newline);
        }
        if (kept.Count > 0)
        {
            result.Append(newline);
        }
        result.Append("[mcp_servers.").Append(McpServerName.Value).Append(']').Append(newline);
        result.Append("command = ").Append(Quote(command.Command)).Append(newline);
        result.Append("args = [").Append(string.Join(", ", command.Args.Select(Quote))).Append(']').Append(newline);
        // Codex gives up on an MCP call after 60 s by default; mbus's wait tool may take longer.
        result.Append("tool_timeout_sec = ").Append(ToolTimeoutSeconds).Append(newline);
        FileReplacer.WriteAllText(path, result.ToString());
    }

    /// <summary>A TOML basic string.</summary>
    internal static string Quote(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append($"\\u{(int)c:X4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}

/// <summary>Replaces a file in one step, keeping its Unix permissions.</summary>
internal static class FileReplacer
{
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var temp = $"{path}.mbus-{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
        }
        File.Move(temp, path, overwrite: true);
    }
}
