using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;

namespace MessageBus.Cli.Runner.Agents;

/// <summary>
/// Claude Code, headless: <c>claude -p --output-format stream-json --verbose</c> with the prompt on
/// stdin. The permission profile becomes <c>--permission-mode</c>, <c>--allowedTools</c> and
/// <c>--disallowedTools</c> (in <c>-p</c> mode anything not allowed that needs permission is refused).
/// <para>
/// Each turn re-sends the system prompt and every tool's schema, so the run loads only what the task needs
/// (the original issue): <c>--tools</c> keeps the built-in tools the profile can use (no sub-agents, skills, schedulers or
/// worktree tools; the skill list goes with the Skill tool); <c>--strict-mcp-config</c> with
/// <c>--mcp-config</c> gives it the mbus server alone (none of the person's other MCP servers, plugins'
/// servers or claude.ai connectors); <c>--exclude-dynamic-system-prompt-sections</c> moves the per-machine
/// parts of the system prompt into the first message, so runs share a cached system prompt.
/// </para>
/// <para>
/// the original issue: a role that keeps named tools gets each one's definition copied from this computer's own Claude Code settings
/// (<see cref="SettingsFile"/>: a project's local scope and user scope) into the run's <c>mcp.json</c>, with its tools
/// allowed. A repository's <c>.mcp.json</c> never counts: it isn't read here, and <c>--strict-mcp-config</c> keeps
/// Claude Code from reading it.
/// </para>
/// </summary>
/// <param name="mbus">How Claude Code starts the local mbus MCP server for a run; this mbus by default.</param>
/// <param name="settingsFile">Claude Code's own settings file (<see cref="SettingsFile"/>), read only for kept tools; <c>~/.claude.json</c> by default.</param>
public sealed class ClaudeCodeAdapter(Mcp.McpLaunchCommand? mbus = null, string? settingsFile = null) : IAgentAdapter
{
    /// <summary>
    /// Where Claude Code keeps its MCP servers (user scope, and each project's local scope): <c>.claude.json</c> in
    /// <c>CLAUDE_CONFIG_DIR</c> when that is set, else in the home folder.
    /// </summary>
    public static string SettingsFile(Func<string, string?> env, string homeDirectory) =>
        Path.Combine(env("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : homeDirectory, ".claude.json");

    /// <summary>The run's MCP settings, written next to the prompt (JSON can't pass through an npm <c>.cmd</c> shim as an argument).</summary>
    public const string McpConfigFileName = "mcp.json";

    /// <summary>The mbus MCP server's name in the agents' settings (written by <c>mbus connect</c>); its tools are <c>mcp__mbus__*</c>.</summary>
    public const string McpServerName = "mbus";

    /// <summary>Commands no run may use: agents never approve or merge (design S2), whatever the profile.</summary>
    public static IReadOnlyList<string> AlwaysDenied { get; } =
    [
        "Bash(gh pr merge:*)",
        "Bash(gh pr review --approve:*)",
        "Bash(gh pr review -a:*)",
        "Bash(az repos pr update:*)",
    ];

    /// <inheritdoc />
    public AgentKind Kind => AgentKind.ClaudeCode;

    /// <inheritdoc />
    public bool ReportsUsageAsItGoes => true;

    /// <inheritdoc />
    public bool CanResume => true;

    /// <inheritdoc />
    public AgentLaunch BuildLaunch(AgentLaunchContext context)
    {
        var kept = context.KeepAgentTools;
        var (keptServers, cannotKeep) = KeptServers(kept, context.WorkingDirectory);
        if (cannotKeep is not null)
        {
            return new AgentLaunch([], context.Prompt) { CannotStart = cannotKeep };
        }
        var profile = context.Profile;
        var allowed = new List<string> { "mcp__" + McpServerName };
        // the original issue: every tool of each kept server; nobody can answer a permission prompt in -p mode.
        allowed.AddRange(kept.Select(name => "mcp__" + name));
        var denied = new List<string>(AlwaysDenied);
        allowed.AddRange(profile.AllowedTools);
        if (profile.AllowedTools.Count == 0 && profile.CanRunCommands)
        {
            allowed.Add("Bash");
        }
        // the original issue: no shell, except the commands the profile names (a reviewer's read-only git commands); in -p mode
        // any other command is refused, and a plain "Bash" deny would refuse those too.
        if (!profile.CanRunCommands && !profile.AllowedTools.Any(t => t.StartsWith("Bash(", StringComparison.Ordinal)))
        {
            denied.Add("Bash");
        }
        if (!profile.CanEditFiles)
        {
            denied.AddRange(["Edit", "Write", "MultiEdit", "NotebookEdit"]);
        }
        if (!profile.CanPush)
        {
            denied.Add("Bash(git push:*)");
        }
        switch (profile.Network)
        {
            case NetworkAccess.None:
                denied.AddRange(["WebFetch", "WebSearch"]);
                break;
            case NetworkAccess.Allowlist:
                allowed.AddRange(profile.AllowedHosts.Select(h => $"WebFetch(domain:{h})"));
                break;
            case NetworkAccess.Open:
                allowed.AddRange(["WebFetch", "WebSearch"]);
                break;
        }

        List<string> args =
        [
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--permission-mode", profile.CanEditFiles ? "acceptEdits" : "default",
            "--allowedTools", string.Join(',', allowed.Distinct(StringComparer.Ordinal)),
            "--disallowedTools", string.Join(',', denied.Distinct(StringComparer.Ordinal)),
            "--tools", string.Join(',', BuiltInTools(profile)),
            "--strict-mcp-config",
            "--mcp-config", WriteMcpConfig(context, keptServers),
            "--exclude-dynamic-system-prompt-sections",
        ];
        if (context.ResumeSessionId is { } session)
        {
            // the original issue: the same conversation, from the folder it was started in; the flags above still limit what it may do.
            args.AddRange(["--resume", session]);
        }
        if (context.Model is { Length: > 0 } model)
        {
            args.AddRange(["--model", model]); // the task's or role's model (the original issue), or its cheaper one (the original issue)
        }
        if (kept.Count == 0)
        {
            return new AgentLaunch(args, context.Prompt);
        }
        var names = string.Join(", ", kept);
        return new AgentLaunch(args, context.Prompt + $"\nThis run keeps these tools: {names}. Use them instead of starting programs yourself.\n")
        {
            Note = $"Keeps these Claude Code tools: {names}.",
        };
    }

    /// <summary>
    /// Why a run can't keep <paramref name="kept"/> here (a name missing from this computer's Claude Code settings, an
    /// incomplete entry, or a settings file that can't be read), or null when it can. Never says what a definition holds:
    /// its <c>env</c> often has tokens.
    /// </summary>
    /// <param name="kept">The role's named tools.</param>
    /// <param name="workingDirectory">The run's folder, whose project entry counts first; null when not known yet.</param>
    internal string? CannotKeepTools(IReadOnlyList<string> kept, string? workingDirectory = null) =>
        KeptServers(kept, workingDirectory).Problem;

    /// <summary>
    /// Each kept server's definition, from this computer's own Claude Code settings: the local scope of the project for
    /// <paramref name="workingDirectory"/> (as in Claude Code, local wins over user), then user scope, then another
    /// project's local scope when all of them define it the same way (<c>claude mcp add</c> adds to local scope by default,
    /// and a run's own folder is never one of the person's projects). A project that turned the server off doesn't count.
    /// </summary>
    private (IReadOnlyDictionary<string, JsonObject> Servers, string? Problem) KeptServers(IReadOnlyList<string> kept, string? workingDirectory)
    {
        var found = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (kept.Count == 0) return (found, null);
        var names = string.Join(", ", kept);
        if (kept.Any(name => name.Equals(McpServerName, StringComparison.OrdinalIgnoreCase)))
        {
            return (found, $"This role's runs keep {names}, but {McpServerName} is messagebus.ai's own tool and can't be replaced. Remove it from the role's extra agent tools.");
        }
        var file = settingsFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        JsonObject? settings = null;
        try
        {
            if (File.Exists(file))
            {
                settings = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new JsonException();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Not the exception's text: a parse error can quote the file.
            return (found, $"This role's runs keep {names}, but Claude Code's settings file on this computer (.claude.json) couldn't be read. Fix the file, or run this role on another computer.");
        }

        var projects = (settings?["projects"] as JsonObject)?.Select(p => (Path: p.Key, Entry: p.Value as JsonObject))
            .Where(p => p.Entry is not null).ToList() ?? [];
        var here = workingDirectory is null ? null : projects.FirstOrDefault(p => SamePath(p.Path, workingDirectory)).Entry;
        var missing = new List<string>();
        foreach (var name in kept)
        {
            var definition = ServerIn(here, name) ?? (settings?["mcpServers"] as JsonObject)?[name];
            if (definition is null)
            {
                var elsewhere = projects.Select(p => ServerIn(p.Entry, name)).OfType<JsonNode>()
                    .DistinctBy(d => d.ToJsonString(), StringComparer.Ordinal).ToList();
                if (elsewhere.Count > 1)
                {
                    return (found, $"This role's runs keep {name}, which Claude Code on this computer has set up differently in several projects. Add it once for all projects (claude mcp add --scope user), or run this role on another computer.");
                }
                definition = elsewhere.SingleOrDefault();
            }
            if (definition is null)
            {
                missing.Add(name);
            }
            else if (!Complete(definition))
            {
                return (found, $"This role's runs keep {name}, but its entry in Claude Code's settings on this computer isn't complete: it needs a command to start or an address. Set it up again with claude mcp add, or run this role on another computer.");
            }
            else
            {
                found[name] = (JsonObject)definition.DeepClone();
            }
        }
        if (missing.Count == 0) return (found, null);
        var pronoun = missing.Count == 1 ? "it" : "them";
        var which = missing.Count == kept.Count ? pronoun : string.Join(", ", missing);
        return (found, $"This role's runs keep {names}, but Claude Code on this computer doesn't have {which} set up. Add {pronoun} to Claude Code's settings here (claude mcp add --scope user), or run this role on another computer.");

        static JsonNode? ServerIn(JsonObject? project, string name) =>
            project is null || (project["disabledMcpServers"] as JsonArray)?.Any(n => IsText(n, out var s) && s == name) == true
                ? null
                : (project["mcpServers"] as JsonObject)?[name];
    }

    /// <summary>A stdio server with a command, or an http or sse server with an address, with args, env and headers in the shape Claude Code reads.</summary>
    private static bool Complete(JsonNode definition)
    {
        if (definition is not JsonObject server) return false;
        static bool Filled(JsonNode? node) => IsText(node, out var s) && s.Trim().Length > 0;
        static bool Texts(JsonNode? node) => node is null || node is JsonArray a && a.All(n => IsText(n, out _));
        static bool Map(JsonNode? node) => node is null || node is JsonObject o && o.All(p => IsText(p.Value, out _));
        var type = server["type"] is null ? "stdio" : IsText(server["type"], out var typed) ? typed : null;
        return type switch
        {
            "stdio" => Filled(server["command"]) && Texts(server["args"]) && Map(server["env"]),
            "http" or "sse" => Filled(server["url"]) && Map(server["headers"]),
            _ => false,
        };
    }

    private static bool IsText(JsonNode? node, out string text)
    {
        text = "";
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue(out text!);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            static string Normal(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Replace('/', Path.DirectorySeparatorChar)));
            return string.Equals(Normal(a), Normal(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the run's MCP settings (the original issue: they can hold a kept tool's tokens) once the agent has ended, so they don't
    /// stay in a run folder that is held or kept. False when the file is still there.
    /// </summary>
    public static bool DeleteMcpConfig(string runDirectory)
    {
        var path = Path.Combine(runDirectory, McpConfigFileName);
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return !File.Exists(path);
        }
    }

    /// <summary>
    /// The built-in tools a run gets: reading always; editing, the shell and the web as the profile allows; and any
    /// built-in tool the profile names (<c>Bash(dotnet test:*)</c> names Bash). Left out: sub-agents (Agent), skills,
    /// workflows, schedulers, worktrees, notebooks and the rest of Claude Code's default set, which a one-task run
    /// doesn't use and which cost their schemas on every turn.
    /// </summary>
    public static IReadOnlyList<string> BuiltInTools(PermissionProfile profile)
    {
        var tools = new List<string> { "Read", "Glob", "Grep" };
        if (profile.CanEditFiles)
        {
            tools.AddRange(["Edit", "Write"]);
        }
        if (profile.CanRunCommands)
        {
            tools.Add("Bash");
        }
        if (profile.Network == NetworkAccess.Open || (profile.Network == NetworkAccess.Allowlist && profile.AllowedHosts.Count > 0))
        {
            tools.Add("WebFetch");
        }
        if (profile.Network == NetworkAccess.Open)
        {
            tools.Add("WebSearch");
        }
        foreach (var named in profile.AllowedTools)
        {
            var name = named.Split('(', 2)[0].Trim();
            if (name.Length > 0 && !name.StartsWith("mcp__", StringComparison.Ordinal))
            {
                tools.Add(name);
            }
        }
        return [.. tools.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Writes the run's MCP settings: the mbus server, with the run's MBUS_* variables (the original issue), and the kept servers
    /// (the original issue), and returns the file's path. Next to the prompt file, in the run's folder, which the run may read; only
    /// this user can read it (a kept server's <c>env</c> often holds a token), and it is deleted when the agent ends
    /// (<see cref="DeleteMcpConfig"/>).
    /// </summary>
    private string WriteMcpConfig(AgentLaunchContext context, IReadOnlyDictionary<string, JsonObject> kept)
    {
        var command = mbus ?? Mcp.McpLaunchCommand.ForCurrentProcess();
        var env = new JsonObject();
        foreach (var (name, value) in (context.McpEnvironment ?? new Dictionary<string, string>()).OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            env[name] = value;
        }
        var servers = new JsonObject
        {
            [McpServerName] = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = command.Command,
                ["args"] = new JsonArray([.. command.Args.Select(a => (JsonNode)a)]),
                ["env"] = env,
            },
        };
        foreach (var (name, definition) in kept)
        {
            servers[name] = definition;
        }
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(context.PromptFile))!, McpConfigFileName);
        WriteOwnerOnly(path, new JsonObject { ["mcpServers"] = servers }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>A new file only this user can read (on macOS and Linux; on Windows the run folder is in the user's own profile).</summary>
    private static void WriteOwnerOnly(string path, string text)
    {
        File.Delete(path); // made afresh, so the mode applies from the first byte
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(path, options))
        {
            stream.Write(new UTF8Encoding(false).GetBytes(text));
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // whatever the umask removed
        }
    }

    /// <inheritdoc />
    public IAgentOutputParser CreateParser() => new ClaudeOutputParser();
}

/// <summary>
/// Reads Claude Code's JSON output: either <c>--output-format json</c> (one result object) or
/// <c>stream-json</c> (one event per line: <c>system</c>/<c>init</c>, <c>assistant</c>, <c>user</c>,
/// then <c>result</c>). Usage comes from the last <c>result</c> event (its totals are cumulative):
/// <c>modelUsage</c> per model when present (it includes sub-agents), else <c>usage</c>; cost from
/// <c>total_cost_usd</c>; time from <c>duration_ms</c>, summed over results when a run resumes after
/// background work.
/// </summary>
public sealed class ClaudeOutputParser(TimeProvider? time = null) : IAgentOutputParser
{
    private readonly CapacityReadings _capacity = new("claude-code", time ?? TimeProvider.System);
    /// <inheritdoc />
    public MessageBus.Contracts.Runner.AgentCapacityReport? Capacity => _capacity.Report;
    private const int MaxBuffered = 1 << 20;
    private readonly StringBuilder _unparsed = new();
    private string? _initModel;
    private string? _lastText;
    private AgentOutcome? _result;
    private int _results;
    private long _durationMs;
    // Each API call's usage as the assistant events report it (the original issue), by message id: Claude Code sends one event per
    // content block, each repeating its message's usage, so the latest for an id replaces the one before.
    private readonly Dictionary<string, (string? Model, long In, long CachedIn, long Writes, long Out)> _calls = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string? Observe(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }
        if (!TryParse(trimmed, out var doc))
        {
            // Pretty-printed JSON spans lines; keep it for Complete().
            if (_unparsed.Length < MaxBuffered)
            {
                _unparsed.AppendLine(line);
            }
            return null;
        }
        using (doc)
        {
            return Handle(doc.RootElement);
        }
    }

    /// <inheritdoc />
    public AgentOutcome Complete()
    {
        if (_result is null && _unparsed.Length > 0 && TryParse(_unparsed.ToString(), out var doc))
        {
            using (doc)
            {
                Handle(doc.RootElement);
            }
        }
        return _result ?? new AgentOutcome
        {
            FinalText = _lastText,
            Usage = new Usage { AgentKind = AgentKind.ClaudeCode, Vendor = "anthropic", Model = _initModel },
        };
    }

    private string? Handle(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || Str(root, "type") is not { } type)
        {
            return null;
        }
        switch (type)
        {
            case "rate_limit_event" when root.TryGetProperty("rate_limit_info", out var info):
                _capacity.Claude(info);
                return null;
            case "system" when Str(root, "subtype") == "init":
                _initModel = Str(root, "model") ?? _initModel;
                return null;
            case "assistant" when root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object:
                _initModel ??= Str(message, "model");
                CountCall(message);
                return Progress(message);
            case "result":
                // A run that resumes after background work ends each turn with a result (the original issue). Tokens and cost are
                // the session's running totals (Claude Code documents modelUsage and total_cost_usd as cumulative:
                // "read the latest result rather than summing"), so the latest wins; duration_ms is that turn's alone.
                _results++;
                _durationMs += Long(root, "duration_ms");
                _result = ReadResult(root);
                return null;
            default:
                return null;
        }
    }

    /// <inheritdoc />
    public Usage? UsageSoFar
    {
        get
        {
            // The model calls as they ended; the final result's totals come from Complete().
            if (_calls.Count == 0) return null;
            // Priced as the model that wrote the most; small helper calls are a small share of a run.
            var model = _calls.Values.GroupBy(c => c.Model).MaxBy(g => g.Sum(c => c.Out))?.Key ?? _initModel;
            return new Usage
            {
                AgentKind = AgentKind.ClaudeCode,
                Vendor = "anthropic",
                Model = model,
                TokensIn = _calls.Values.Sum(c => c.In + c.CachedIn + c.Writes),
                CachedTokensIn = _calls.Values.Sum(c => c.CachedIn),
                CacheWriteTokensIn = _calls.Values.Sum(c => c.Writes),
                TokensOut = _calls.Values.Sum(c => c.Out),
            };
        }
    }

    private void CountCall(JsonElement message)
    {
        if (Str(message, "id") is not { } id || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        var output = Long(usage, "output_tokens");
        if (_calls.TryGetValue(id, out var seen) && seen.Out > output)
        {
            output = seen.Out;
        }
        _calls[id] = (Str(message, "model") ?? _initModel, Long(usage, "input_tokens"), Long(usage, "cache_read_input_tokens"),
            Long(usage, "cache_creation_input_tokens"), output);
    }

    private string? Progress(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        string? note = null;
        foreach (var item in content.EnumerateArray())
        {
            switch (Str(item, "type"))
            {
                case "text" when Str(item, "text") is { } text:
                    _lastText = text;
                    note = AgentText.OneLine(text);
                    break;
                case "tool_use" when Str(item, "name") is { } name:
                    var detail = item.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                        ? Str(input, "description") ?? Str(input, "command") ?? Str(input, "file_path") ?? Str(input, "pattern")
                        : null;
                    note = AgentText.OneLine(detail is null ? name : $"{name}: {detail}");
                    break;
            }
        }
        return note;
    }

    private AgentOutcome ReadResult(JsonElement root)
    {
        long tokensIn = 0, tokensOut = 0, cachedIn = 0, writes = 0;
        string? model = null;
        // Which cache writes were for an hour: only the API usage block says (cache_creation), never modelUsage (the original issue).
        var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        var hourWrites = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("cache_creation", out var tiers)
            ? Long(tiers, "ephemeral_1h_input_tokens") : 0;
        if (root.TryGetProperty("modelUsage", out var perModel) && perModel.ValueKind == JsonValueKind.Object)
        {
            var bestCost = decimal.MinValue;
            foreach (var entry in perModel.EnumerateObject())
            {
                var m = entry.Value;
                var output = Long(m, "outputTokens");
                tokensIn += Long(m, "inputTokens") + Long(m, "cacheReadInputTokens") + Long(m, "cacheCreationInputTokens");
                cachedIn += Long(m, "cacheReadInputTokens");
                writes += Long(m, "cacheCreationInputTokens"); // cache writes cost more than input (the original issue)
                tokensOut += output;
                // The model that cost most did the work; small helper models also show up here.
                var weight = Dec(m, "costUSD") ?? output;
                if (weight > bestCost)
                {
                    bestCost = weight;
                    model = entry.Name;
                }
            }
        }
        else if (usage.ValueKind == JsonValueKind.Object)
        {
            tokensIn = Long(usage, "input_tokens") + Long(usage, "cache_read_input_tokens") + Long(usage, "cache_creation_input_tokens");
            cachedIn = Long(usage, "cache_read_input_tokens");
            writes = Long(usage, "cache_creation_input_tokens");
            tokensOut = Long(usage, "output_tokens");
        }

        var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        var text = Str(root, "result") ?? _lastText;
        return new AgentOutcome
        {
            FinalText = text,
            ReportedError = isError,
            ErrorText = isError ? text ?? Str(root, "subtype") : null,
            SpansTurns = _results > 1,
            Usage = new Usage
            {
                AgentKind = AgentKind.ClaudeCode,
                Vendor = "anthropic",
                Model = model ?? _initModel,
                TokensIn = tokensIn,
                CachedTokensIn = cachedIn,
                CacheWriteTokensIn = writes,
                CacheWrite1hTokensIn = Math.Min(hourWrites, writes),
                TokensOut = tokensOut,
                EstimatedCostUsd = Dec(root, "total_cost_usd"),
                // Every turn's time; with more than one turn the runner uses its own clock (SpansTurns).
                ActiveSeconds = _durationMs / 1000.0,
            },
        };
    }

    private static bool TryParse(string text, out JsonDocument doc)
    {
        try
        {
            doc = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            doc = null!;
            return false;
        }
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    internal static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
}
