using System.Text.Json;
using System.Text.RegularExpressions;
using MessageBus.Cli.Agents;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;

namespace MessageBus.Cli.Runner.Agents;

/// <summary>
/// Codex CLI, headless: <c>codex exec --json --skip-git-repo-check --sandbox … -</c> with the prompt
/// on stdin. The permission profile picks Codex's own sandbox: <c>workspace-write</c> when the run may
/// edit files, else <c>read-only</c>; Windows roles may opt into the runner's isolation instead.
/// <para>
/// Each turn re-sends the instructions and tool list, so the run leaves out what a one-task worker doesn't
/// use (the original issue): every MCP server except mbus and the role's named tools from this computer's own settings, including servers in a trusted repository's <c>.codex/config.toml</c>
/// (the original issue; <c>-c mcp_servers.NAME.enabled=false</c>), plugins
/// (their skills, tools and MCP servers) and ChatGPT apps (<c>-c features.plugins=false</c>,
/// <c>features.apps=false</c>; through <c>-c</c> because <c>--disable</c> stops a Codex that doesn't know
/// the feature).
/// </para>
/// </summary>
/// <param name="model">
/// The model in Codex's settings, for usage reports when the run itself doesn't say (see <see cref="CodexOutputParser"/>).
/// </param>
/// <param name="mbusServerConfigured">
/// Codex's settings have the <c>[mcp_servers.mbus]</c> server (<c>mbus connect</c>). Codex starts MCP servers with
/// only a few of its own variables, so the run's MBUS_* variables are given to that server with <c>-c</c>. Without
/// the table the runner defines the whole server with <c>-c</c> (command, args and env; the original issue): env alone, for a
/// server with no command, stops Codex from starting.
/// </param>
/// <param name="mbus">How Codex starts the local mbus MCP server when its settings don't say; this mbus by default.</param>
/// <param name="codexHome">Codex's home folder (<c>CODEX_HOME</c>, else <c>~/.codex</c>), where it keeps each session's log.</param>
/// <param name="getEnvironmentVariable">The runner's environment, for <see cref="SandboxOverrideVariable"/>; this process's by default.</param>
/// <param name="sandboxOverride">The machine owner's command-line or environment override.</param>
public sealed partial class CodexAdapter(
    string? model,
    bool mbusServerConfigured = false,
    Mcp.McpLaunchCommand? mbus = null,
    string? codexHome = null,
    Func<string, string?>? getEnvironmentVariable = null,
    string? sandboxOverride = null) : IAgentAdapter
{
    /// <summary>
    /// the original issue: set to <c>unelevated</c>, Codex on Windows uses its restricted-token sandbox even where Windows has its own
    /// isolation (MXC); <c>always</c> requires Codex's sandbox regardless of the role's opt-in.
    /// There is no value for Codex's elevated sandbox: that one needs admin rights.
    /// </summary>
    public const string SandboxOverrideVariable = "MBUS_RUNNER_CODEX_SANDBOX";

    internal const string DotnetPromptNote = "Windows Codex sandbox: builds must use -m:1; dotnet test can't run in this sandbox because it blocks named pipes. Say so in the result rather than retrying; built test DLLs may be run directly.";
    internal const string DotnetSettleNote = "If tests didn't run: Codex's sandbox on Windows blocks `dotnet test`; a role owner can change this in the role's Run environment (Codex on Windows).";

    internal static string WindowsModeDescription(string? machineOverride) => machineOverride switch
    {
        "always" => "Codex's sandbox is required by this machine; sandboxed .NET tests cannot run on Windows.",
        "unelevated" => "Codex's restricted-token sandbox is required by this machine; sandboxed .NET tests cannot run on Windows.",
        _ => "Codex uses its sandbox by default (sandboxed .NET tests cannot run on Windows); a role's Codex on Windows setting can opt into the same isolation as other agents.",
    };

    /// <inheritdoc />
    public AgentKind Kind => AgentKind.Codex;

    /// <inheritdoc />
    public bool CanResume => true;

    /// <inheritdoc />
    public AgentLaunch BuildLaunch(AgentLaunchContext context) => BuildLaunch(context, windows: null);

    /// <summary>The launch, as on Windows or not (<paramref name="windows"/>; null: this machine).</summary>
    internal AgentLaunch BuildLaunch(AgentLaunchContext context, bool? windows)
    {
        var kept = context.KeepAgentTools ?? [];
        if (CannotKeepTools(kept) is { } missing)
            return new AgentLaunch([], context.Prompt) { CannotStart = missing };
        var userConfig = Path.Combine(codexHome ?? DefaultHome(), "config.toml");
        if (context.UsesCodexEnvironmentKey && kept.Count > 0 && MayForwardKey(userConfig))
            return new AgentLaunch([], context.Prompt)
            {
                CannotStart = "Codex's settings may forward CODEX_API_KEY to a retained tool. Remove the key reference from the tool settings or use saved Codex sign-in for this role.",
            };
        var repositoryServers = RepositoryMcpServers(context.WorkingDirectory, userConfig);
        var collisions = repositoryServers.Where(name => kept.Contains(name, StringComparer.Ordinal)).ToArray();
        if (kept.Count > 0 && (collisions.Length > 0 || RepositorySettingsMayDefineTools(context.WorkingDirectory)))
            return new AgentLaunch([], context.Prompt)
            {
                CannotStart = $"This repository's own Codex settings set up agent tools, which could change {string.Join(", ", kept)}. Runs keep a tool only from this computer's own Codex settings.",
            };
        var onWindows = windows ?? OperatingSystem.IsWindows();
        var profile = context.Profile;
        var writable = profile.CanEditFiles;
        var machineOverride = RunnerOptions.ParseCodexSandbox(sandboxOverride ?? (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(SandboxOverrideVariable));
        var roleModeApplies = onWindows && writable && context.RunnerIsolationEnabled && context.ResumeSessionId is null
            && context.CodexOnWindows == CodexWindowsSandbox.SameAsOtherAgents;
        // the original issue: one sandbox layer. Inside the runner's own sandbox (macOS) Codex runs without its own, which couldn't
        // start inside it and would block builds and commits there. Windows roles may opt into the runner's
        // isolation and command rules instead, but only when that isolation is enabled.
        // A resumed session keeps Codex's own: it works in its own folder, which may be the user's repository.
        var sandbox = !writable ? "read-only"
            : onWindows && machineOverride is not null ? "workspace-write"
            : (context.RunnerSandboxed && context.ResumeSessionId is null) || (roleModeApplies && machineOverride is null) ? "danger-full-access"
            : "workspace-write";
        // the original issue: `codex exec resume <session>` continues an earlier session; it takes no --sandbox, so the mode goes in as its setting.
        List<string> args = context.ResumeSessionId is { } session
            ? ["exec", "resume", session, "--json", "--skip-git-repo-check", "-c", $"sandbox_mode=\"{sandbox}\""]
            : ["exec", "--json", "--skip-git-repo-check", "--sandbox", sandbox];
        // Codex can't limit the network to a host list; with any hosts allowed it gets the whole network
        // (nothing narrows it to the list yet, see the original issue). Elsewhere no hosts, or none at all, keeps Codex's default (off);
        // on Windows any network turns it on (below).
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var prompt = context.Prompt;
        if (onWindows && sandbox != "danger-full-access")
        {
            // the original issue: Codex's Windows sandbox without admin rights: Windows' own isolation (MXC) where it has it, else a
            // restricted token ("unelevated"). Never Codex's "elevated" one (the user's config.toml may choose it), whose
            // setup asks for admin rights and runs again whenever a run's proxy port or Codex's version differs.
            // prefer_mxc is always given, so a config.toml that turns it on doesn't override MBUS_RUNNER_CODEX_SANDBOX.
            var unelevatedOnly = machineOverride == "unelevated";
            args.AddRange(["-c", $"features.prefer_mxc={(unelevatedOnly ? "false" : "true")}", "-c", "windows.sandbox=\"unelevated\""]);
        }
        // the original issue: a read-only run gets no network setting. Codex 0.169.0 has none for read-only (only
        // sandbox_workspace_write.network_access), so its network is off. On Windows, Codex's network-off step only fills
        // in proxy variables that aren't set (windows-sandbox-rs env.rs, apply_no_network_to_env uses or_insert), so the
        // run's mbus proxy variables stay: 'unelevated' runs still reach the proxy, while under MXC the network block may
        // also stop the local proxy. A read-only role that needs network should use workspace-write.
        if (sandbox == "workspace-write")
        {
            // the original issue: on Windows, Codex's network off also blocks the run's network proxy on this computer (package restores
            // fail), so any network at all turns it on. The proxy's allow-list is advisory there: it steers only programs
            // that use the proxy (as for every agent on Windows); nothing stops a program that connects directly.
            if (profile.Network == NetworkAccess.Open || (profile.Network == NetworkAccess.Allowlist && (onWindows || profile.AllowedHosts.Count > 0)))
            {
                args.AddRange(["-c", "sandbox_workspace_write.network_access=true"]);
            }
            if (RunFolders.For(context, windows) is { } run)
            {
                // the original issue: Codex's sandbox keeps every .git under the folder it may write read-only, so a commit fails;
                // the run's clone is its own (never the user's repository), so its .git is opened up, with the run's
                // own package caches (the user's are outside what Codex may write).
                args.AddRange(["-c", $"sandbox_workspace_write.writable_roots=[{string.Join(", ", run.WritableRoots.Select(Connect.CodexTomlConfig.Quote))}]"]);
                foreach (var (name, value) in run.Environment)
                {
                    environment[name] = value;
                }
                if (onWindows)
                {
                    // the original issue: MXC can deny the named pipes used by persistent MSBuild/Roslyn servers.
                    // Compile in the build process instead; this does not change the sandbox's permissions.
                    environment["MSBUILDDISABLENODEREUSE"] = "1";
                    environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
                    environment["UseSharedCompilation"] = "false";
                    // MTP's native dotnet-test handshake still requires a pipe; Git's MSYS shell can fail to initialize.
                    prompt += "\nWindows sandbox: local Git transports and junction creation may be blocked.\n";
                }
            }
        }
        var dotnetLimited = onWindows && writable && sandbox != "danger-full-access" && (profile.CanRunCommands || profile.CanRunTests)
            && TestCommands.IsDotnetRepository(context.WorkingDirectory);
        if (dotnetLimited)
        {
            prompt += "\n" + DotnetPromptNote + "\n";
        }
        args.AddRange(["-c", "features.plugins=false", "-c", "features.apps=false"]);
        if (context.UsesCodexEnvironmentKey)
        {
            // Keep the user's shell filters while replacing the client's key in every command environment.
            args.AddRange(["-c", CodexAuthentication.ShellKeyOverride]);
        }
        foreach (var other in OtherMcpServers(userConfig).Union(repositoryServers, StringComparer.Ordinal))
        {
            if (kept.Contains(other, StringComparer.Ordinal))
            {
                // Verified by hand on 2026-11-16 with Codex 0.169.0 on Windows (workspace-write, unelevated
                // sandbox, plugins/apps and other servers off): kept playwright started its browser, navigated to
                // a local page and saved a screenshot while shell commands stayed sandboxed. MCP servers run
                // outside Codex's command sandbox, so no sandbox change is needed. exec cannot answer a prompt.
                args.AddRange(["-c", $"mcp_servers.{other}.default_tools_approval_mode=\"approve\""]);
            }
            else
            {
                args.AddRange(["-c", $"mcp_servers.{other}.enabled=false"]);
            }
        }
        if (context.McpEnvironment is { } mcpEnvironment)
        {
            var server = "mcp_servers." + Connect.McpServerName.Value;
            // Task tools receive only their run variables, including when a local mbus MCP config exists.
            args.AddRange(["-c", $"{server}.env_vars=[]"]);
            if (!mbusServerConfigured)
            {
                var command = mbus ?? Mcp.McpLaunchCommand.ForCurrentProcess();
                args.AddRange(["-c", $"{server}.command={Connect.CodexTomlConfig.Quote(command.Command)}"]);
                args.AddRange(["-c", $"{server}.args=[{string.Join(", ", command.Args.Select(Connect.CodexTomlConfig.Quote))}]"]);
                args.AddRange(["-c", $"{server}.tool_timeout_sec={Connect.CodexTomlConfig.ToolTimeoutSeconds}"]);
            }
            foreach (var (name, value) in mcpEnvironment.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                if (!CodexAuthentication.IsKeyVariable(name))
                    args.AddRange(["-c", $"{server}.env.{name}={Connect.CodexTomlConfig.Quote(value)}"]);
            }
        }
        if (context.Model is { Length: > 0 } chosen)
        {
            args.AddRange(["--model", chosen]); // the task's or role's model (the original issue), or its cheaper one (the original issue)
        }
        args.Add("-");
        if (kept.Count > 0)
            prompt += $"\nThis run keeps these tools: {string.Join(", ", kept)}. Use them instead of starting programs yourself.\n";
        var toolNote = kept.Count > 0 ? $"Keeps these Codex tools: {string.Join(", ", kept)}." : null;
        var modeNote = roleModeApplies ? machineOverride is null
            ? "Codex on Windows: the role selected the same isolation as other agents; Codex's own sandbox is off."
            : "Codex on Windows: the machine owner requires Codex's sandbox, overriding the role's setting." : null;
        return new AgentLaunch(args, prompt)
        {
            Environment = environment,
            Note = modeNote is null ? toolNote : toolNote is null ? modeNote : modeNote + " " + toolNote,
            SettleNote = dotnetLimited ? DotnetSettleNote : null,
        };
    }

    /// <summary>
    /// What a run in Codex's own sandbox gets to write besides its worktree (the original issue): the clone's <c>.git</c> and the run's
    /// own package caches, with the variables that point NuGet, npm and (on Windows) NuGet's user settings at them.
    /// Only for the runner's own clone in its run folder: never a resumed session's folder, which may be the user's repository.
    /// </summary>
    internal sealed record RunFolders(IReadOnlyList<string> WritableRoots, IReadOnlyDictionary<string, string> Environment)
    {
        /// <summary>The run's caches, under its run folder.</summary>
        public const string CacheFolder = "cache";

        public static RunFolders? For(AgentLaunchContext context, bool? windows = null)
        {
            if (context.ResumeSessionId is not null || context.RunDirectory is not { Length: > 0 } runDirectory)
            {
                return null;
            }
            // the original issue: the run folder, its clone, the clone's .git and the caches must all be real folders inside the run, with no
            // link on the way that could lead out; otherwise nothing is opened up (Codex works as before) and nothing is created.
            var run = Path.GetFullPath(runDirectory);
            var work = Path.GetFullPath(context.WorkingDirectory);
            if (!IsRealDirectory(run) || work.Equals(run, StringComparison.OrdinalIgnoreCase) || !MessageBus.Mcp.RunToolScope.IsInside(run, work) || !Directory.Exists(work))
            {
                return null;
            }
            var git = Path.Combine(work, ".git");
            if (File.Exists(git) || (Directory.Exists(git) && !MessageBus.Mcp.RunToolScope.IsInside(run, git)))
            {
                // A .git file (gitdir:) points at a repository somewhere else.
                return null;
            }
            var cache = Path.Combine(run, CacheFolder);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["NUGET_PACKAGES"] = Path.Combine(cache, "nuget", "packages"),
                ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(cache, "nuget", "http"),
                ["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(cache, "nuget", "plugins"),
                ["npm_config_cache"] = Path.Combine(cache, "npm"),
            };
            if (windows ?? OperatingSystem.IsWindows())
            {
                // Where Codex's Windows sandbox keeps the user's AppData (NuGet's user settings) out of reach, NuGet makes its
                // default ones here instead; the restricted-token sandbox can read AppData but gets the same, so runs match.
                // The repository's own nuget.config still applies.
                environment["APPDATA"] = Path.Combine(cache, "appdata");
            }
            if (File.Exists(cache) || !MessageBus.Mcp.RunToolScope.IsInside(run, cache))
            {
                return null;
            }
            foreach (var folder in environment.Values)
            {
                if (!CreateInside(run, cache, folder))
                {
                    return null;
                }
            }
            List<string> roots = [cache];
            if (Directory.Exists(git))
            {
                roots.Insert(0, git);
            }
            return new RunFolders(roots, environment);
        }

        private static bool IsRealDirectory(string path) =>
            new DirectoryInfo(path) is { Exists: true, LinkTarget: null } info && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);

        /// <summary>Creates <paramref name="folder"/> under <paramref name="cache"/> one level at a time, checking each level is a real folder in the run first.</summary>
        private static bool CreateInside(string run, string cache, string folder)
        {
            var current = cache;
            foreach (var part in Path.GetRelativePath(cache, folder).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Prepend(""))
            {
                current = part.Length == 0 ? cache : Path.Combine(current, part);
                if (File.Exists(current) || !MessageBus.Mcp.RunToolScope.IsInside(run, current))
                {
                    return false;
                }
                if (!Directory.Exists(current))
                {
                    try
                    {
                        Directory.CreateDirectory(current);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return false;
                    }
                }
                if (!IsRealDirectory(current))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <inheritdoc />
    public IAgentOutputParser CreateParser() => new CodexOutputParser(model, codexHome ?? DefaultHome());

    /// <inheritdoc />
    public IAgentOutputParser CreateParser(string? model) => model is { Length: > 0 } ? new CodexOutputParser(model, codexHome ?? DefaultHome()) : CreateParser();

    /// <summary><c>CODEX_HOME</c>, else <c>~/.codex</c>.</summary>
    public static string DefaultHome() =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>The <c>model = "…"</c> at the top of Codex's <c>config.toml</c>, or null.</summary>
    /// <param name="codexHome"><c>CODEX_HOME</c>, else <c>~/.codex</c>.</param>
    public static string? ReadConfiguredModel(string codexHome)
    {
        try
        {
            var file = Path.Combine(codexHome, "config.toml");
            if (!File.Exists(file))
            {
                return null;
            }
            foreach (var line in File.ReadLines(file))
            {
                if (line.TrimStart().StartsWith('['))
                {
                    return null; // past the top-level keys
                }
                if (ModelLine().Match(line) is { Success: true } match)
                {
                    return match.Groups[1].Value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    [GeneratedRegex("""^\s*model\s*=\s*["']([^"']+)["']""")]
    private static partial Regex ModelLine();

    /// <summary>
    /// The MCP servers in Codex's <c>config.toml</c> other than mbus (<c>[mcp_servers.NAME]</c> tables), by names
    /// <c>-c</c> can address (letters, digits, <c>-</c> and <c>_</c>; a server named otherwise stays on). Only servers
    /// that are there: Codex stops on <c>-c</c> for a server it has no command or URL for.
    /// </summary>
    public static IReadOnlyList<string> OtherMcpServers(string configFile)
    {
        var names = new List<string>();
        try
        {
            if (!File.Exists(configFile))
            {
                return names;
            }
            foreach (var line in File.ReadLines(configFile))
            {
                if (ServerHeader().Match(line) is { Success: true } match)
                {
                    var name = match.Groups["name"].Value;
                    if (name != Connect.McpServerName.Value && PlainKey().IsMatch(name) && !names.Contains(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return names;
    }

    /// <summary>Only enabled, unambiguous tables in this computer's own Codex settings may be kept.</summary>
    internal string? CannotKeepTools(IReadOnlyList<string> kept)
    {
        if (kept.Count == 0) return null;
        var config = Path.Combine(codexHome ?? DefaultHome(), "config.toml");
        var names = OtherMcpServers(config);
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            string? server = null;
            if (File.Exists(config))
            {
                foreach (var line in File.ReadLines(config))
                {
                    if (line.TrimStart().StartsWith('['))
                    {
                        var header = ServerHeader().Match(line);
                        server = header.Success ? header.Groups["name"].Value : null;
                    }
                    else if (server is not null && DisabledServer().IsMatch(line)) disabled.Add(server);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            names = [];
        }
        if (kept.All(name => names.Contains(name, StringComparer.Ordinal) && !disabled.Contains(name)
            && names.Count(other => other.Equals(name, StringComparison.OrdinalIgnoreCase)) == 1)) return null;
        var pronoun = kept.Count == 1 ? "it" : "them";
        return $"This role's runs keep {string.Join(", ", kept)}, but Codex on this computer doesn't have {pronoun} set up. Add {pronoun} to Codex's settings here, or run this role on another computer.";
    }

    [GeneratedRegex("""^\s*enabled\s*=\s*false\s*(?:#.*)?$""")]
    private static partial Regex DisabledServer();

    [GeneratedRegex("""^\s*\[\s*mcp_servers\s*\.\s*(?:"(?<name>[^"]+)"|'(?<name>[^']+)'|(?<name>[A-Za-z0-9_-]+))\s*\]\s*(?:#.*)?$""")]
    private static partial Regex ServerHeader();

    private static bool MayForwardKey(string config)
    {
        try
        {
            // A server can request the parent's key as an env var or an HTTP header. Refuse
            // key references (including ones potentially written with TOML Unicode escapes).
            var settings = File.ReadAllText(config);
            return settings.Contains(CodexAuthentication.CodexKey, StringComparison.OrdinalIgnoreCase)
                || settings.Contains(@"\u", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // Fail closed without interpreting TOML or relying on the inferred project trust settings.
    private static bool RepositorySettingsMayDefineTools(string workingDirectory)
    {
        try
        {
            var start = new DirectoryInfo(Path.GetFullPath(workingDirectory));
            DirectoryInfo? root = null;
            for (var d = start; d is not null; d = d.Parent)
            {
                if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                {
                    root = d;
                    break;
                }
            }
            for (var d = start; d is not null; d = d.Parent)
            {
                try
                {
                    var text = File.ReadAllText(Path.Combine(d.FullName, ".codex", "config.toml"));
                    if (text.Contains("mcp", StringComparison.OrdinalIgnoreCase)
                        || text.Contains(@"\u", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (root is null || Normal(d.FullName) == Normal(root.FullName)) break;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// The MCP servers a repository's own Codex settings (<c>.codex/config.toml</c> in the repository root and each
    /// folder down to the working directory) add to a run there, other than mbus (the original issue). Codex reads those files only
    /// for a project trusted in the user's settings (<c>[projects."&lt;path&gt;"] trust_level = "trusted"</c>, for the
    /// working directory or its repository root); for any other folder they add nothing, and a <c>-c</c> for one of
    /// their servers would stop Codex, so none are returned.
    /// </summary>
    /// <param name="workingDirectory">Where the run starts Codex.</param>
    /// <param name="userConfigFile">The user's <c>config.toml</c>, which says which projects are trusted.</param>
    public static IReadOnlyList<string> RepositoryMcpServers(string workingDirectory, string userConfigFile)
    {
        try
        {
            var start = new DirectoryInfo(Path.GetFullPath(workingDirectory));
            DirectoryInfo? root = null;
            for (var d = start; d is not null; d = d.Parent)
            {
                if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                {
                    root = d;
                    break;
                }
            }
            var trusted = TrustedProjects(userConfigFile);
            if (!trusted.Contains(Normal(start.FullName)) && (root is null || !trusted.Contains(Normal(root.FullName))))
            {
                return [];
            }
            var folders = new List<DirectoryInfo>();
            for (var d = start; d is not null; d = d.Parent)
            {
                folders.Insert(0, d);
                if (root is null || Normal(d.FullName) == Normal(root.FullName)) break;
            }
            var names = new List<string>();
            foreach (var folder in folders)
            {
                foreach (var name in OtherMcpServers(Path.Combine(folder.FullName, ".codex", "config.toml")))
                {
                    if (!names.Contains(name)) names.Add(name);
                }
            }
            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>The project folders the user's Codex settings mark <c>trust_level = "trusted"</c>, normalized.</summary>
    private static HashSet<string> TrustedProjects(string configFile)
    {
        var trusted = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(configFile)) return trusted;
        string? project = null;
        foreach (var line in File.ReadLines(configFile))
        {
            if (line.TrimStart().StartsWith('['))
            {
                var header = ProjectHeader().Match(line);
                project = !header.Success ? null
                    : header.Groups["basic"].Success ? header.Groups["basic"].Value.Replace("\\\\", "\\").Replace("\\\"", "\"")
                    : header.Groups["literal"].Value;
                continue;
            }
            if (project is not null && TrustLine().Match(line) is { Success: true } trust && trust.Groups[1].Value == "trusted")
            {
                trusted.Add(Normal(project));
            }
        }
        return trusted;
    }

    /// <summary>A folder path as Codex compares project paths: absolute, no trailing separator, case-insensitive on Windows and macOS.</summary>
    private static string Normal(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToUpperInvariant() : full;
    }

    [GeneratedRegex("""^\s*\[\s*projects\s*\.\s*(?:"(?<basic>(?:[^"\\]|\\.)+)"|'(?<literal>[^']+)')\s*\]\s*(?:#.*)?$""")]
    private static partial Regex ProjectHeader();

    [GeneratedRegex("""^\s*trust_level\s*=\s*["']([^"']+)["']""")]
    private static partial Regex TrustLine();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex PlainKey();
}

/// <summary>
/// Reads <c>codex exec --json</c> output (JSONL): <c>thread.started</c>, <c>turn.started</c>,
/// <c>item.started</c>/<c>item.updated</c>/<c>item.completed</c> (agent messages, reasoning, commands,
/// file changes, MCP calls), <c>turn.completed</c> with <c>usage</c>, <c>turn.failed</c> and
/// <c>error</c>. <c>input_tokens</c> already includes <c>cached_input_tokens</c>, which is kept apart too
/// (priced at the cached rate, the original issue). Codex reports no cost.
/// <para>
/// The model (the original issue), first found of: a <c>model</c> on an event (older Codex versions sent a
/// <c>session_configured</c> event with it); the session's own log, which Codex writes to
/// <c>{codexHome}/sessions/yyyy/MM/dd/rollout-…-{thread id}.jsonl</c> with the model in each
/// <c>turn_context</c> line; the model in Codex's settings. None of them means unknown, never a guess.
/// </para>
/// </summary>
/// <param name="configuredModel">The model in Codex's settings, if any.</param>
/// <param name="codexHome">Codex's home folder, to find the session's log; null skips it.</param>
public sealed class CodexOutputParser(string? configuredModel, string? codexHome = null, TimeProvider? time = null) : IAgentOutputParser
{
    private string? _model;
    private readonly CapacityReadings _capacity = new("codex", time ?? TimeProvider.System);
    /// <inheritdoc />
    public MessageBus.Contracts.Runner.AgentCapacityReport? Capacity => _capacity.Report;
    private string? _threadId;
    private long _in;
    private long _cached;
    private long _out;
    private string? _lastMessage;
    private string? _error;
    private bool _failed;
    private bool _sessionRead;
    private string? _sessionModel;

    /// <inheritdoc />
    public string? Observe(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            _model ??= ModelIn(root);
            _capacity.Codex(root);
            switch (ClaudeOutputParser.Str(root, "type"))
            {
                case "thread.started":
                    _threadId = ClaudeOutputParser.Str(root, "thread_id");
                    return null;
                case "turn.completed" when root.TryGetProperty("usage", out var usage):
                    _in += ClaudeOutputParser.Long(usage, "input_tokens");
                    _cached += ClaudeOutputParser.Long(usage, "cached_input_tokens");
                    _out += ClaudeOutputParser.Long(usage, "output_tokens");
                    return null;
                case "turn.failed":
                    _failed = true;
                    _error = root.TryGetProperty("error", out var err) ? ClaudeOutputParser.Str(err, "message") ?? "turn failed" : "turn failed";
                    if (_error.Contains("usage limit", StringComparison.OrdinalIgnoreCase)) _capacity.Hit();
                    return null;
                case "error":
                    // Also sent for problems Codex recovers from (a dropped stream it retries), so
                    // only turn.failed marks the run as failed; this text explains it if it does fail.
                    _error ??= ClaudeOutputParser.Str(root, "message");
                    return null;
                case "item.started" or "item.completed" when root.TryGetProperty("item", out var item):
                    return Item(item, ClaudeOutputParser.Str(root, "type") == "item.completed");
                default:
                    return null;
            }
        }
    }

    private string? Item(JsonElement item, bool completed)
    {
        switch (ClaudeOutputParser.Str(item, "type"))
        {
            case "agent_message" when completed && ClaudeOutputParser.Str(item, "text") is { } text:
                _lastMessage = text;
                return AgentText.OneLine(text);
            case "command_execution" when !completed:
                return AgentText.OneLine("Running " + ClaudeOutputParser.Str(item, "command"));
            case "file_change" when completed:
                return "Changed files";
            case "mcp_tool_call" when !completed:
                return AgentText.OneLine($"{ClaudeOutputParser.Str(item, "server")} {ClaudeOutputParser.Str(item, "tool")}");
            default:
                return null;
        }
    }

    /// <inheritdoc />
    public Usage? UsageSoFar => _in + _out > 0 ? new Usage
    {
        AgentKind = AgentKind.Codex, Vendor = "openai", Model = _model ?? configuredModel,
        TokensIn = _in, CachedTokensIn = _cached, TokensOut = _out,
    } : null;

    /// <inheritdoc />
    public AgentOutcome Complete()
    {
        if (!_sessionRead)
        {
            _sessionRead = true;
            _sessionModel = SessionModel();
        }
        var sessionModel = _sessionModel;
        return new()
        {
            FinalText = _lastMessage,
            ReportedError = _failed,
            ErrorText = _error,
            Usage = new Usage
            {
                AgentKind = AgentKind.Codex,
                Vendor = "openai",
                Model = _model ?? sessionModel ?? configuredModel,
                TokensIn = _in,
                CachedTokensIn = _cached,
                TokensOut = _out,
            },
        };
    }

    /// <summary>A <c>model</c> named on the event itself or in its <c>msg</c> / <c>payload</c> (session and turn settings).</summary>
    private static string? ModelIn(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Named(root) is { } direct) return direct;
        foreach (var inner in (string[])["msg", "payload"])
        {
            if (root.TryGetProperty(inner, out var nested) && nested.ValueKind == JsonValueKind.Object && Named(nested) is { } model)
            {
                return model;
            }
        }
        return null;

        static string? Named(JsonElement e) => ClaudeOutputParser.Str(e, "model") is { Length: > 0 } m ? m : null;
    }

    /// <summary>
    /// The model in the last <c>turn_context</c> of this run's session log. Codex names the log after the
    /// thread id and files it under the local date it started, so only the last few days' folders are searched.
    /// </summary>
    private string? SessionModel()
    {
        if (codexHome is null || _threadId is not { Length: > 0 } thread || thread.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }
        try
        {
            var sessions = Path.Combine(codexHome, "sessions");
            var today = (time ?? TimeProvider.System).GetLocalNow().Date;
            for (var back = 0; back < 3; back++)
            {
                var day = Path.Combine(sessions, today.AddDays(-back).ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture));
                if (!Directory.Exists(day)) continue;
                foreach (var file in Directory.EnumerateFiles(day, $"*{thread}.jsonl"))
                {
                    string? model = null;
                    foreach (var line in File.ReadLines(file))
                    {
                        if (!line.Contains("\"turn_context\"", StringComparison.Ordinal) && !line.Contains("\"token_count\"", StringComparison.Ordinal)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            _capacity.Codex(doc.RootElement);
                            if (ClaudeOutputParser.Str(doc.RootElement, "type") == "turn_context") model = ModelIn(doc.RootElement) ?? model;
                        }
                        catch (JsonException)
                        {
                        }
                    }
                    if (model is not null) return model;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}
