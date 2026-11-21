using System.Text.Json.Nodes;

namespace MessageBus.Cli.Agents;

/// <summary>Source-pinned OpenCode 1 confinement. See docs/opencode-local-runtime.md.</summary>
internal static class OpenCodeLocalRuntime
{
    internal const string Version = "1.19.5";
    internal const string Model = "mbus-local/selected";

    internal static string? ManagedProblem(Func<string, string?> env)
    {
        try { return InspectManaged(env); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "OpenCode local-only inference is not verified: managed/system settings could not be inspected.";
        }
    }

    private static string? InspectManaged(Func<string, string?> env)
    {
        // Managed settings load AFTER inline content. Never redirect or override an administrator's scope.
        var system = OperatingSystem.IsWindows()
            ? @"C:\ProgramData\opencode"
            : OperatingSystem.IsMacOS() ? "/Library/Application Support/opencode" : "/etc/opencode";
        var paths = new List<string> { system };
        if (OperatingSystem.IsWindows())
            foreach (var programData in new[] { env("ProgramData"), Environment.GetEnvironmentVariable("ProgramData") })
                if (programData is { Length: > 0 }) paths.Add(Path.Combine(programData, "opencode"));
        if (env("OPENCODE_TEST_MANAGED_CONFIG_DIR") is { Length: > 0 } custom) paths.Add(custom);
        if (OperatingSystem.IsMacOS())
        {
            paths.Add("/Library/Managed Preferences/ai.opencode.managed.plist");
            paths.Add(Path.Combine("/Library/Managed Preferences", Environment.UserName, "ai.opencode.managed.plist"));
        }
        foreach (var path in paths)
        {
            if (!Path.IsPathFullyQualified(path)) return "OpenCode local-only inference is not verified with relative managed/system settings paths.";
            try
            {
                _ = File.GetAttributes(path);
                return "OpenCode local-only inference is not verified with managed/system settings present. Use an explicit cloud selection or an unmanaged host.";
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return "OpenCode local-only inference is not verified: managed/system settings could not be inspected.";
            }
        }
        return null;
    }

    internal static OpenCodeSelection Confine(OpenCodeSelection selection)
    {
        // Construct from endpoint evidence only. No user plugins, model overrides, agents, fallbacks,
        // cloud credentials, variants or model SDK options are copied into the runtime config.
        var agents = new JsonObject();
        foreach (var name in new[] { "build", "plan", "general", "explore", "title", "summary", "compaction" })
            agents[name] = new JsonObject { ["model"] = Model };
        var config = new JsonObject
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["model"] = Model,
            ["small_model"] = Model,
            ["enabled_providers"] = new JsonArray("mbus-local"),
            ["share"] = "disabled",
            ["agent"] = agents,
            ["provider"] = new JsonObject
            {
                ["mbus-local"] = new JsonObject
                {
                    ["npm"] = "@ai-sdk/openai-compatible",
                    ["whitelist"] = new JsonArray("selected"),
                    ["options"] = new JsonObject { ["baseURL"] = selection.Endpoint!.AbsoluteUri, ["apiKey"] = selection.ApiKey ?? "local" },
                    ["models"] = new JsonObject
                    {
                        ["selected"] = new JsonObject
                        {
                            ["id"] = selection.ApiId,
                            ["tool_call"] = true,
                            ["limit"] = new JsonObject { ["context"] = 8192, ["output"] = 2048 },
                            ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0 },
                        },
                    },
                },
            },
        };
        return selection with { ConfigJson = config.ToJsonString(), ConfinedLocal = true };
    }

    internal static Dictionary<string, string> EnvironmentFor(string runDirectory)
    {
        // A fresh directory per launch also prevents stale account databases/configs on resumed deliveries.
        var root = Path.Combine(Path.GetFullPath(runDirectory), "opencode-local", Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        var temporary = Path.Combine(root, "tmp");
        Directory.CreateDirectory(temporary);
        var catalog = Path.Combine(root, "models.json");
        File.WriteAllText(catalog, "{}");
        return new Dictionary<string, string>
        {
            ["HOME"] = home, ["USERPROFILE"] = home, ["OPENCODE_TEST_HOME"] = home,
            ["TMPDIR"] = temporary, ["TMP"] = temporary, ["TEMP"] = temporary,
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
            ["XDG_STATE_HOME"] = Path.Combine(root, "state"),
            ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            ["OPENCODE_CONFIG_DIR"] = Path.Combine(root, "config", "opencode"),
            ["OPENCODE_CONFIG"] = "", ["OPENCODE_AUTH_CONTENT"] = "{}",
            ["OPENCODE_DB"] = Path.Combine(root, "accounts.db"),
            ["OPENCODE_MODELS_PATH"] = catalog,
            ["OPENCODE_DISABLE_PROJECT_CONFIG"] = "true",
            ["OPENCODE_PURE"] = "true", ["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "true",
            ["OPENCODE_DISABLE_MODELS_FETCH"] = "true", ["OPENCODE_DISABLE_AUTOUPDATE"] = "true",
            ["OPENCODE_AUTO_SHARE"] = "false",
            ["OPENCODE_EXPERIMENTAL"] = "false", ["OPENCODE_EXPERIMENTAL_NATIVE_LLM"] = "false",
            ["OPENCODE_ENABLE_PARALLEL"] = "false", ["OPENCODE_EXPERIMENTAL_PARALLEL"] = "false",
            ["OPENCODE_EXPERIMENTAL_WORKSPACES"] = "false", ["OPENCODE_WORKSPACE_ID"] = "",
        };
    }
}
