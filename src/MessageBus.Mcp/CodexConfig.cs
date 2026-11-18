using System.Globalization;
using System.Text.RegularExpressions;
using MessageBus.Contracts.Mcp;

namespace MessageBus.Mcp;

/// <summary>
/// What the local server reads from Codex's <c>config.toml</c> (the original issue): the mbus server's <c>tool_timeout_sec</c>,
/// which <c>mbus connect</c> sets to 120 s. Codex ends a tool call after that long (60 s by default), so the local
/// server lets one call block for a little less.
/// </summary>
public static partial class CodexConfig
{
    /// <summary>
    /// How long one call may block for a Codex whose config is in <paramref name="codexHome"/>: its mbus
    /// <c>tool_timeout_sec</c> less a margin (120 s gives 110, like Claude Code; 60 s gives 55). Null when the
    /// config doesn't set one, or can't be read.
    /// </summary>
    public static int? CallLimitSeconds(string? codexHome)
    {
        if (codexHome is null) return null;
        try
        {
            var path = Path.Combine(codexHome, "config.toml");
            if (!File.Exists(path)) return null;
            var inMbus = false;
            foreach (var line in File.ReadLines(path))
            {
                if (TableHeader().IsMatch(line))
                {
                    inMbus = MbusHeader().IsMatch(line);
                    continue;
                }
                if (inMbus && ToolTimeout().Match(line) is { Success: true } m
                    && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 2)
                {
                    var timeout = (int)Math.Min(seconds, 24 * 60 * 60);
                    return timeout - (timeout > 60 ? 10 : 5) is var limit && limit > 0 ? limit : 1;
                }
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The longest one call may block for Codex: from its config, else <see cref="McpCallLimits.OtherClientSeconds"/>.</summary>
    internal static int CallLimitSecondsOrDefault(string? codexHome) => CallLimitSeconds(codexHome) ?? McpCallLimits.OtherClientSeconds;

    [GeneratedRegex("""^\s*\[""")]
    private static partial Regex TableHeader();

    [GeneratedRegex("""^\s*\[\s*mcp_servers\s*\.\s*(?:mbus|"mbus"|'mbus')\s*\]\s*(?:#.*)?$""")]
    private static partial Regex MbusHeader();

    [GeneratedRegex("""^\s*tool_timeout_sec\s*=\s*([0-9]+(?:\.[0-9]+)?)\s*(?:#.*)?$""")]
    private static partial Regex ToolTimeout();
}
