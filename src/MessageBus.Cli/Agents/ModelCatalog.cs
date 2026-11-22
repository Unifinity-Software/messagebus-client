using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Connect;
using MessageBus.Contracts.Machines;

namespace MessageBus.Cli.Agents;

/// <summary>What an agent says about its models (the original issue): its default (null when its settings don't say) and the models it lists.</summary>
/// <param name="Default">The model it runs when a task names none, as its settings or environment say.</param>
/// <param name="Models">The models it can run, as it lists them (the default included).</param>
/// <param name="Partial">It couldn't list them all: only what is known is reported.</param>
internal sealed record AgentModels(string? Default, IReadOnlyList<string> Models, bool Partial)
{
    /// <summary>Nothing known: no default and no list.</summary>
    public static AgentModels Unknown { get; } = new(null, [], true);

    /// <summary>Only the default (when there is one): the agent couldn't list its models.</summary>
    public static AgentModels OnlyDefault(string? model) => new(model, model is null ? [] : [model], true);
}

/// <summary>
/// The models each agent CLI exposes (the original issue), read without a model call and never guessed:
/// <list type="bullet">
/// <item>Claude Code 2.1.x has no command that lists models. It keeps the catalog it fetched for its <c>/model</c> picker in
/// <c>~/.claude/cache/model-catalog/*.json</c> (<c>catalog.surface</c> <c>cc</c> is the CLI), with each model's id, the oldest
/// Claude Code that runs it, and what each alias (<c>opus</c>, <c>fable</c>) points at. Its default is <c>ANTHROPIC_MODEL</c>,
/// else <c>model</c> in <c>~/.claude/settings.json</c>, with an alias resolved through the catalog.</item>
/// <item>Codex 0.169 prints its model catalog with <c>codex debug models</c> (<c>{"models":[{"slug", "visibility", "priority"}]}</c>,
/// the models with <c>visibility: list</c> being the ones its picker offers); it caches the same in <c>~/.codex/models_cache.json</c>.
/// Its default is <c>model</c> at the top of <c>config.toml</c>.</item>
/// <item>OpenCode: the runner's configured models (a partial catalog), without inference. Copilot CLI is unknown.</item>
/// </list>
/// </summary>
internal static class ModelCatalog
{
    /// <summary>How long <c>codex debug models</c> or <c>agy models</c> may take before falling back.</summary>
    internal static readonly TimeSpan CodexListTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Largest catalog file read; anything bigger isn't one.</summary>
    private const long MaxCatalogBytes = 4 * 1024 * 1024;

    /// <summary>
    /// What <paramref name="kind"/> on this machine says about its models; <see cref="AgentModels.Unknown"/> when reading them
    /// fails in any way, so a broken catalog never stops the scan.
    /// </summary>
    public static async Task<AgentModels> ReadAsync(AgentKind kind, string executable, string? version, AgentLocations locations,
        SignInEnvironment env, CancellationToken cancellationToken)
    {
        try
        {
            return kind switch
            {
                AgentKind.ClaudeCode => Claude(locations, env, version),
                AgentKind.Codex => await CodexAsync(executable, locations, env, cancellationToken).ConfigureAwait(false),
                AgentKind.OpenCode => OpenCodeSettings.Read(env.Get, locations.HomeDirectory, version).Inventory,
                AgentKind.Antigravity => await AntigravityAsync(executable, locations, env, cancellationToken).ConfigureAwait(false),
                _ => AgentModels.Unknown,
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return AgentModels.Unknown;
        }
    }

    // ── Claude Code ─────────────────────────────────────────────────────────────

    private static AgentModels Claude(AgentLocations locations, SignInEnvironment env, string? version)
    {
        // An empty ANTHROPIC_MODEL is unset.
        var configured = (env.Get("ANTHROPIC_MODEL") is { } fromEnv && !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv.Trim() : null)
                         ?? ReadString(Path.Combine(locations.ClaudeDirectory, "settings.json"), "model");
        var catalog = NewestClaudeCatalog(Path.Combine(locations.ClaudeDirectory, "cache", "model-catalog"), version);
        var model = ResolveClaudeAlias(configured, catalog?.Aliases);
        if (catalog is null) return AgentModels.OnlyDefault(model);
        var models = catalog.Models.ToList();
        if (model is not null && !models.Contains(model, StringComparer.OrdinalIgnoreCase)) models.Insert(0, model);
        return new AgentModels(model, models, Partial: false);
    }

    /// <summary>Claude Code's models and aliases, from one cached catalog file.</summary>
    internal sealed record ClaudeCatalog(DateTimeOffset FetchedAt, IReadOnlyList<string> Models, IReadOnlyDictionary<string, string> Aliases);

    /// <summary>
    /// The newest catalog for the Claude Code CLI (surface <c>cc</c>, files <c>*-cc.json</c>) in <paramref name="folder"/>,
    /// or null. The cache is Claude Code's undocumented internal file, not an interface: any change to it reads as no
    /// catalog, and the agent reports only its default.
    /// </summary>
    private static ClaudeCatalog? NewestClaudeCatalog(string folder, string? version)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            return Directory.EnumerateFiles(folder, "*-cc.json")
                .Select(file => ParseClaudeCatalog(SafeRead(file), version))
                .OfType<ClaudeCatalog>()
                .MaxBy(c => c.FetchedAt);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// One cached catalog: the ids of the models <paramref name="version"/> of Claude Code can run, and what each alias
    /// points at; null when it isn't the CLI's catalog or can't be read.
    /// </summary>
    internal static ClaudeCatalog? ParseClaudeCatalog(string? json, string? version)
    {
        try
        {
            return ReadClaudeCatalog(json, version);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            return null; // duplicate keys, a fetchedAt out of range, a value of the wrong kind
        }
    }

    private static ClaudeCatalog? ReadClaudeCatalog(string? json, string? version)
    {
        if (TryParse(json) is not JsonObject root || root["catalog"] is not JsonObject catalog) return null;
        if (Str(catalog["surface"]) is not "cc" || catalog["config"] is not JsonObject config) return null;
        if (config["models"] is not JsonArray list) return null;
        var models = new List<string>();
        foreach (var item in list.OfType<JsonObject>())
        {
            if (Str(item["id"]) is not { Length: > 0 } id) continue;
            if (Str(item["min_claude_code_version"]) is { } min && !AtLeast(version, min)) continue;
            if (!models.Contains(id, StringComparer.OrdinalIgnoreCase)) models.Add(id);
        }
        if (models.Count == 0) return null;
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config["provider_alias_targets"] is JsonObject targets)
            foreach (var (alias, target) in targets)
                if (target is JsonObject t && Str(t["default"]) is { Length: > 0 } to)
                    aliases[alias] = to;
        var fetched = root["fetchedAt"] is JsonValue v && v.TryGetValue<long>(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.MinValue;
        return new ClaudeCatalog(fetched, models, aliases);
    }

    /// <summary><c>opus</c> as the model it points at (<c>claude-opus-5-5</c>), keeping a <c>[1m]</c> suffix; a full name as it is.</summary>
    internal static string? ResolveClaudeAlias(string? configured, IReadOnlyDictionary<string, string>? aliases)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        var value = configured.Trim();
        var bracket = value.IndexOf('[');
        var (name, suffix) = bracket > 0 ? (value[..bracket], value[bracket..]) : (value, "");
        return aliases is not null && aliases.TryGetValue(name, out var target) ? target + suffix : value;
    }

    // ── Codex ───────────────────────────────────────────────────────────────────

    private static async Task<AgentModels> CodexAsync(string executable, AgentLocations locations, SignInEnvironment env, CancellationToken cancellationToken)
    {
        var configured = Runner.Agents.CodexAdapter.ReadConfiguredModel(locations.CodexHome);
        var listed = await CommandRunner.RunAsync(executable, ["debug", "models"], cancellationToken, CodexListTimeout, environment: env.Added).ConfigureAwait(false) is { ExitCode: 0 } run
            ? ParseCodexModels(run.Output)
            : null;
        // An older Codex without the command: the catalog it cached last.
        listed ??= ParseCodexModels(SafeRead(Path.Combine(locations.CodexHome, "models_cache.json")));
        if (listed is null) return AgentModels.OnlyDefault(configured);
        var models = listed.ToList();
        if (configured is not null && !models.Contains(configured, StringComparer.OrdinalIgnoreCase)) models.Insert(0, configured);
        return new AgentModels(configured, models, Partial: false);
    }

    /// <summary>
    /// The models Codex's catalog offers (<c>visibility</c> <c>list</c>, or none given), in its order (<c>priority</c>);
    /// null when <paramref name="json"/> isn't a catalog.
    /// </summary>
    internal static IReadOnlyList<string>? ParseCodexModels(string? json)
    {
        if (TryParse(json) is not JsonObject root || root["models"] is not JsonArray list) return null;
        var models = list.OfType<JsonObject>()
            .Where(m => Str(m["slug"]) is { Length: > 0 } && Str(m["visibility"]) is null or "list")
            .OrderBy(m => m["priority"] is JsonValue p && p.TryGetValue<int>(out var n) ? n : int.MaxValue)
            .Select(m => Str(m["slug"])!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return models.Count == 0 ? null : models;
    }

    // ── Google Antigravity CLI ──────────────────────────────────────────────────

    private static async Task<AgentModels> AntigravityAsync(string executable, AgentLocations locations, SignInEnvironment env, CancellationToken cancellationToken)
    {
        var configured = ReadString(locations.AntigravitySettings, "model");
        var listed = await CommandRunner.RunAsync(executable, ["models"], cancellationToken, CodexListTimeout, environment: env.Added).ConfigureAwait(false) is { ExitCode: 0 } run
            ? ParseAntigravityModels(run.Output)
            : null;
        if (listed is null)
        {
            // Antigravity stores a display label (e.g. "Gemini 4 Neon (High)") in settings.json, whereas --model expects a slug.
            // Without `agy models` to resolve labels, only return configured when it already looks like a slug (no whitespace).
            var slug = configured is { Length: > 0 } && !configured.Any(char.IsWhiteSpace) ? configured : null;
            return AgentModels.OnlyDefault(slug);
        }
        var model = ResolveAntigravityDefault(configured, listed.Value.Labels);
        var models = listed.Value.Models.ToList();
        if (model is not null && !models.Contains(model, StringComparer.OrdinalIgnoreCase)) models.Insert(0, model);
        return new AgentModels(model, models, Partial: false);
    }

    /// <summary>
    /// Parses <c>agy models</c> tab-separated output (<c>slug\tDisplay Name</c>), ignoring status lines;
    /// null when no model rows are present.
    /// </summary>
    internal static (IReadOnlyList<string> Models, IReadOnlyDictionary<string, string> Labels)? ParseAntigravityModels(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var models = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var slug = line[..tab].Trim();
            var display = line[(tab + 1)..].Trim();
            if (slug.Length == 0 || slug.Any(char.IsWhiteSpace)) continue;
            if (seen.Add(slug)) models.Add(slug);
            if (display.Length > 0) labels.TryAdd(display, slug);
        }
        return models.Count == 0 ? null : (models, labels);
    }

    /// <summary>Resolves Antigravity's saved display label (e.g. <c>Gemini 4 Neon (High)</c>) to its model slug.</summary>
    internal static string? ResolveAntigravityDefault(string? configured, IReadOnlyDictionary<string, string>? labels)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        var value = configured.Trim();
        return labels is not null && labels.TryGetValue(value, out var slug) ? slug : value;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Whether <paramref name="version"/> is at least <paramref name="min"/> (an unknown version is taken to be new enough).</summary>
    private static bool AtLeast(string? version, string min) =>
        !Version.TryParse(Numeric(version), out var have) || !Version.TryParse(Numeric(min), out var need) || have >= need;

    private static string? Numeric(string? version) =>
        version is null ? null : new string([.. version.TakeWhile(c => char.IsDigit(c) || c == '.')]);

    private static string? ReadString(string file, string property) =>
        JsonConfigFile.TryRead(file)?[property] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static JsonNode? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? SafeRead(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists && info.Length <= MaxCatalogBytes ? File.ReadAllText(file) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
