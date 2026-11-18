using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MessageBus.Cli.Connect;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Readiness;
using MessageBus.Contracts.Machines;

namespace MessageBus.Cli.Agents;

/// <summary>One agent as the scan found it, with a caution that doesn't stop it taking work.</summary>
/// <param name="Agent">What is reported to the service.</param>
/// <param name="Warning">Something that may still go wrong (shown by <c>mbus doctor</c>), or null.</param>
internal sealed record AgentScan(DetectedAgent Agent, string? Warning);

/// <summary>Everything the machine scan found: agents and toolchains.</summary>
internal sealed record MachineScan(IReadOnlyList<AgentScan> Agents, IReadOnlyList<ToolStatus> Tools)
{
    /// <summary>git, which every task with a repository needs.</summary>
    public ToolStatus Git => Tools.First(t => t.Name == MachineTools.Git);
}

/// <summary>
/// The read-only machine scan behind <c>mbus login</c>, <c>mbus connect --rescan</c>, <c>mbus doctor</c>
/// and the runner: for each agent, whether it is installed, its version, whether it is on PATH, whether
/// its MCP settings point at <c>mbus</c>, and whether it can use its sign-in from this session (the
/// agent's own status command where it has one, else its saved sign-in or API key variable); and which
/// toolchains work (<see cref="MachineTools"/>). It runs <c>--version</c> and status commands, never a
/// model call; it changes nothing.
/// </summary>
internal static partial class AgentScanner
{
    private sealed record AgentSpec(
        AgentKind Kind,
        string Command,
        string? Vendor,
        Func<AgentLocations, Func<string, string?>, bool> McpConfigured,
        Func<AgentLocations, Func<string, string?>, bool> SignedIn,
        Func<string, SignInEnvironment, CancellationToken, Task<SignInCheck?>>? CheckSignIn = null);

    private static readonly AgentSpec[] Specs =
    [
        new(AgentKind.ClaudeCode, "claude", "anthropic",
            (loc, _) => JsonConfigFile.HasEntry(loc.ClaudeUserConfig, "mcpServers"),
            (loc, env) => HasAny(env, "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN")
                || File.Exists(loc.ClaudeCredentials)
                // macOS keeps the token in the Keychain; the account Claude Code signed in with is in .claude.json everywhere.
                || JsonConfigFile.TryRead(loc.ClaudeUserConfig)?["oauthAccount"] is JsonObject,
            SignInChecks.ClaudeAsync),
        new(AgentKind.Codex, "codex", "openai",
            (loc, _) => CodexTomlConfig.HasMbus(loc.CodexConfig),
            (loc, env) => HasAny(env, "OPENAI_API_KEY", "CODEX_API_KEY") || File.Exists(loc.CodexAuth),
            SignInChecks.CodexAsync),
        new(AgentKind.Copilot, "copilot", "github",
            (loc, _) => JsonConfigFile.HasEntry(loc.CopilotMcpConfig, "mcpServers"),
            (loc, env) => HasAny(env, "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN")
                || JsonConfigFile.TryRead(loc.CopilotConfig)?["logged_in_users"] is JsonArray { Count: > 0 }),
        new(AgentKind.OpenCode, "opencode", null,
            (loc, _) => OpenCodeConfig.HasMbus(loc.OpenCodeConfig),
            (loc, _) => File.Exists(loc.OpenCodeAuth)),
    ];

    /// <summary>The command each agent kind is started with.</summary>
    public static string CommandFor(AgentKind kind) => Specs.First(s => s.Kind == kind).Command;

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.\-]+)?")]
    private static partial Regex VersionPattern();

    /// <summary>Scans every agent kind mbus knows, in parallel.</summary>
    public static async Task<IReadOnlyList<DetectedAgent>> ScanAsync(CliHost host, CancellationToken cancellationToken) =>
        [.. (await ScanAgentsAsync(host, cancellationToken).ConfigureAwait(false)).Select(a => a.Agent)];

    /// <summary>
    /// Agents and toolchains, at once. An agent can't take work while git doesn't work (every task with a
    /// repository needs it), so then each found agent is not ready, with git's problem and fix.
    /// </summary>
    public static async Task<MachineScan> ScanMachineAsync(CliHost host, CancellationToken cancellationToken)
    {
        var agents = ScanAgentsAsync(host, cancellationToken);
        var tools = MachineTools.CheckAsync(host.GetEnvironmentVariable, host.HomeDirectory, cancellationToken);
        var scan = new MachineScan(await agents.ConfigureAwait(false), await tools.ConfigureAwait(false));
        if (scan.Git is { Works: false } git)
        {
            var problem = git.Problem ?? "git doesn't work on this machine.";
            scan = scan with
            {
                Agents = [.. scan.Agents.Select(a => a.Agent is { Found: true, NotReady: null } ? a with { Agent = a.Agent with { NotReady = problem } } : a)],
            };
        }
        return scan;
    }

    /// <summary>A full inventory for the service.</summary>
    public static async Task<MachineInventory> BuildInventoryAsync(CliHost host, string? machineId, string? location, CancellationToken cancellationToken)
    {
        var scan = await ScanMachineAsync(host, cancellationToken).ConfigureAwait(false);
        var agents = scan.Agents.Select(a => a.Agent).ToList();
        var os = OperatingSystem.IsWindows() ? MachineOs.Windows : OperatingSystem.IsMacOS() ? MachineOs.MacOs : MachineOs.Linux;
        var capabilities = new List<string>
        {
            CapabilityNames.Format(CapabilityNames.OsPrefix, os == MachineOs.MacOs ? "macos" : os.ToString()),
        };
        capabilities.AddRange(agents
            .Where(a => a is { Found: true, SignedIn: true, NotReady: null })
            .Select(a => CapabilityNames.Format(CapabilityNames.AgentPrefix, KindName(a.Kind))));
        capabilities.AddRange(MachineTools.Capabilities(scan.Tools));

        return new MachineInventory
        {
            // Empty until enrollment assigns one; the service fills it from the request's credentials.
            MachineId = machineId ?? "",
            HostName = host.MachineName,
            Os = os,
            OsVersion = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? CpuArchitecture.Arm64 : CpuArchitecture.X64,
            Target = DeploymentTargets.Current(host.GetEnvironmentVariable),
            Location = location,
            CliVersion = CliHost.Version,
            Capabilities = capabilities,
            Agents = agents,
            ScannedAt = host.Time.GetUtcNow(),
        };
    }

    /// <summary>The contract name of an agent kind (<c>claude-code</c>).</summary>
    public static string KindName(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => "claude-code",
        AgentKind.Codex => "codex",
        AgentKind.Copilot => "copilot",
        AgentKind.OpenCode => "opencode",
        _ => "custom",
    };

    /// <summary>How people know the agent.</summary>
    public static string DisplayName(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => "Claude Code",
        AgentKind.Codex => "Codex",
        AgentKind.Copilot => "GitHub Copilot CLI",
        AgentKind.OpenCode => "OpenCode",
        _ => "Custom agent",
    };

    private static async Task<IReadOnlyList<AgentScan>> ScanAgentsAsync(CliHost host, CancellationToken cancellationToken)
    {
        var locations = new AgentLocations(host);
        // the original issue: what the runner's agents see, with the sign-ins stored for the runner service.
        var env = new SignInEnvironment(host.GetEnvironmentVariable, Runner.Service.AgentEnvironmentStore.ReadFrom(host.ConfigDirectory));
        return await Task.WhenAll(Specs.Select(spec => ScanOneAsync(host, env, locations, spec, cancellationToken))).ConfigureAwait(false);
    }

    private static async Task<AgentScan> ScanOneAsync(CliHost host, SignInEnvironment env, AgentLocations locations, AgentSpec spec, CancellationToken cancellationToken)
    {
        var located = ExecutableLocator.Find(host, spec.Command);
        if (located is null)
        {
            return new(new DetectedAgent { Kind = spec.Kind, Found = false, Vendor = spec.Vendor }, null);
        }

        string? version = null;
        if (await CommandRunner.RunAsync(located.Path, ["--version"], cancellationToken).ConfigureAwait(false) is { } run)
        {
            var match = VersionPattern().Match(run.Output);
            version = match.Success ? match.Value : null;
            if (version is null && run.ExitCode != 0)
            {
                // A shim that only says the agent isn't installed (VS Code puts a 'copilot' one on PATH).
                return new(new DetectedAgent { Kind = spec.Kind, Found = false, Vendor = spec.Vendor }, null);
            }
        }

        // The agent's own answer when it has a status command; its saved files only when it can't say.
        var checking = spec.CheckSignIn is null ? Task.FromResult<SignInCheck?>(null) : spec.CheckSignIn(located.Path, env, cancellationToken);
        // the original issue: its default model and the models it lists, read alongside the sign-in check.
        var models = await ModelCatalog.ReadAsync(spec.Kind, located.Path, version, locations, env, cancellationToken).ConfigureAwait(false);
        var check = await checking.ConfigureAwait(false);
        var agent = new DetectedAgent
        {
            Kind = spec.Kind,
            Found = true,
            Version = version,
            OnPath = located.OnPath,
            ExecutablePath = located.Path,
            McpConfigured = spec.McpConfigured(locations, host.GetEnvironmentVariable),
            SignedIn = check?.SignedIn ?? spec.SignedIn(locations, env.Get),
            NotReady = check?.NotReady,
            Vendor = spec.Vendor,
            DefaultModel = models.Default,
            Models = models.Models,
            ModelsPartial = models.Partial,
        };
        return new(agent, check?.Warning);
    }

    private static bool HasAny(Func<string, string?> env, params string[] names) =>
        names.Any(name => !string.IsNullOrWhiteSpace(env(name)));
}
