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
/// </summary>
public sealed class AntigravityAdapter : IAgentAdapter
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

    /// <inheritdoc />
    public AgentKind Kind => AgentKind.Antigravity;

    /// <inheritdoc />
    public bool ReportsUsageAsItGoes => true;

    /// <inheritdoc />
    public bool CanResume => true;

    /// <inheritdoc />
    public AgentLaunch BuildLaunch(AgentLaunchContext context)
    {
        var profile = context.Profile;
        var promptDir = Path.GetDirectoryName(Path.GetFullPath(context.PromptFile))!;
        var settingsPath = WriteRunSettings(context, promptDir);

        List<string> args =
        [
            "--output-format", "stream-json",
            "--disable-slash-commands",
            "--settings", settingsPath,
            "--add-dir", promptDir,
        ];

        if (profile.CanRunCommands && profile.CanEditFiles)
        {
            // Explicit permissions.deny rules in --settings still take precedence over --dangerously-skip-permissions.
            args.Add("--dangerously-skip-permissions");
        }
        else if (profile.CanEditFiles)
        {
            args.AddRange(["--mode", "accept-edits"]);
        }

        if (context.RunnerIsolationEnabled && !context.RunnerSandboxed)
        {
            args.Add("--sandbox");
        }

        if (context.Model is { Length: > 0 } model)
        {
            args.AddRange(["--model", model]);
        }

        if (context.ResumeSessionId is { Length: > 0 } session)
        {
            args.AddRange(["--conversation", session]);
        }

        args.AddRange(["-p", $"Read the task in the file {context.PromptFile} and do it."]);
        return new AgentLaunch(args, null);
    }

    /// <inheritdoc />
    public IAgentOutputParser CreateParser() => new AntigravityOutputParser();

    /// <inheritdoc />
    public IAgentOutputParser CreateParser(string? configuredModel) => new AntigravityOutputParser(configuredModel);

    private static string WriteRunSettings(AgentLaunchContext context, string promptDir)
    {
        var profile = context.Profile;
        var allow = new List<string>
        {
            $"mcp({Connect.McpServerName.Value}/*)",
            $"read_file({context.PromptFile})",
        };
        foreach (var kept in context.KeepAgentTools.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            allow.Add($"mcp({kept.Trim()}/*)");
        }

        var deny = new List<string>(AlwaysDenied);
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
/// <item><c>init</c>: records <c>conversation_id</c>.</item>
/// <item><c>step_update</c>: tool steps emit progress notes; completed <c>agent_response</c> steps accumulate
/// live token usage (<c>input_tokens</c> + <c>cache_read_tokens</c>, <c>output_tokens</c>) and response deltas.</item>
/// <item><c>result</c>: final <c>status</c>, <c>response</c>, <c>error</c>, <c>duration_seconds</c> and total <c>usage</c>.</item>
/// </list>
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
