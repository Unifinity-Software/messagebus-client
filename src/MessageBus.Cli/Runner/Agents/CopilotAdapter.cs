using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;

namespace MessageBus.Cli.Runner.Agents;

/// <summary>
/// GitHub Copilot CLI, headless (the original issue; flags checked against Copilot CLI 1.0.112's command reference):
/// <c>copilot -p "…" --output-format json --no-ask-user</c> with the profile as <c>--allow-tool</c> and
/// <c>--deny-tool</c> rules (<c>--allow-all-tools</c> only for a role that may run commands). Copilot takes the prompt as an argument, so it gets a short one pointing at the saved
/// prompt file, whose folder <c>--add-dir</c> lets it read.
/// <para>
/// Copilot starts MCP servers with little of its own environment, so the run's mbus server, with the run's
/// MBUS_* variables, is given in a file next to the prompt with <c>--additional-mcp-config @file</c> (it
/// replaces a server of the same name from <c>mbus connect</c>). Like the other adapters (the original issue) the run leaves
/// out the person's other MCP servers (<c>--disable-mcp-server</c>) and Copilot's built-in ones
/// (<c>--disable-builtin-mcps</c>: the GitHub server could merge a pull request past the deny rules).
/// </para>
/// </summary>
/// <param name="mbus">How Copilot starts the local mbus MCP server for a run; this mbus by default.</param>
/// <param name="copilotHome">Copilot's settings folder (<c>COPILOT_HOME</c>, else <c>~/.copilot</c>), whose MCP servers a run leaves out.</param>
public sealed class CopilotAdapter(Mcp.McpLaunchCommand? mbus = null, string? copilotHome = null) : IAgentAdapter
{
    /// <summary>The run's MCP settings, written next to the prompt (JSON can't pass through an npm <c>.cmd</c> shim as an argument).</summary>
    public const string McpConfigFileName = "copilot-mcp.json";

    /// <summary>How long Copilot lets one mbus tool call take, in milliseconds (its default, 30 s, is shorter than a long <c>wait</c>).</summary>
    public const int ToolTimeoutMilliseconds = 120_000;

    /// <inheritdoc />
    public AgentKind Kind => AgentKind.Copilot;

    /// <inheritdoc />
    public bool ReportsUsageAsItGoes => true;

    /// <inheritdoc />
    public AgentLaunch BuildLaunch(AgentLaunchContext context)
    {
        var profile = context.Profile;
        List<string> args =
        [
            "-p", $"Read the task in the file {context.PromptFile} and do it.",
            "--output-format", "json",
            "--no-ask-user",
            "--add-dir", Path.GetDirectoryName(context.PromptFile)!,
            "--disable-builtin-mcps",
            // Deny rules beat every allow rule. A reviewer comments; only a person approves or merges.
            "--deny-tool=shell(gh pr merge)",
            "--deny-tool=shell(gh pr review)",
        ];
        if (!profile.CanPush)
        {
            args.Add("--deny-tool=shell(git push)");
        }
        if (profile.CanRunCommands)
        {
            args.Add("--allow-all-tools");
        }
        else
        {
            // Only what's allowed by name; in -p mode Copilot refuses anything else that needs permission. The profile's
            // commands (Claude Code's Bash(git log:*) rules, the original issue and the original issue) are Copilot's shell(git log:*) rules.
            var commands = profile.AllowedTools
                .Where(t => t.StartsWith("Bash(", StringComparison.Ordinal) && t.EndsWith(')'))
                .Select(t => "--allow-tool=shell(" + t["Bash(".Length..^1] + ")")
                .ToList();
            args.AddRange(["--allow-tool=read", "--allow-tool=" + Connect.McpServerName.Value]);
            if (profile.CanEditFiles)
            {
                args.Add("--allow-tool=write");
            }
            args.AddRange(commands);
            if (commands.Count == 0)
            {
                args.Add("--deny-tool=shell");
            }
        }
        if (!profile.CanEditFiles)
        {
            args.Add("--deny-tool=write");
        }
        // Copilot asks before fetching a URL, and in -p mode nobody can answer, so the profile's network setting
        // becomes its URL rules (its web fetches and the URLs it sees in commands; not other traffic, see the original issue).
        if (profile.Network == NetworkAccess.Open)
        {
            args.Add("--allow-all-urls");
        }
        else if (profile.Network == NetworkAccess.Allowlist)
        {
            args.AddRange(profile.AllowedHosts.Where(h => h.Length > 0).Select(h => "--allow-url=" + h));
        }
        foreach (var other in OtherMcpServers(Path.Combine(copilotHome ?? DefaultHome(), "mcp-config.json")))
        {
            args.Add("--disable-mcp-server=" + other);
        }
        if (context.McpEnvironment is { } mcpEnvironment)
        {
            args.AddRange(["--additional-mcp-config", "@" + WriteMcpConfig(context.PromptFile, mcpEnvironment)]);
        }
        if (context.Model is { Length: > 0 } model)
        {
            args.Add("--model=" + model); // the role's cheaper model (the original issue)
        }
        return new AgentLaunch(args, null);
    }

    /// <inheritdoc />
    public IAgentOutputParser CreateParser() => new CopilotOutputParser();

    /// <summary><c>COPILOT_HOME</c>, else <c>~/.copilot</c>.</summary>
    public static string DefaultHome() =>
        Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

    /// <summary>The servers in Copilot's <c>mcp-config.json</c> other than mbus.</summary>
    public static IReadOnlyList<string> OtherMcpServers(string configFile) =>
        Connect.JsonConfigFile.TryRead(configFile)?["mcpServers"] is JsonObject servers
            ? [.. servers.Select(s => s.Key).Where(n => n != Connect.McpServerName.Value && n.Length > 0)]
            : [];

    private string WriteMcpConfig(string promptFile, IReadOnlyDictionary<string, string> mcpEnvironment)
    {
        var command = mbus ?? Mcp.McpLaunchCommand.ForCurrentProcess();
        var env = new JsonObject();
        foreach (var (name, value) in mcpEnvironment.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            env[name] = value;
        }
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [Connect.McpServerName.Value] = new JsonObject
                {
                    ["type"] = "local",
                    ["command"] = command.Command,
                    ["args"] = new JsonArray([.. command.Args.Select(a => (JsonNode)a)]),
                    ["env"] = env,
                    ["tools"] = new JsonArray("*"),
                    ["timeout"] = ToolTimeoutMilliseconds,
                },
            },
        };
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(promptFile))!, McpConfigFileName);
        File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}

/// <summary>
/// Reads <c>copilot -p --output-format json</c> output: one session event per line, <c>{"type", "data", "id",
/// "timestamp"}</c>, the same events Copilot keeps in <c>~/.copilot/session-state/&lt;id&gt;/events.jsonl</c>.
/// <list type="bullet">
/// <item><c>assistant.message</c> (<c>data.content</c>, <c>data.model</c>): the agent's messages; the last is the final text.</item>
/// <item><c>tool.execution_start</c> (<c>data.toolName</c>, <c>data.arguments</c>): progress.</item>
/// <item>Usage, first found of: <c>session.shutdown</c>'s <c>data.modelMetrics</c> (per-model totals for the whole run),
/// the sum of <c>assistant.usage</c> (one per model call, sent live), the sum of <c>session.usage_record</c>
/// (<c>data.usage</c>, the same counts as kept in the session log). In each, <c>inputTokens</c> already includes
/// <c>cacheReadTokens</c> and <c>cacheWriteTokens</c>. Copilot's <c>cost</c> counts premium requests, not dollars,
/// so no cost is reported.</item>
/// <item><c>session.error</c> (<c>data.message</c>): the run failed.</item>
/// </list>
/// A line that isn't JSON (an older Copilot printing text) counts as the final text.
/// </summary>
public sealed class CopilotOutputParser : IAgentOutputParser
{
    private readonly Totals _calls = new();
    private readonly Totals _records = new();
    private Totals? _shutdown;
    private string? _model;
    private string? _lastMessage;
    private string? _lastText;
    private string? _error;
    private bool _failed;

    /// <inheritdoc />
    public string? Observe(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            _lastText = line.Trim();
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            switch (ClaudeOutputParser.Str(root, "type"))
            {
                case "assistant.message":
                    _model = ClaudeOutputParser.Str(data, "model") ?? _model;
                    if (ClaudeOutputParser.Str(data, "content") is { Length: > 0 } content && content.Trim().Length > 0)
                    {
                        _lastMessage = content;
                        return AgentText.OneLine(content);
                    }
                    return null;
                case "tool.execution_start":
                    return ToolNote(data);
                case "assistant.usage":
                    _calls.Add(data);
                    return null;
                case "session.usage_record" when OpenCodeOutputParser.Prop(data, "usage") is { ValueKind: JsonValueKind.Object } usage:
                    _records.Add(usage);
                    return null;
                case "session.model_change":
                    _model = ClaudeOutputParser.Str(data, "newModel") ?? _model;
                    return null;
                case "session.shutdown" when data.TryGetProperty("modelMetrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object:
                    _shutdown = new Totals();
                    foreach (var model in metrics.EnumerateObject())
                    {
                        if (OpenCodeOutputParser.Prop(model.Value, "usage") is { ValueKind: JsonValueKind.Object } modelUsage)
                        {
                            _shutdown.Add(modelUsage, model.Name);
                        }
                    }
                    return null;
                case "session.error":
                    _failed = true;
                    _error = ClaudeOutputParser.Str(data, "message") ?? ClaudeOutputParser.Str(data, "errorType") ?? "Copilot reported an error";
                    return null;
                default:
                    return null;
            }
        }
    }

    private static string? ToolNote(JsonElement data)
    {
        var tool = ClaudeOutputParser.Str(data, "toolName");
        if (tool is null)
        {
            return null;
        }
        if (data.TryGetProperty("arguments", out var arguments) && ClaudeOutputParser.Str(arguments, "command") is { Length: > 0 } command)
        {
            return AgentText.OneLine("Running " + command);
        }
        return AgentText.OneLine(data.TryGetProperty("toolTitle", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : tool);
    }

    /// <inheritdoc />
    public Usage? UsageSoFar
    {
        get
        {
            // The model calls as they ended; the shutdown's totals come from Complete().
            var totals = _calls.Seen ? _calls : _records.Seen ? _records : null;
            return totals is null ? null : new Usage
            {
                AgentKind = AgentKind.Copilot, Vendor = "github", Model = totals.MainModel ?? _model,
                TokensIn = totals.In, CachedTokensIn = totals.CacheRead, CacheWriteTokensIn = totals.CacheWrite, TokensOut = totals.Out,
            };
        }
    }

    /// <inheritdoc />
    public AgentOutcome Complete()
    {
        var totals = _shutdown is { Seen: true } ? _shutdown : _calls.Seen ? _calls : _records;
        return new AgentOutcome
        {
            FinalText = _lastMessage ?? _lastText,
            ReportedError = _failed,
            ErrorText = _error,
            Usage = new Usage
            {
                AgentKind = AgentKind.Copilot,
                Vendor = "github",
                Model = totals.MainModel ?? _model,
                TokensIn = totals.In,
                CachedTokensIn = totals.CacheRead,
                CacheWriteTokensIn = totals.CacheWrite,
                TokensOut = totals.Out,
            },
        };
    }

    /// <summary>Token counts summed over model calls, and the model that produced the most output.</summary>
    private sealed class Totals
    {
        private readonly Dictionary<string, long> _outByModel = new(StringComparer.Ordinal);

        public bool Seen { get; private set; }
        public long In { get; private set; }
        public long CacheRead { get; private set; }
        public long CacheWrite { get; private set; }
        public long Out { get; private set; }

        public string? MainModel => _outByModel.Count == 0 ? null : _outByModel.MaxBy(m => m.Value).Key;

        public void Add(JsonElement usage, string? model = null)
        {
            Seen = true;
            In += ClaudeOutputParser.Long(usage, "inputTokens");
            CacheRead += ClaudeOutputParser.Long(usage, "cacheReadTokens");
            CacheWrite += ClaudeOutputParser.Long(usage, "cacheWriteTokens");
            var output = ClaudeOutputParser.Long(usage, "outputTokens");
            Out += output;
            if ((model ?? ClaudeOutputParser.Str(usage, "model")) is { Length: > 0 } name)
            {
                _outByModel[name] = _outByModel.GetValueOrDefault(name) + output;
            }
        }
    }
}
