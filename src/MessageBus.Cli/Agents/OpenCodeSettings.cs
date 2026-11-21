using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MessageBus.Cli.Runner.Agents;

namespace MessageBus.Cli.Agents;

/// <summary>A bounded snapshot of runner-owned OpenCode settings, never repository settings.</summary>
internal sealed class OpenCodeSettings
{
    internal const int MaxBytes = 1024 * 1024;
    private readonly JsonObject _root;
    private readonly Dictionary<string, OpenCodeSelection> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Shape, string Settings, JsonObject Options, string? Package)> _overlays = new(StringComparer.Ordinal);
    internal string? Problem { get; }
    internal AgentModels Inventory { get; }
    // Readiness needs only immutable endpoint metadata, not a cloned launch overlay for every model.
    internal IEnumerable<OpenCodeSelection> LocalCandidates => Inventory.Models
        .Where(_models.ContainsKey).Select(id => _models[id]).Where(m => m.Endpoint is not null);

    private OpenCodeSettings(JsonObject root, string? problem = null)
    {
        _root = root;
        Problem = problem;
        // Do not guess conversion precedence when both generations configure providers.
        if (root["provider"] is not null && root["providers"] is not null) throw new JsonException();
        var configJson = root.ToJsonString();
        foreach (var shape in new[] { ("provider", "options"), ("providers", "settings") })
        {
            if (root[shape.Item1] is not JsonObject providers) continue;
            foreach (var (providerId, node) in providers.OrderByDescending(p => Str(root["model"])?.StartsWith(p.Key + "/", StringComparison.Ordinal) == true))
            {
                if (node is not JsonObject provider || Disabled(provider) || Listed(root["disabled_providers"], providerId)
                    || root["enabled_providers"] is JsonArray enabled && !Listed(enabled, providerId)) continue;
                var models = provider["models"] as JsonObject ?? new JsonObject();
                var defaultId = Str(root["model"]);
                if (defaultId is not null && defaultId.StartsWith(providerId + "/", StringComparison.Ordinal)
                    && !models.ContainsKey(defaultId[(providerId.Length + 1)..]))
                {
                    models = (JsonObject)models.DeepClone();
                    models[defaultId[(providerId.Length + 1)..]] = new JsonObject();
                }
                foreach (var (modelId, modelNode) in models.OrderByDescending(m => providerId + "/" + m.Key == defaultId))
                {
                    if (_models.Count == 100) break;
                    var id = providerId + "/" + modelId;
                    if (!ValidId(id) || modelNode is not JsonObject model || Disabled(model)
                        || Str(model["type"]) is { } type && type != "chat") continue;
                    var options = (provider[shape.Item2]?.DeepClone() as JsonObject) ?? new JsonObject();
                    if (model[shape.Item2] is JsonObject overrides) Merge(options, overrides);
                    var url = Str(options["baseURL"]);
                    var endpoint = Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsLoopback(uri.Host)
                        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
                        && uri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ? uri : null;
                    // A compatible runtime and a trusted configured endpoint only make this a candidate.
                    // Native LM Studio loaded-model evidence establishes primary readiness only.
                    var package = Str(model["package"]) ?? Str(model["npm"]) ?? Str(provider["package"]) ?? Str(provider["npm"]);
                    var compatible = package is "@ai-sdk/openai-compatible" or "@opencode/ai/providers/openai-compatible"
                        or "@opencode/ai/providers/lmstudio";
                    if (_models.TryAdd(id, new(id, Str(model["modelID"]) ?? Str(model["id"]) ?? modelId,
                        compatible ? endpoint : null, Str(options["apiKey"]), configJson)))
                        _overlays[id] = (shape.Item1, shape.Item2, options, package);
                }
            }
        }
        var configured = Str(root["model"]);
        if (configured is not null && !ValidId(configured)) configured = null;
        if (configured is not null && SelectionProblem(configured) is not null) configured = null;
        var ids = _models.Keys.ToList();
        if (configured is not null && !ids.Contains(configured, StringComparer.Ordinal)) ids.Insert(0, configured);
        Inventory = new(configured, ids.Take(100).ToArray(), true);
    }

    internal OpenCodeSelection Select(string? requested)
    {
        var id = requested ?? Inventory.Default;
        if (id is not null && _models.TryGetValue(id, out var model))
        {
            var overlay = _overlays[id];
            var snapshot = (JsonObject)_root.DeepClone();
            var slash = id.IndexOf('/');
            var provider = snapshot[overlay.Shape]![id[..slash]]!.AsObject();
            var models = provider["models"] as JsonObject ?? new JsonObject();
            if (provider["models"] is null) provider["models"] = models;
            var entry = models[id[(slash + 1)..]] as JsonObject ?? new JsonObject();
            if (models[id[(slash + 1)..]] is null) models[id[(slash + 1)..]] = entry;
            // Pin model-level settings, alias and runtime too: repository overrides must not redirect a checked endpoint.
            entry[overlay.Settings] = overlay.Options.DeepClone();
            entry[overlay.Shape == "providers" ? "modelID" : "id"] = model.ApiId;
            if (overlay.Package is { } package && overlay.Shape == "providers") entry["package"] = package;
            snapshot["model"] = id;
            return model with { ConfigJson = snapshot.ToJsonString() };
        }
        return new(id, id is not null && id.Contains('/') ? id[(id.IndexOf('/') + 1)..] : id,
            null, null, _root.ToJsonString());
    }

    internal string? SelectionProblem(string? requested)
    {
        var id = requested ?? Str(_root["model"]);
        if (id is null) return null;
        if (!ValidId(id)) return "OpenCode selected model ID is unsupported. Use a configured provider/model ID of at most 100 characters.";
        var slash = id.IndexOf('/');
        var provider = id[..slash];
        var model = id[(slash + 1)..];
        var disabled = Listed(_root["disabled_providers"], provider)
            || _root["enabled_providers"] is JsonArray enabled && !Listed(enabled, provider)
            || new[] { "provider", "providers" }.Any(shape => _root[shape] is JsonObject providers && providers[provider] is JsonObject p
                && (Disabled(p) || p["models"] is JsonObject models && models[model] is JsonObject m && (Disabled(m)
                    || Str(m["type"]) is { } type && type != "chat")));
        if (disabled) return "OpenCode selected model or provider is disabled. Enable it in the runner's settings, then mbus connect --rescan.";
        if (!_models.ContainsKey(id))
        {
            foreach (var shape in new[] { ("provider", "options"), ("providers", "settings") })
            {
                if (_root[shape.Item1] is not JsonObject providers || providers[provider] is not JsonObject p) continue;
                if (p["models"] is JsonObject models && models.ContainsKey(model)
                    || Uri.TryCreate(Str((p[shape.Item2] as JsonObject)?["baseURL"]), UriKind.Absolute, out var uri) && IsLoopback(uri.Host))
                    return "OpenCode selected model is outside the bounded configured catalog. Add it to the runner's model map and keep at most 100 models, then mbus connect --rescan.";
            }
        }
        return null;
    }

    internal string? LocalRunProblem(string? requested, string? version)
    {
        var id = requested ?? Inventory.Default;
        if (id is null || !_overlays.TryGetValue(id, out var overlay)) return null;
        var loopback = Uri.TryCreate(Str(overlay.Options["baseURL"]), UriKind.Absolute, out var uri) && IsLoopback(uri.Host);
        if (!loopback && overlay.Package != "@opencode/ai/providers/lmstudio") return null;

        if (version == OpenCodeLocalRuntime.Version && overlay.Shape == "provider"
            && overlay.Package == "@ai-sdk/openai-compatible" && _models[id].Endpoint is not null) return null;

        var generation = version is "1.19.5" or "2.3.0" ? $"OpenCode {version}" : "This OpenCode version";
        return $"{generation}: local-only inference is not verified. Run settings cannot guarantee restriction of auxiliary, compaction, agent/subagent and fallback models, including repository and managed/system overrides. Local selections are refused before checkout/setup; use the isolated OpenCode 1.19.5 openai-compatible baseline or an explicit cloud selection. Personal settings are unchanged.";
    }

    internal static OpenCodeSettings Read(Func<string, string?> env, string home, string? version = null)
    {
        try
        {
            var root = new JsonObject();
            var global = Path.Combine(env("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode");
            AddDirectory(global);
            if (env("OPENCODE_CONFIG") is { Length: > 0 } file) AddFile(file, required: true);
            if (env("OPENCODE_CONFIG_DIR") is { Length: > 0 } dir && Path.GetFullPath(dir) != Path.GetFullPath(global)) AddDirectory(dir);
            if (env(OpenCodeAdapter.ConfigContentVariable) is { Length: > 0 } content) Merge(root, Parse(content, env));
            if (Encoding.UTF8.GetByteCount(root.ToJsonString()) > MaxBytes) throw new JsonException();
            if (version?.StartsWith("1.", StringComparison.Ordinal) == true && root["providers"] is not null) throw new JsonException();
            return new(root);

            void AddDirectory(string dir)
            {
                AddFile(Path.Combine(dir, "opencode.json"));
                AddFile(Path.Combine(dir, "opencode.jsonc"));
            }
            void AddFile(string path, bool required = false)
            {
                if (!File.Exists(path))
                {
                    if (required) throw new IOException();
                    return;
                }
                using var stream = File.OpenRead(path);
                if (stream.Length > MaxBytes) throw new JsonException();
                using var reader = new StreamReader(stream);
                var chars = new char[MaxBytes + 1];
                var count = reader.ReadBlock(chars, 0, chars.Length);
                if (count > MaxBytes) throw new JsonException();
                var text = new string(chars, 0, count);
                Merge(root, Parse(text, env));
                if (Encoding.UTF8.GetByteCount(root.ToJsonString()) > MaxBytes) throw new JsonException();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return new(new JsonObject(), "OpenCode settings are unreadable, oversized or unsupported. Fix the runner's JSON/JSONC configuration, then mbus connect --rescan.");
        }
    }

    internal static OpenCodeSettings Parse(string content) => new(Parse(content, _ => null));

    private static JsonObject Parse(string content, Func<string, string?> env)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxBytes) throw new JsonException();
        var root = JsonNode.Parse(content, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new JsonException();
        Expand(root);
        return root;

        void Expand(JsonNode node)
        {
            if (node is JsonObject obj)
                foreach (var key in obj.Select(p => p.Key).ToArray()) obj[key] = ExpandValue(obj[key]);
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++) array[i] = ExpandValue(array[i]);
        }
        JsonNode? ExpandValue(JsonNode? node)
        {
            if (Str(node) is { } value)
            {
                if (value.Contains("{file:", StringComparison.Ordinal)) throw new JsonException();
                return JsonValue.Create(Regex.Replace(value, @"\{env:([A-Za-z_][A-Za-z0-9_]*)\}", m => env(m.Groups[1].Value) ?? throw new JsonException()));
            }
            if (node is not null) Expand(node);
            return node?.DeepClone();
        }
    }

    internal static void Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay)
        {
            if (value is JsonObject o && target[key] is JsonObject existing) Merge(existing, o);
            else target[key] = value?.DeepClone();
        }
    }

    internal static bool IsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    private static bool ValidId(string id) => id.Length is > 2 and <= 100 && id[0] != '-' && id.IndexOf('/') > 0 && !id.EndsWith('/')
        && id.All(c => char.IsAsciiLetterOrDigit(c) || "-_.:/[]@".Contains(c));
    private static bool Disabled(JsonObject node) => node["disabled"] is JsonValue v && v.TryGetValue<bool>(out var disabled) && disabled
        || node["enabled"] is JsonValue v2 && v2.TryGetValue<bool>(out var enabled) && !enabled;
    private static bool Listed(JsonNode? node, string id) => node is JsonArray a && a.Any(v => Str(v) == id);
    internal static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}

/// <summary>One immutable selection, passed to readiness, launch, proxy and usage.</summary>
internal sealed record OpenCodeSelection(string? Id, string? ApiId, Uri? Endpoint, string? ApiKey, string ConfigJson, bool ConfinedLocal = false);
