using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Mcp;

namespace MessageBus.Cli.Connect;

/// <summary>
/// OpenCode's settings file (<c>opencode.json</c> or <c>opencode.jsonc</c>, the original issue). OpenCode 2 keeps MCP servers under
/// <c>mcp.servers</c>; OpenCode 1 directly under <c>mcp</c>, which OpenCode 2 still reads. mbus writes the shape
/// the file already uses, the version 1 shape in a file that has neither, and leaves every other setting as it was.
/// </summary>
internal static class OpenCodeConfig
{
    /// <summary>The mbus entry, in either shape; null when there is none.</summary>
    public static JsonObject? Mbus(string path) => TryMcp(JsonConfigFile.TryRead(path)) is { } mcp
        ? (mcp["servers"] as JsonObject)?[McpServerName.Value] as JsonObject ?? mcp[McpServerName.Value] as JsonObject
        : null;

    /// <summary>Whether the file names the mbus MCP server.</summary>
    public static bool HasMbus(string path) => Mbus(path) is not null;

    /// <summary>Whether rewriting the file loses something a person wrote in it (JSONC comments).</summary>
    public static bool HasComments(string path)
    {
        try
        {
            return File.Exists(path) && JsonConfigFile.TryRead(path) is not null && !IsPlainJson(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Adds or replaces the mbus server. Refuses to touch a file that exists but can't be read as JSON.</summary>
    public static void SetMbus(string path, McpLaunchCommand command)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            root = JsonConfigFile.TryRead(path) ?? throw new CliException($"{path} isn't valid JSON, so mbus left it alone. Fix it and run mbus connect again.");
        }
        else
        {
            root = new JsonObject { ["$schema"] = "https://opencode.ai/config.json" };
        }
        if (root["mcp"] is not JsonObject mcp)
        {
            mcp = [];
            root["mcp"] = mcp;
        }
        var commandLine = new JsonArray([(JsonNode)command.Command, .. command.Args.Select(a => (JsonNode)a)]);
        if (mcp["servers"] is JsonObject servers)
        {
            mcp.Remove(McpServerName.Value);
            servers[McpServerName.Value] = new JsonObject { ["type"] = "local", ["command"] = commandLine };
        }
        else
        {
            mcp[McpServerName.Value] = new JsonObject { ["type"] = "local", ["command"] = commandLine, ["enabled"] = true };
        }
        FileReplacer.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static JsonObject? TryMcp(JsonObject? root) => root?["mcp"] as JsonObject;

    private static bool IsPlainJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
