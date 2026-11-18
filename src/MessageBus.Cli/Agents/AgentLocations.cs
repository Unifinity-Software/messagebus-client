using MessageBus.Cli.Hosting;

namespace MessageBus.Cli.Agents;

/// <summary>
/// Where each agent keeps its settings, sign-in and skills, on Windows, macOS and Linux alike
/// (each agent uses the same layout under the home folder on all three).
/// </summary>
internal sealed class AgentLocations(CliHost host)
{
    private string Home => host.HomeDirectory;

    private string? Env(string name) => host.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    // ── Claude Code ─────────────────────────────────────────────────────────────
    // CLAUDE_CONFIG_DIR moves both the settings folder and .claude.json into that folder.

    /// <summary>Claude Code's settings folder (<c>~/.claude</c>).</summary>
    public string ClaudeDirectory => Env("CLAUDE_CONFIG_DIR") ?? Path.Combine(Home, ".claude");

    /// <summary>Claude Code's user settings file, which holds user-scope MCP servers and the signed-in account.</summary>
    public string ClaudeUserConfig => Env("CLAUDE_CONFIG_DIR") is { } dir ? Path.Combine(dir, ".claude.json") : Path.Combine(Home, ".claude.json");

    /// <summary>Claude Code's credentials file (Windows and Linux; macOS keeps them in the Keychain).</summary>
    public string ClaudeCredentials => Path.Combine(ClaudeDirectory, ".credentials.json");

    /// <summary>Claude Code's personal skills folder.</summary>
    public string ClaudeSkills => Path.Combine(ClaudeDirectory, "skills");

    // ── Codex ───────────────────────────────────────────────────────────────────

    /// <summary>Codex's home (<c>CODEX_HOME</c>, else <c>~/.codex</c>).</summary>
    public string CodexHome => Env("CODEX_HOME") ?? Path.Combine(Home, ".codex");

    /// <summary>Codex's settings, including <c>[mcp_servers.*]</c>.</summary>
    public string CodexConfig => Path.Combine(CodexHome, "config.toml");

    /// <summary>Codex's saved sign-in.</summary>
    public string CodexAuth => Path.Combine(CodexHome, "auth.json");

    /// <summary>Codex's personal skills folder.</summary>
    public string CodexSkills => Path.Combine(CodexHome, "skills");

    // ── GitHub Copilot CLI ──────────────────────────────────────────────────────

    /// <summary>Copilot CLI's settings folder.</summary>
    public string CopilotDirectory => Env("COPILOT_HOME") ?? Path.Combine(Home, ".copilot");

    /// <summary>Copilot CLI's MCP servers.</summary>
    public string CopilotMcpConfig => Path.Combine(CopilotDirectory, "mcp-config.json");

    /// <summary>Copilot CLI's settings, which list signed-in users.</summary>
    public string CopilotConfig => Path.Combine(CopilotDirectory, "config.json");

    /// <summary>Copilot CLI's personal skills folder.</summary>
    public string CopilotSkills => Path.Combine(CopilotDirectory, "skills");

    // ── OpenCode (XDG-style folders on every OS) ──────────────────────────────────────

    /// <summary>OpenCode's settings folder (<c>OPENCODE_CONFIG_DIR</c>, else <c>~/.config/opencode</c>).</summary>
    public string OpenCodeDirectory => Runner.Agents.OpenCodeAdapter.ConfigDirectory(Env, Home);

    /// <summary>OpenCode's settings, including <c>mcp</c> (<c>opencode.jsonc</c> when there is one, else <c>opencode.json</c>).</summary>
    public string OpenCodeConfig => Runner.Agents.OpenCodeAdapter.ConfigFile(OpenCodeDirectory);

    /// <summary>OpenCode's personal skills folder.</summary>
    public string OpenCodeSkills => Path.Combine(OpenCodeDirectory, "skills");

    /// <summary>OpenCode's saved provider sign-ins.</summary>
    public string OpenCodeAuth => Path.Combine(Env("XDG_DATA_HOME") ?? Path.Combine(Home, ".local", "share"), "opencode", "auth.json");
}
