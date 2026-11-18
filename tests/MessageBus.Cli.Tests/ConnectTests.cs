using System.Text.Json.Nodes;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests;

/// <summary>Agent detection (<c>mbus connect --rescan</c>) and setup (<c>mbus connect</c>) with fake agent commands.</summary>
public sealed class ConnectTests
{
    [Fact]
    public async Task Rescan_reports_found_missing_unconfigured_and_signed_out_agents()
    {
        using var machine = new TestMachine();
        machine.InstallFake("git", new("git version 2.50.1"));
        machine.InstallAgent("claude", "2.1.3 (Claude Code)");
        machine.InstallAgent("codex", "codex-cli 0.50.0");
        // Claude Code: signed in, with mbus as a user MCP server. Codex: no mbus entry, no sign-in.
        machine.WriteHomeFile(".claude.json", """{ "oauthAccount": { "emailAddress": "a@contoso.com" }, "mcpServers": { "mbus": { "command": "mbus", "args": ["mcp"] } } }""");
        machine.WriteHomeFile(Path.Combine(".codex", "config.toml"), "model = \"gpt-5\"\n[mcp_servers.other]\ncommand = \"other\"\n");
        // the original issue: Claude Code's default is an alias its cached catalog resolves; this Codex has no 'debug models', so its cached catalog counts.
        machine.WriteHomeFile(Path.Combine(".claude", "settings.json"), """{ "model": "opus" }""");
        machine.WriteHomeFile(Path.Combine(".claude", "cache", "model-catalog", "a-cc.json"),
            """{"fetchedAt":1,"catalog":{"surface":"cc","config":{"models":[{"id":"claude-opus-5-5"},{"id":"claude-fable-5-2"}],"provider_alias_targets":{"opus":{"default":"claude-opus-5-5"}}}}}""");
        machine.WriteHomeFile(Path.Combine(".codex", "models_cache.json"), """{"models":[{"slug":"gpt-6.1-sol","visibility":"list","priority":1}]}""");

        var agents = await ScanAsync(machine);

        Assert.Equal(new DetectedAgent
        {
            Kind = AgentKind.ClaudeCode, Found = true, Version = "2.1.3", OnPath = true, McpConfigured = true, SignedIn = true,
            ExecutablePath = agents[AgentKind.ClaudeCode].ExecutablePath, Vendor = "anthropic", DefaultModel = "claude-opus-5-5",
        }, agents[AgentKind.ClaudeCode] with { Models = [] });
        Assert.Equal(["claude-opus-5-5", "claude-fable-5-2"], agents[AgentKind.ClaudeCode].Models);
        Assert.Equal(("gpt-5", false), (agents[AgentKind.Codex].DefaultModel, agents[AgentKind.Codex].ModelsPartial));
        Assert.Equal(["gpt-5", "gpt-6.1-sol"], agents[AgentKind.Codex].Models);
        Assert.Equal((true, "0.50.0", true, false, false),
            (agents[AgentKind.Codex].Found, agents[AgentKind.Codex].Version, agents[AgentKind.Codex].OnPath, agents[AgentKind.Codex].McpConfigured, agents[AgentKind.Codex].SignedIn));
        Assert.False(agents[AgentKind.Copilot].Found);
        Assert.False(agents[AgentKind.OpenCode].Found);
    }

    [Fact]
    public async Task Connect_from_the_dashboard_changes_nothing_until_someone_at_the_machine_says_yes()
    {
        using var machine = new TestMachine();
        machine.InstallAgent("claude", "2.1.3 (Claude Code)");
        var claudeJson = machine.WriteHomeFile(".claude.json", """{ "theme": "dark" }""");
        var rescans = 0;
        var ct = TestContext.Current.CancellationToken;
        AgentConnectRequests Requests(string typed, bool terminal) => new(machine.CreateHost(),
            new TerminalConfirmer(new StringReader(typed), TextWriter.Null, terminal, machine.Time), TextWriter.Null, _ =>
            {
                rescans++;
                return Task.CompletedTask;
            });
        bool Connected() => JsonNode.Parse(File.ReadAllText(claudeJson))!["mcpServers"]?["mbus"] is not null;
        var ask = new ConnectAgentRequest { Agent = AgentKind.ClaudeCode, RequestedBy = "Ada Lovelace" };

        // A runner with no terminal (a service, a container) can't ask, so it doesn't change anything.
        Assert.Equal(ConnectAgentStatus.NeedsTerminal, (await Requests("y\n", terminal: false).RequestAsync(ask, ct)).Status);
        Assert.Equal(ConnectAgentStatus.NotInstalled, (await Requests("y\n", true).RequestAsync(ask with { Agent = AgentKind.Codex }, ct)).Status);

        var refused = Requests("n\n", true);
        Assert.Equal(ConnectAgentStatus.Asked, (await refused.RequestAsync(ask, ct)).Status);
        await refused.Pending;
        Assert.False(Connected());

        var allowed = Requests("y\n", true);
        Assert.Equal(ConnectAgentStatus.Asked, (await allowed.RequestAsync(ask, ct)).Status);
        await allowed.Pending;
        Assert.True(Connected());
        Assert.Equal("dark", (string?)JsonNode.Parse(File.ReadAllText(claudeJson))!["theme"]);
        Assert.Equal(1, rescans); // the dashboard hears it is connected
    }

    [Fact]
    public async Task Connect_adds_mbus_to_each_agent_and_keeps_their_other_settings()
    {
        using var machine = new TestMachine();
        machine.InstallAgent("claude", "2.1.3 (Claude Code)");
        machine.InstallAgent("codex", "codex-cli 0.50.0");
        machine.InstallAgent("copilot", "GitHub Copilot CLI 1.0.112.");
        machine.InstallAgent("opencode", "2.3.0");
        machine.SkillFiles["SKILL.md"] = "---\nname: mbus-orchestrate\n---\n";
        machine.SkillFiles["reference/patterns.md"] = "# Patterns\n";
        var claudeJson = machine.WriteHomeFile(".claude.json", """{ "theme": "dark", "mcpServers": { "other": { "command": "other" } } }""");
        var codexToml = machine.WriteHomeFile(Path.Combine(".codex", "config.toml"),
            "model = \"gpt-5\"\n\n[mcp_servers.mbus]\ncommand = \"old-mbus\"\n\n[mcp_servers.mbus.env]\nX = \"1\"\n\n[mcp_servers.other]\ncommand = \"other\"\n");

        var copilotJson = machine.WriteHomeFile(Path.Combine(".copilot", "mcp-config.json"), """{ "mcpServers": { "playwright": { "type": "local", "command": "npx", "args": [], "tools": ["*"] } } }""");
        // OpenCode 2's own shape (mcp.servers), with a comment, as in an opencode.jsonc it wrote.
        var openCodeJsonc = machine.WriteHomeFile(Path.Combine(".config", "opencode", "opencode.jsonc"), """
            {
              // mine
              "model": "anthropic/claude-sonnet-5-5",
              "mcp": { "servers": { "context7": { "type": "remote", "url": "https://mcp.context7.com/mcp" } } },
            }
            """);

        // Twice: a second connect replaces mbus's entries rather than adding more.
        Assert.Equal(0, (await machine.RunAsync("connect", "--yes")).ExitCode);
        Assert.Equal(0, (await machine.RunAsync("connect", "--yes")).ExitCode);

        var claude = JsonNode.Parse(File.ReadAllText(claudeJson))!;
        Assert.Equal("dark", (string?)claude["theme"]);
        Assert.Equal("other", (string?)claude["mcpServers"]!["other"]!["command"]);
        Assert.Equal(machine.MbusCommand.Command, (string?)claude["mcpServers"]!["mbus"]!["command"]);
        Assert.Equal(["mcp"], claude["mcpServers"]!["mbus"]!["args"]!.AsArray().Select(a => (string?)a));

        var codexLines = File.ReadAllLines(codexToml);
        Assert.Contains("model = \"gpt-5\"", codexLines);
        Assert.Contains("[mcp_servers.other]", codexLines);
        Assert.Single(codexLines, l => l == "[mcp_servers.mbus]");
        Assert.DoesNotContain(codexLines, l => l.Contains("old-mbus") || l.Contains("mcp_servers.mbus.env"));
        Assert.Contains($"command = \"{machine.MbusCommand.Command.Replace("\\", "\\\\")}\"", codexLines);
        Assert.Contains("args = [\"mcp\"]", codexLines);
        Assert.Contains("tool_timeout_sec = 120", codexLines); // Codex's own 60 s would cut a long mbus wait short
        // ...and the local mbus server reads it back, so one call may block for 110 s under Codex, not 55 (the original issue).
        Assert.Equal(110, MessageBus.Mcp.CodexConfig.CallLimitSeconds(Path.Combine(machine.Home, ".codex")));

        // the original issue: Copilot CLI and OpenCode too, each in the shape it reads.
        var copilot = JsonNode.Parse(File.ReadAllText(copilotJson))!["mcpServers"]!;
        Assert.Equal("npx", (string?)copilot["playwright"]!["command"]);
        Assert.Equal(machine.MbusCommand.Command, (string?)copilot["mbus"]!["command"]);
        Assert.Equal(["*"], copilot["mbus"]!["tools"]!.AsArray().Select(a => (string?)a));
        var openCode = JsonNode.Parse(File.ReadAllText(openCodeJsonc))!;
        Assert.Equal("anthropic/claude-sonnet-5-5", (string?)openCode["model"]);
        Assert.Equal("https://mcp.context7.com/mcp", (string?)openCode["mcp"]!["servers"]!["context7"]!["url"]);
        Assert.Equal([machine.MbusCommand.Command, "mcp"], openCode["mcp"]!["servers"]!["mbus"]!["command"]!.AsArray().Select(a => (string?)a));

        foreach (var skills in new[] { ".claude", ".codex", ".copilot", Path.Combine(".config", "opencode") }.Select(d => Path.Combine(machine.Home, d, "skills")))
        {
            Assert.True(File.Exists(Path.Combine(skills, "mbus-orchestrate", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(skills, "mbus-orchestrate", "reference", "patterns.md")));
        }

        var agents = await ScanAsync(machine);
        Assert.True(agents[AgentKind.ClaudeCode].McpConfigured);
        Assert.True(agents[AgentKind.Codex].McpConfigured);
        Assert.True(agents[AgentKind.Copilot].McpConfigured);
        Assert.True(agents[AgentKind.OpenCode].McpConfigured);
    }

    [Fact]
    public async Task Connect_changes_nothing_unless_the_person_agrees()
    {
        using var machine = new TestMachine();
        machine.InstallAgent("claude", "2.1.3 (Claude Code)");
        machine.Input = "n\n";

        var run = await machine.RunAsync("connect");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(Path.Combine(machine.Home, ".claude.json"), run.Output);
        Assert.False(File.Exists(Path.Combine(machine.Home, ".claude.json")));
    }

    private static async Task<Dictionary<AgentKind, DetectedAgent>> ScanAsync(TestMachine machine)
    {
        var run = await machine.RunAsync("connect", "--rescan", "--json");
        Assert.Equal(0, run.ExitCode);
        var inventory = MessageBusJson.Deserialize<MachineInventory>(run.Output)!;
        return inventory.Agents.ToDictionary(a => a.Kind);
    }
}
