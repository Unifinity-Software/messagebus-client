using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;

namespace MessageBus.Cli.Runner.Agents;

/// <summary>
/// Google Antigravity CLI (<c>agy</c>), headless: <c>agy -p "…" --output-format stream-json --disable-slash-commands</c>
/// with a per-run <c>--settings</c> JSON file carrying <c>permissions.allow</c> and <c>permissions.deny</c> rules.
/// Antigravity evaluates permission lists in strict precedence order (<c>Deny &gt; Ask &gt; Allow</c>), and explicit deny
/// rules still block actions even when <c>--dangerously-skip-permissions</c> is passed.
/// <para>
/// Like the other adapters, a run denies every other MCP server configured in the person's <c>mcp_config.json</c>
/// except <c>mbus</c> and any tools named in <see cref="AgentLaunchContext.KeepAgentTools"/>, and fails before launch
/// when a kept tool isn't configured on this machine. Task-scoped <c>MBUS_*</c> variables from
/// <see cref="AgentLaunchContext.McpEnvironment"/> are forwarded in the launched process environment so the spawned
/// <c>mbus mcp</c> server receives the run's delivery, settle and run-tools files instead of the machine's interactive sign-in.
/// </para>
/// </summary>
/// <param name="mcpConfigFile">Antigravity's global <c>mcp_config.json</c>; <c>~/.gemini/config/mcp_config.json</c> by default.</param>
public sealed class AntigravityAdapter(string? mcpConfigFile = null) : IAgentAdapter
{
    /// <summary>The per-run settings file written next to the prompt.</summary>
    public const string SettingsFileName = "agy-settings.json";

    /// <summary>Commands no run may use: agents never approve or merge, whatever the profile.</summary>
    public static IReadOnlyList<string> AlwaysDenied { get; } =
    [
        "command(gh pr merge)",
        "command(gh pr review --approve)",
        "command(gh pr review -a)",
        "command(az repos pr update)",
    ];

    /// <summary>Antigravity's default global MCP config path (<c>~/.gemini/config/mcp_config.json</c>).</summary>
    public static string DefaultMcpConfigFile(string? homeDirectory = null) =>
        Path.Combine(homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config", "mcp_config.json");

    /// <inheritdoc />
    public AgentKind Kind => AgentKind.Antigravity;

    /// <inheritdoc />
    public bool ReportsUsageAsItGoes => true;

    /// <inheritdoc />
    public bool CanResume => false;

    /// <inheritdoc />
    public AgentLaunch BuildLaunch(AgentLaunchContext context)
    {
        var configPath = mcpConfigFile ?? DefaultMcpConfigFile();
        var configuredServers = ReadConfiguredMcpServers(configPath);
        var kept = context.KeepAgentTools
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var missing = kept
            .Where(name => !configuredServers.TryGetValue(name, out var enabled) || !enabled)
            .ToList();
        if (missing.Count > 0)
        {
            return new AgentLaunch([], null)
            {
                CannotStart = $"This machine's Antigravity MCP settings don't have {string.Join(", ", missing)}. Configure {(missing.Count == 1 ? "it" : "them")} in {configPath} first.",
            };
        }

        var deniedServers = configuredServers.Keys
            .Where(name => !string.Equals(name, Connect.McpServerName.Value, StringComparison.Ordinal)
                && !kept.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var profile = context.Profile;
        var promptDir = Path.GetDirectoryName(Path.GetFullPath(context.PromptFile))!;
        var settingsPath = WriteRunSettings(context, promptDir, kept, deniedServers);

        List<string> args =
        [
            "--output-format", "stream-json",
            "--disable-slash-commands",
            "--settings", settingsPath,
            "--add-dir", promptDir,
        ];

        // Under --dangerously-skip-permissions, allow rules are bypassed while permissions.deny rules in --settings
        // still take precedence (Deny > Ask > Allow). For roles with host-allowlist networking, outbound host
        // filtering is enforced by the runner's RunNetwork proxy rather than Antigravity's read_url allowlist.
        if (profile.CanRunCommands && profile.CanEditFiles)
        {
            args.Add("--dangerously-skip-permissions");
        }
        else if (profile.CanEditFiles)
        {
            args.AddRange(["--mode", "accept-edits"]);
        }

        string? note = null;
        if (context.RunnerIsolationEnabled && !context.RunnerSandboxed)
        {
            args.Add("--sandbox");
            note = "Antigravity CLI requested its own terminal sandbox (--sandbox) because the runner's OS sandbox does not confine this agent.";
        }

        if (context.Model is { Length: > 0 } model)
        {
            args.AddRange(["--model", model]);
        }

        args.AddRange(["-p", $"Read the task in the file {context.PromptFile} and do it."]);

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (context.McpEnvironment is { } mcpEnv)
        {
            foreach (var (key, value) in mcpEnv)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    env[key] = value;
                }
            }
        }

        return new AgentLaunch(args, null)
        {
            Note = note,
            Environment = env,
        };
    }

    /// <inheritdoc />
    public IAgentOutputParser CreateParser() => new AntigravityOutputParser();

    /// <inheritdoc />
    public IAgentOutputParser CreateParser(string? configuredModel) => new AntigravityOutputParser(configuredModel);

    /// <summary>Reads MCP servers from Antigravity's <c>mcp_config.json</c> (name → enabled).</summary>
    internal static Dictionary<string, bool> ReadConfiguredMcpServers(string configFile)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (Connect.JsonConfigFile.TryRead(configFile)?["mcpServers"] is not JsonObject servers)
        {
            return result;
        }
        foreach (var (name, node) in servers)
        {
            if (string.IsNullOrWhiteSpace(name) || node is not JsonObject obj)
            {
                continue;
            }
            var disabled = obj["disabled"] is JsonValue d && d.TryGetValue<bool>(out var isDisabled) && isDisabled;
            result[name] = !disabled;
        }
        return result;
    }

    private static string WriteRunSettings(
        AgentLaunchContext context,
        string promptDir,
        IReadOnlyList<string> keptServers,
        IReadOnlyList<string> deniedServers)
    {
        var profile = context.Profile;
        var allow = new List<string>
        {
            $"mcp({Connect.McpServerName.Value}/*)",
            $"read_file({context.PromptFile})",
        };
        foreach (var kept in keptServers)
        {
            allow.Add($"mcp({kept}/*)");
        }

        var deny = new List<string>(AlwaysDenied);
        foreach (var other in deniedServers)
        {
            deny.Add($"mcp({other}/*)");
        }
        if (!profile.CanPush)
        {
            deny.Add("command(git push)");
        }

        if (profile.CanEditFiles)
        {
            allow.Add($"write_file({context.WorkingDirectory})");
        }
        else
        {
            deny.Add("write_file(*)");
        }

        if (profile.CanRunCommands)
        {
            allow.Add("command(*)");
        }
        else
        {
            var commands = AllowedCommandRules(profile.AllowedTools);
            allow.AddRange(commands);
            if (commands.Count == 0)
            {
                deny.Add("command(*)");
            }
        }

        if (profile.Network == NetworkAccess.Open)
        {
            allow.Add("read_url(*)");
        }
        else if (profile.Network == NetworkAccess.Allowlist)
        {
            foreach (var host in profile.AllowedHosts.Where(h => !string.IsNullOrWhiteSpace(h)))
            {
                allow.Add($"read_url({host.Trim()})");
            }
        }
        else
        {
            deny.Add("read_url(*)");
            deny.Add("execute_url(*)");
            deny.Add("download_url(*)");
        }

        var settings = new JsonObject
        {
            ["disableSlashCommands"] = true,
            ["permissions"] = new JsonObject
            {
                ["allow"] = new JsonArray([.. allow.Distinct(StringComparer.Ordinal).Select(a => (JsonNode?)JsonValue.Create(a))]),
                ["deny"] = new JsonArray([.. deny.Distinct(StringComparer.Ordinal).Select(d => (JsonNode?)JsonValue.Create(d))]),
            },
        };

        // For non-command roles (e.g. read-only reviewers with a small Bash(...) allowlist), explicitly pin
        // default mode + request-review so Antigravity's auto mode cannot auto-approve unlisted commands;
        // in headless -p mode, any action that would ask for approval is immediately soft-denied without hanging.
        if (!profile.CanRunCommands)
        {
            settings["agentMode"] = "";
            settings["toolPermission"] = "request-review";
        }

        var path = Path.Combine(promptDir, SettingsFileName);
        Directory.CreateDirectory(promptDir);
        File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Converts Claude-style <c>Bash(git log:*)</c> entries from a profile into Antigravity <c>command(git log)</c> rules.</summary>
    internal static List<string> AllowedCommandRules(IReadOnlyList<string> allowedTools)
    {
        var rules = new List<string>();
        foreach (var tool in allowedTools)
        {
            if (!tool.StartsWith("Bash(", StringComparison.Ordinal) || !tool.EndsWith(')'))
            {
                continue;
            }
            var inner = tool["Bash(".Length..^1].Trim();
            if (inner.EndsWith(":*", StringComparison.Ordinal))
            {
                inner = inner[..^2].Trim();
            }
            else if (inner.EndsWith(" *", StringComparison.Ordinal))
            {
                inner = inner[..^2].Trim();
            }
            if (inner.Length > 0)
            {
                rules.Add($"command({inner})");
            }
        }
        return rules;
    }
}

/// <summary>
/// Reads <c>agy -p --output-format stream-json</c> NDJSON events:
/// <list type="bullet">
/// <item><c>step_update</c>: tool steps emit progress notes; completed <c>agent_response</c> steps accumulate
/// live token usage and response text.</item>
/// <item><c>result</c>: final <c>status</c>, <c>response</c>, <c>error</c>, <c>duration_seconds</c> and total <c>usage</c>.</item>
/// </list>
/// Observed in Antigravity CLI 1.5.14: <c>usage.input_tokens</c> excludes <c>usage.cache_read_tokens</c> (on multi-step
/// turns, <c>input_tokens</c> drops by the cached prompt prefix count while <c>total_tokens == input_tokens + output_tokens</c>),
/// so contract <see cref="Usage.TokensIn"/> sums <c>input_tokens + cache_read_tokens</c> to match the other adapters.
/// A non-JSON line counts as plain fallback text.
/// </summary>
public sealed class AntigravityOutputParser(string? configuredModel = null) : IAgentOutputParser
{
    private readonly string? _model = configuredModel;
    private readonly Dictionary<int, System.Text.StringBuilder> _stepText = new();
    private long _stepIn;
    private long _stepCacheRead;
    private long _stepOut;
    private bool _stepUsageSeen;

    private long _resultIn;
    private long _resultCacheRead;
    private long _resultOut;
    private bool _resultUsageSeen;
    private double? _durationSeconds;

    private string? _lastResponse;
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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            // Handle single-envelope --output-format json fallback if ever fed directly.
            var ev = ClaudeOutputParser.Str(root, "event");
            if (ev is null && ClaudeOutputParser.Str(root, "status") is not null)
            {
                HandleResult(root);
                return null;
            }
            switch (ev)
            {
                case "step_update" when root.TryGetProperty("step_update", out var step) && step.ValueKind == JsonValueKind.Object:
                    return HandleStepUpdate(step);
                case "result" when root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object:
                    HandleResult(result);
                    return null;
                default:
                    return null;
            }
        }
    }

    private string? HandleStepUpdate(JsonElement step)
    {
        var stepType = ClaudeOutputParser.Str(step, "step_type");
        var state = ClaudeOutputParser.Str(step, "state");
        var index = step.TryGetProperty("step_index", out var idx) && idx.TryGetInt32(out var n) ? n : -1;

        if (stepType == "tool" && state == "ACTIVE")
        {
            return ToolNote(step);
        }

        if (stepType == "agent_response")
        {
            if (ClaudeOutputParser.Str(step, "text_delta") is { Length: > 0 } delta)
            {
                if (!_stepText.TryGetValue(index, out var sb))
                {
                    sb = new System.Text.StringBuilder();
                    _stepText[index] = sb;
                }
                sb.Append(delta);
            }
            if (state == "DONE")
            {
                if (step.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    var input = ClaudeOutputParser.Long(usage, "input_tokens");
                    var cacheRead = ClaudeOutputParser.Long(usage, "cache_read_tokens");
                    var output = ClaudeOutputParser.Long(usage, "output_tokens");
                    _stepIn += input + cacheRead;
                    _stepCacheRead += cacheRead;
                    _stepOut += output;
                    _stepUsageSeen = true;
                }
                if (_stepText.TryGetValue(index, out var finished) && finished.ToString().Trim() is { Length: > 0 } text)
                {
                    _lastResponse = text;
                    return AgentText.OneLine(text);
                }
            }
        }
        return null;
    }

    private void HandleResult(JsonElement result)
    {
        var status = ClaudeOutputParser.Str(result, "status");
        if (status is not null && !string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            _failed = true;
            _error = ClaudeOutputParser.Str(result, "error") ?? $"Antigravity finished with status {status}";
        }
        if (ClaudeOutputParser.Str(result, "response") is { Length: > 0 } response && response.Trim().Length > 0)
        {
            _lastResponse = response.Trim();
        }
        if (result.TryGetProperty("duration_seconds", out var dur) && dur.TryGetDouble(out var seconds) && seconds >= 0)
        {
            _durationSeconds = seconds;
        }
        if (result.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var input = ClaudeOutputParser.Long(usage, "input_tokens");
            var cacheRead = ClaudeOutputParser.Long(usage, "cache_read_tokens");
            var output = ClaudeOutputParser.Long(usage, "output_tokens");
            _resultIn = input + cacheRead;
            _resultCacheRead = cacheRead;
            _resultOut = output;
            _resultUsageSeen = true;
        }
    }

    private static string? ToolNote(JsonElement step)
    {
        var tool = ClaudeOutputParser.Str(step, "tool_name");
        if (tool is null)
        {
            return null;
        }
        if (step.TryGetProperty("tool_info", out var info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object)
        {
            if (ClaudeOutputParser.Str(parameters, "CommandLine") is { Length: > 0 } cmd)
            {
                return AgentText.OneLine("Running " + cmd);
            }
            if (ClaudeOutputParser.Str(parameters, "Path") is { Length: > 0 } path)
            {
                return AgentText.OneLine($"{tool}: {path}");
            }
        }
        return AgentText.OneLine(tool);
    }

    /// <inheritdoc />
    public Usage? UsageSoFar => _stepUsageSeen
        ? new Usage
        {
            AgentKind = AgentKind.Antigravity,
            Vendor = "google",
            Model = _model,
            TokensIn = _stepIn,
            CachedTokensIn = _stepCacheRead,
            TokensOut = _stepOut,
        }
        : null;

    /// <inheritdoc />
    public AgentOutcome Complete()
    {
        var tokensIn = _resultUsageSeen ? _resultIn : _stepIn;
        var cacheRead = _resultUsageSeen ? _resultCacheRead : _stepCacheRead;
        var tokensOut = _resultUsageSeen ? _resultOut : _stepOut;
        return new AgentOutcome
        {
            FinalText = _lastResponse ?? _lastText,
            ReportedError = _failed,
            ErrorText = _error,
            Usage = new Usage
            {
                AgentKind = AgentKind.Antigravity,
                Vendor = "google",
                Model = _model,
                TokensIn = tokensIn,
                CachedTokensIn = cacheRead,
                TokensOut = tokensOut,
                ActiveSeconds = _durationSeconds ?? 0,
            },
        };
    }
}
