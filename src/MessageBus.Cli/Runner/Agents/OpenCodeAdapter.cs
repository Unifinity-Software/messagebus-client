using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Agents;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;

namespace MessageBus.Cli.Runner.Agents;

/// <summary>
/// OpenCode, headless (the original issue; checked against OpenCode 2.3.0, package <c>@opencode/cli</c>, and 1.19.5, package
/// <c>opencode-ai</c>): <c>opencode run --format json "…"</c> with a short prompt pointing at the saved prompt file.
/// <para>
/// OpenCode 2 sends every client to one shared background server by default, which would run the agent's tools
/// outside this run (not in its sandbox, its environment or its process group), so a run starts its own server
/// with <c>--standalone</c>. OpenCode 1 always does, and refuses the flag, so it is left out for version 1.
/// </para>
/// <para>
/// OpenCode has no per-run permission flags; the profile goes in <c>OPENCODE_CONFIG_CONTENT</c>, which both
/// versions merge over the person's settings (version 2 converts the version 1 <c>permission</c> and <c>mcp</c>
/// shapes used here): edits, shell commands (never <c>git push</c> unless the role may push; never merging or
/// approving a pull request) and web fetches, plus the mbus MCP server with the run's MBUS_* variables. A request
/// none of the rules allow is refused, as nobody can answer it.
/// </para>
/// </summary>
/// <param name="version">The installed OpenCode's version (<c>opencode --version</c>), if known.</param>
/// <param name="configuredModel">The <c>model</c> in OpenCode's settings (<c>provider/model</c>), for usage reports.</param>
/// <param name="mbus">How OpenCode starts the local mbus MCP server for a run; this mbus by default.</param>
public sealed class OpenCodeAdapter(string? version = null, string? configuredModel = null, Mcp.McpLaunchCommand? mbus = null) : IAgentAdapter
{
    private Func<OpenCodeSettings>? _readSettings;
    private OpenCodeSelection? _selection;
    private Func<HttpMessageHandler>? _readinessHandler;
    private Func<string?>? _managedProblem;
    internal Uri? LocalEndpoint => _selection?.Endpoint;

    internal static OpenCodeAdapter ForRunner(string? version, Func<string, string?> env, string home,
        Func<HttpMessageHandler>? handler = null, Func<IReadOnlyDictionary<string, string>>? storedEnvironment = null) => new(version)
        {
            _readSettings = () => OpenCodeSettings.Read(new SignInEnvironment(env, storedEnvironment?.Invoke() ?? new Dictionary<string, string>()).Get, home, version),
            _readinessHandler = handler,
            _managedProblem = () => OpenCodeLocalRuntime.ManagedProblem(new SignInEnvironment(env, storedEnvironment?.Invoke() ?? new Dictionary<string, string>()).Get),
        };

    internal async Task<(OpenCodeAdapter Adapter, string? Error)> SelectAsync(string? requested, CancellationToken ct)
    {
        if (_readSettings is null) return (this, null);
        var settings = _readSettings(); // Fresh after approval, outside the engine lock.
        if (settings.Problem is { } problem) return (this, problem);
        if (settings.SelectionProblem(requested) is { } invalid) return (this, invalid);
        if (settings.LocalRunProblem(requested, version) is { } unsafeLocal) return (this, unsafeLocal);
        var selection = settings.Select(requested);
        if (selection.Endpoint is not null && _managedProblem?.Invoke() is { } managed) return (this, managed);
        var error = await OpenCodeReadiness.CheckAsync(selection, ct, _readinessHandler).ConfigureAwait(false);
        if (error is not null) return (this, error);
        if (selection.Endpoint is not null) selection = OpenCodeLocalRuntime.Confine(selection);
        return (new OpenCodeAdapter(version, selection.Id, mbus)
        {
            _managedProblem = _managedProblem,
            _selection = selection,
        }, error);
    }
    /// <summary>The variable OpenCode reads extra settings from (JSON), over its settings files.</summary>
    public const string ConfigContentVariable = "OPENCODE_CONFIG_CONTENT";

    /// <inheritdoc />
    public AgentKind Kind => AgentKind.OpenCode;

    /// <inheritdoc />
    public bool ReportsUsageAsItGoes => true;

    /// <summary>
    /// Whether <paramref name="version"/> is OpenCode 2 or later. An unknown version counts as the current one: a
    /// version 1 then fails at once on <c>--standalone</c>, where the other mistake would quietly run the agent's
    /// tools outside the run.
    /// </summary>
    public static bool UsesSharedServer(string? version) =>
        version is not { Length: > 0 } v || !int.TryParse(v.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) || major >= 2;

    /// <inheritdoc />
    public AgentLaunch BuildLaunch(AgentLaunchContext context)
    {
        var environment = new Dictionary<string, string>();
        if (_selection?.ConfinedLocal == true)
        {
            if (_managedProblem?.Invoke() is { } managed) return new AgentLaunch([], null) { CannotStart = managed };
            if (context.RunDirectory is not { Length: > 0 } runDirectory)
                return new AgentLaunch([], null) { CannotStart = "Local OpenCode requires a run-owned configuration directory." };
            environment = OpenCodeLocalRuntime.EnvironmentFor(runDirectory);
        }
        environment[ConfigContentVariable] = RunConfig(context).ToJsonString();
        List<string> args = ["run"];
        if (UsesSharedServer(version))
        {
            args.Add("--standalone");
        }
        if ((_selection?.ConfinedLocal == true ? OpenCodeLocalRuntime.Model : _selection?.Id ?? context.Model ?? configuredModel) is { Length: > 0 } model)
        {
            args.AddRange(["--model", model]); // the role's cheaper model (the original issue), as OpenCode names it: provider/model
        }
        args.AddRange(["--format", "json", $"Read the task in the file {context.PromptFile} and do it."]);
        return new AgentLaunch(args, null)
        {
            Environment = environment,
        };
    }

    /// <summary>The run's settings: the profile as OpenCode permissions, and the mbus server.</summary>
    internal JsonObject RunConfig(AgentLaunchContext context)
    {
        var profile = context.Profile;
        JsonNode shell;
        if (profile.CanRunCommands)
        {
            var rules = new JsonObject { ["*"] = "allow" };
            List<string> denied = ["gh pr merge", "gh pr review"];
            if (!profile.CanPush)
            {
                denied.Add("git push");
            }
            foreach (var command in denied)
            {
                rules[command] = "deny";
                rules[command + " *"] = "deny";
            }
            shell = rules;
        }
        else
        {
            // The profile's commands (Claude Code's Bash(git log:*) rules, the original issue and the original issue): OpenCode's "git log *".
            var rules = new JsonObject { ["*"] = "deny" };
            foreach (var tool in profile.AllowedTools.Where(t => t.StartsWith("Bash(", StringComparison.Ordinal) && t.EndsWith(')')))
            {
                var command = tool["Bash(".Length..^1];
                command = command.EndsWith(":*", StringComparison.Ordinal) ? command[..^2] : command;
                rules[command] = "allow";
                rules[command + " *"] = "allow";
            }
            shell = rules;
        }
        var config = _selection is { } selected ? JsonNode.Parse(selected.ConfigJson)!.AsObject() : new JsonObject();
        // Local selections supply a fresh single-provider config; cloud selections retain their settings.
        OpenCodeSettings.Merge(config, new JsonObject
        {
            ["permission"] = new JsonObject
            {
                ["edit"] = profile.CanEditFiles ? "allow" : "deny",
                ["bash"] = shell,
                ["webfetch"] = profile.Network == NetworkAccess.None ? "deny" : "allow",
            },
        });
        if (context.McpEnvironment is { } mcpEnvironment)
        {
            var command = mbus ?? Mcp.McpLaunchCommand.ForCurrentProcess();
            var environment = new JsonObject();
            foreach (var (name, value) in mcpEnvironment.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                environment[name] = value;
            }
            var mcpConfig = new JsonObject
            {
                [Connect.McpServerName.Value] = new JsonObject
                {
                    ["type"] = "local",
                    ["command"] = new JsonArray([(JsonNode)command.Command, .. command.Args.Select(a => (JsonNode)a)]),
                    ["environment"] = environment,
                    ["enabled"] = true,
                },
            };
            if (_selection is not null && UsesSharedServer(version))
                mcpConfig = new JsonObject { ["servers"] = mcpConfig };
            OpenCodeSettings.Merge(config, new JsonObject { ["mcp"] = mcpConfig });
        }
        return config;
    }

    /// <inheritdoc />
    public IAgentOutputParser CreateParser() => new OpenCodeOutputParser(_selection?.Id ?? configuredModel, _selection?.ConfinedLocal == true);

    /// <inheritdoc />
    public IAgentOutputParser CreateParser(string? model) => new OpenCodeOutputParser(_selection?.Id ?? (model is { Length: > 0 } ? model : configuredModel), _selection?.ConfinedLocal == true);

    /// <summary>OpenCode's settings folder: <c>OPENCODE_CONFIG_DIR</c>, else <c>$XDG_CONFIG_HOME/opencode</c>, else <c>~/.config/opencode</c> (on every OS).</summary>
    public static string ConfigDirectory(Func<string, string?> env, string home) =>
        env("OPENCODE_CONFIG_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(env("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".config"), "opencode");

    /// <summary>The settings file in <paramref name="configDirectory"/>: <c>opencode.jsonc</c> when there is one, else <c>opencode.json</c>.</summary>
    public static string ConfigFile(string configDirectory) =>
        File.Exists(Path.Combine(configDirectory, "opencode.jsonc")) ? Path.Combine(configDirectory, "opencode.jsonc") : Path.Combine(configDirectory, "opencode.json");

    /// <summary>The <c>model</c> in OpenCode's settings, or null.</summary>
    public static string? ReadConfiguredModel(string configDirectory) =>
        Connect.JsonConfigFile.TryRead(ConfigFile(configDirectory))?["model"] is JsonValue model && model.TryGetValue<string>(out var name) && name.Length > 0 ? name : null;
}

/// <summary>
/// Reads <c>opencode run --format json</c> output (both versions): one event per line, <c>{"type", "timestamp",
/// "sessionID", "part"}</c>. <c>text</c> (<c>part.text</c>, <c>part.messageID</c>): the agent's words, the last
/// message's are the final text; <c>tool_use</c> (<c>part.tool</c>, <c>part.state.input</c>): progress;
/// <c>step_finish</c> (<c>part.tokens</c> = <c>input</c>, <c>output</c>, <c>reasoning</c>, <c>cache.read</c>,
/// <c>cache.write</c>; <c>part.cost</c> in US dollars): one model call's usage, where <c>input</c> leaves out the
/// cached tokens and <c>output</c> the reasoning; <c>error</c> (<c>error.message</c>, or version 1's
/// <c>error.data.message</c>): the run failed. The model isn't in the output: it is the one in OpenCode's settings
/// (<c>provider/model</c>), unknown when they name none.
/// </summary>
/// <param name="configuredModel">The <c>model</c> in OpenCode's settings, if any.</param>
/// <param name="knownLocal">The caller independently guarantees all inference is local; primary endpoint readiness alone is insufficient. The runner asserts this only for the pinned isolated local runtime.</param>
public sealed class OpenCodeOutputParser(string? configuredModel, bool knownLocal = false) : IAgentOutputParser
{
    private long _in;
    private long _cacheRead;
    private long _cacheWrite;
    private long _out;
    private decimal _cost;
    private string? _messageId;
    private string _message = "";
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
            var part = Prop(root, "part");
            switch (ClaudeOutputParser.Str(root, "type"))
            {
                case "text" when ClaudeOutputParser.Str(part, "text") is { } text:
                    var id = ClaudeOutputParser.Str(part, "messageID");
                    if (id != _messageId)
                    {
                        _messageId = id;
                        _message = "";
                    }
                    _message += text;
                    return AgentText.OneLine(text);
                case "tool_use":
                    return ToolNote(part);
                case "step_finish" when Prop(part, "tokens") is { ValueKind: JsonValueKind.Object } tokens:
                    var cache = Prop(tokens, "cache");
                    _in += ClaudeOutputParser.Long(tokens, "input");
                    _cacheRead += ClaudeOutputParser.Long(cache, "read");
                    _cacheWrite += ClaudeOutputParser.Long(cache, "write");
                    _out += ClaudeOutputParser.Long(tokens, "output") + ClaudeOutputParser.Long(tokens, "reasoning");
                    _cost += ClaudeOutputParser.Dec(part, "cost") ?? 0;
                    return null;
                case "error":
                    _failed = true;
                    var error = Prop(root, "error");
                    _error = ClaudeOutputParser.Str(error, "message")
                        ?? ClaudeOutputParser.Str(Prop(error, "data"), "message")
                        ?? ClaudeOutputParser.Str(error, "name")
                        ?? "OpenCode reported an error";
                    return null;
                default:
                    return null;
            }
        }
    }

    private static string? ToolNote(JsonElement part)
    {
        if (ClaudeOutputParser.Str(part, "tool") is not { } tool)
        {
            return null;
        }
        return ClaudeOutputParser.Str(Prop(Prop(part, "state"), "input"), "command") is { Length: > 0 } command
            ? AgentText.OneLine("Running " + command)
            : AgentText.OneLine(tool);
    }

    /// <summary>The member <paramref name="name"/> of an object, or an undefined element.</summary>
    internal static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) ? value : default;

    /// <inheritdoc />
    public Usage? UsageSoFar => _in + _cacheRead + _cacheWrite + _out > 0 ? Complete().Usage : null;

    /// <inheritdoc />
    public AgentOutcome Complete()
    {
        var slash = configuredModel?.IndexOf('/') ?? -1;
        return new AgentOutcome
        {
            FinalText = _message.Trim() is { Length: > 0 } message ? message : _lastText,
            ReportedError = _failed,
            ErrorText = _error,
            Usage = new Usage
            {
                AgentKind = AgentKind.OpenCode,
                Vendor = slash > 0 ? configuredModel![..slash] : null,
                Model = slash > 0 ? configuredModel![(slash + 1)..] : configuredModel,
                TokensIn = _in + _cacheRead + _cacheWrite,
                CachedTokensIn = _cacheRead,
                CacheWriteTokensIn = _cacheWrite,
                TokensOut = _out,
                // A provider paid by subscription reports nothing (0): the price list then estimates it.
                EstimatedCostUsd = _cost > 0 ? _cost : knownLocal && _in + _cacheRead + _cacheWrite + _out > 0 ? 0 : null,
            },
        };
    }
}
