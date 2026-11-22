using System.Text.Json.Nodes;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Mcp;
using MessageBus.Contracts.Machines;

namespace MessageBus.Cli.Connect;

/// <summary>One file <c>mbus connect</c> will write, shown before anything is written.</summary>
/// <param name="Agent">The agent it is for.</param>
/// <param name="Path">The file or folder.</param>
/// <param name="What">What changes, in a few words.</param>
/// <param name="Apply">Writes it.</param>
internal sealed record PlannedChange(AgentKind Agent, string Path, string What, Action Apply);

/// <summary>
/// What <c>mbus connect</c> writes for each agent it can set up: the MCP server entry that starts
/// <c>mbus mcp</c>, and the orchestrate skill. Agents mbus can't set up yet are left alone.
/// </summary>
internal static class AgentConnector
{
    /// <summary>Agents <c>mbus connect</c> can set up.</summary>
    public static readonly AgentKind[] Supported = [AgentKind.ClaudeCode, AgentKind.Codex, AgentKind.Copilot, AgentKind.OpenCode, AgentKind.Antigravity];

    public static IReadOnlyList<PlannedChange> Plan(CliHost host, AgentKind agent)
    {
        var locations = new AgentLocations(host);
        var launcher = Update.DotnetMcpLauncher.ForConnection(host);
        var command = launcher?.Launch ?? host.MbusCommand;
        var skill = host.Skills.GetFiles();
        var changes = new List<PlannedChange>();
        if (launcher is not null)
            changes.Add(new(agent, launcher.Directory, "install the mbus MCP launcher so updates can run during a session", launcher.Install));
        switch (agent)
        {
            case AgentKind.ClaudeCode:
                var claudeConfig = locations.ClaudeUserConfig;
                changes.Add(new PlannedChange(agent, claudeConfig,
                    JsonConfigFile.HasEntry(claudeConfig, "mcpServers") ? "update the mbus MCP server" : "add the mbus MCP server",
                    () => JsonConfigFile.SetEntry(claudeConfig, "mcpServers", ClaudeEntry(command))));
                AddSkill(changes, agent, locations.ClaudeSkills, host.Skills.Name, skill);
                break;
            case AgentKind.Codex:
                var codexConfig = locations.CodexConfig;
                changes.Add(new PlannedChange(agent, codexConfig,
                    CodexTomlConfig.HasMbus(codexConfig) ? "update [mcp_servers.mbus]" : "add [mcp_servers.mbus]",
                    () => CodexTomlConfig.SetMbus(codexConfig, command)));
                AddSkill(changes, agent, locations.CodexSkills, host.Skills.Name, skill);
                break;
            case AgentKind.Copilot:
                // the original issue: ~/.copilot/mcp-config.json, the file `copilot mcp add` writes.
                var copilotConfig = locations.CopilotMcpConfig;
                changes.Add(new PlannedChange(agent, copilotConfig,
                    JsonConfigFile.HasEntry(copilotConfig, "mcpServers") ? "update the mbus MCP server" : "add the mbus MCP server",
                    () => JsonConfigFile.SetEntry(copilotConfig, "mcpServers", CopilotEntry(command))));
                AddSkill(changes, agent, locations.CopilotSkills, host.Skills.Name, skill);
                break;
            case AgentKind.OpenCode:
                var openCodeConfig = locations.OpenCodeConfig;
                var what = OpenCodeConfig.HasMbus(openCodeConfig) ? "update the mbus MCP server" : "add the mbus MCP server";
                changes.Add(new PlannedChange(agent, openCodeConfig,
                    OpenCodeConfig.HasComments(openCodeConfig) ? what + " (the comments in this file are removed)" : what,
                    () => OpenCodeConfig.SetMbus(openCodeConfig, command)));
                AddSkill(changes, agent, locations.OpenCodeSkills, host.Skills.Name, skill);
                break;
            case AgentKind.Antigravity:
                var antigravityConfig = locations.AntigravityMcpConfig;
                changes.Add(new PlannedChange(agent, antigravityConfig,
                    JsonConfigFile.HasEntry(antigravityConfig, "mcpServers") ? "update the mbus MCP server" : "add the mbus MCP server",
                    () => JsonConfigFile.SetEntry(antigravityConfig, "mcpServers", AntigravityEntry(command))));
                AddSkill(changes, agent, locations.AntigravitySkills, host.Skills.Name, skill);
                break;
            default:
                throw new CliException($"mbus connect can't set up {AgentScanner.DisplayName(agent)} yet. Add the MCP server '{string.Join(' ', [command.Command, .. command.Args])}' to it by hand.");
        }
        return changes;
    }

    private static JsonObject AntigravityEntry(McpLaunchCommand command) => new()
    {
        ["command"] = command.Command,
        ["args"] = new JsonArray([.. command.Args.Select(a => (JsonNode?)JsonValue.Create(a))]),
        ["disabled"] = false,
    };

    private static JsonObject ClaudeEntry(McpLaunchCommand command) => new()
    {
        ["type"] = "stdio",
        ["command"] = command.Command,
        ["args"] = new JsonArray([.. command.Args.Select(a => (JsonNode?)JsonValue.Create(a))]),
        ["env"] = new JsonObject(),
    };

    /// <summary>Copilot CLI's entry: every tool, and two minutes per call (its 30 s default would cut a long mbus wait short).</summary>
    private static JsonObject CopilotEntry(McpLaunchCommand command) => new()
    {
        ["type"] = "local",
        ["command"] = command.Command,
        ["args"] = new JsonArray([.. command.Args.Select(a => (JsonNode?)JsonValue.Create(a))]),
        ["tools"] = new JsonArray("*"),
        ["timeout"] = Runner.Agents.CopilotAdapter.ToolTimeoutMilliseconds,
    };

    private static void AddSkill(List<PlannedChange> changes, AgentKind agent, string skillsRoot, string name, IReadOnlyList<SkillFile> files)
    {
        if (files.Count == 0)
        {
            return;
        }
        var folder = Path.Combine(skillsRoot, name);
        changes.Add(new PlannedChange(agent, folder, $"install the {name} skill ({files.Count} file{(files.Count == 1 ? "" : "s")})", () => WriteSkill(folder, files)));
    }

    private static void WriteSkill(string folder, IReadOnlyList<SkillFile> files)
    {
        // Replace the folder, so a file dropped from a newer skill doesn't linger.
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            var target = Path.GetFullPath(Path.Combine(folder, file.RelativePath));
            if (!target.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file.Content);
        }
    }
}
