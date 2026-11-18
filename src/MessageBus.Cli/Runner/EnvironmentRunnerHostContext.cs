using System.Globalization;
using System.Runtime.InteropServices;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Security;

namespace MessageBus.Cli.Runner;

/// <summary>
/// Environment variables the runner reads. The first four are the container image's
/// (<c>docker/runner</c>) and the same names <c>mbus</c> uses everywhere; the rest are runner-only.
/// </summary>
public static class RunnerEnvVars
{
    /// <summary>Server URL.</summary>
    public const string Server = "MBUS_SERVER";
    /// <summary>The machine's API key (<c>Authorization: MbusKey …</c>).</summary>
    public const string ApiKey = "MBUS_API_KEY";
    /// <summary>The machine's display name.</summary>
    public const string MachineName = "MBUS_MACHINE_NAME";
    /// <summary>The machine's Location label.</summary>
    public const string Location = "MBUS_LOCATION";
    /// <summary>The machine id (<c>mch_…</c>), when known.</summary>
    public const string MachineId = "MBUS_MACHINE_ID";
    /// <summary>The workspace id, sent as <see cref="MbusHeaders.Workspace"/>.</summary>
    public const string Workspace = "MBUS_WORKSPACE";
    /// <summary>Agent registry ids for this machine: <c>agt_1=claude-code,agt_2=codex</c>.</summary>
    public const string Agents = "MBUS_AGENTS";
    /// <summary>
    /// <c>own-projects</c>, <c>own-projects-and-groups</c> or <c>workspace</c>. Overrides the machine's setting from the
    /// service (the original issue); before the service answers, unset means <c>workspace</c>. The next three override it the same way.
    /// </summary>
    public const string AcceptsWorkFrom = "MBUS_ACCEPTS_WORK_FROM";
    /// <summary>The machine owner's subject id.</summary>
    public const string Owner = "MBUS_OWNER";
    /// <summary>For <c>own-projects-and-groups</c>: the group members' subject ids, comma-separated.</summary>
    public const string GroupMembers = "MBUS_ACCEPTED_SUBJECTS";
    /// <summary>Daily spend ceiling for other people's work, US dollars.</summary>
    public const string DailySpendCeiling = "MBUS_DAILY_SPEND_CEILING_USD";
    /// <summary>macOS idle sleep policy: always, tasks or off.</summary>
    public const string KeepAwake = "MBUS_RUNNER_KEEP_AWAKE";

    /// <summary>Folder for run directories (default <c>~/.mbus/runs</c>).</summary>
    public const string WorkDirectory = "MBUS_RUNNER_WORK_DIR";
    /// <summary>Prefix for a per-agent command override: <c>MBUS_RUNNER_AGENT_CLAUDE_CODE=/path/to/claude</c>.</summary>
    public const string AgentCommandPrefix = "MBUS_RUNNER_AGENT_";
}

/// <summary>Reads the runner's settings out of environment variables (shared by every host context).</summary>
public static class RunnerEnvironment
{
    /// <summary>The <c>mbus</c> version (<see cref="Hosting.CliHost.Version"/>).</summary>
    public static string Version => Hosting.CliHost.Version;

    /// <summary>
    /// Whose work the machine accepts before the service has said (the original issue), from <see cref="RunnerEnvVars"/>. Unset means
    /// anyone in the workspace.
    /// </summary>
    public static RunnerPolicy ReadPolicy(Func<string, string?> env) => WithOverrides(RunnerPolicy.Workspace, env);

    /// <summary>
    /// <paramref name="policy"/> (the machine's settings from the service, the original issue) with each of
    /// <see cref="RunnerEnvVars.AcceptsWorkFrom"/>, <see cref="RunnerEnvVars.Owner"/>, <see cref="RunnerEnvVars.GroupMembers"/>
    /// and <see cref="RunnerEnvVars.DailySpendCeiling"/> that is set taking its place (containers set them).
    /// </summary>
    /// <exception cref="FormatException">A variable has a value that isn't valid.</exception>
    public static RunnerPolicy WithOverrides(RunnerPolicy policy, Func<string, string?> env)
    {
        if (NonEmpty(env(RunnerEnvVars.AcceptsWorkFrom)) is { } mode)
        {
            policy = policy with
            {
                AcceptsWorkFrom = new AcceptsWorkFrom
                {
                    Mode = mode.ToLowerInvariant() switch
                    {
                        "workspace" => AcceptsWorkFromMode.Workspace,
                        "own-projects" => AcceptsWorkFromMode.OwnProjects,
                        "own-projects-and-groups" => AcceptsWorkFromMode.OwnProjectsAndGroups,
                        var other => throw new FormatException($"{RunnerEnvVars.AcceptsWorkFrom} is '{other}'; use own-projects, own-projects-and-groups or workspace."),
                    },
                },
            };
        }
        if (NonEmpty(env(RunnerEnvVars.Owner)) is { } owner)
        {
            policy = policy with { OwnerSubjectId = owner };
        }
        if (NonEmpty(env(RunnerEnvVars.GroupMembers)) is { } members)
        {
            policy = policy with
            {
                GroupMemberSubjectIds = new HashSet<string>(members.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal),
            };
        }
        if (NonEmpty(env(RunnerEnvVars.DailySpendCeiling)) is { } ceiling)
        {
            policy = policy with { DailySpendCeilingUsd = decimal.Parse(ceiling, NumberStyles.Number, CultureInfo.InvariantCulture) };
        }
        return policy;
    }

    /// <summary>The <see cref="RunnerEnvVars"/> set on this machine that override the service's work policy.</summary>
    public static IReadOnlyList<string> PolicyOverrides(Func<string, string?> env) =>
        [.. new[] { RunnerEnvVars.AcceptsWorkFrom, RunnerEnvVars.Owner, RunnerEnvVars.GroupMembers, RunnerEnvVars.DailySpendCeiling }
            .Where(name => NonEmpty(env(name)) is not null)];

    /// <summary>Agent registry ids from <see cref="RunnerEnvVars.Agents"/>.</summary>
    public static IReadOnlyDictionary<string, AgentKind> ReadAgentIds(Func<string, string?> env)
    {
        var map = new Dictionary<string, AgentKind>(StringComparer.Ordinal);
        foreach (var pair in (NonEmpty(env(RunnerEnvVars.Agents)) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && AgentKinds.TryParse(parts[1], out var kind))
            {
                map[parts[0]] = kind;
            }
        }
        return map;
    }

    /// <summary>A server URL as people type it (<c>host:5200</c>, <c>https://host/</c>).</summary>
    public static Uri ParseServer(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new FormatException($"'{text}' isn't a server URL, for example https://messagebus.contoso.com.");
        }
        return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'));
    }

    /// <summary>A basic inventory: OS, architecture, and which agent commands are found.</summary>
    public static MachineInventory BasicInventory(IRunnerHostContext host, int slots, DateTimeOffset now)
    {
        var os = OperatingSystem.IsWindows() ? MachineOs.Windows : OperatingSystem.IsMacOS() ? MachineOs.MacOs : MachineOs.Linux;
        var agents = AgentKinds.Launchable.Select(kind =>
        {
            var path = host.LocateCommand(AgentKinds.Command(kind));
            return new DetectedAgent { Kind = kind, Found = path is not null, ExecutablePath = path, OnPath = path is not null };
        }).ToList();
        var capabilities = new List<string> { CapabilityNames.Format(CapabilityNames.OsPrefix, os == MachineOs.MacOs ? "macos" : os.ToString()) };
        capabilities.AddRange(agents.Where(a => a.Found).Select(a => CapabilityNames.Format(CapabilityNames.AgentPrefix, AgentKinds.Name(a.Kind))));
        return new MachineInventory
        {
            MachineId = host.MachineId ?? "",
            HostName = Environment.MachineName,
            Os = os,
            OsVersion = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? CpuArchitecture.Arm64 : CpuArchitecture.X64,
            Target = global::MessageBus.Cli.Agents.DeploymentTargets.Current(Environment.GetEnvironmentVariable),
            Location = host.Location,
            CliVersion = Version,
            Slots = slots,
            Capabilities = capabilities,
            Agents = agents,
            ScannedAt = now,
        };
    }

    internal static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A host context from <c>MBUS_*</c> environment variables alone, for a runner with no
/// <c>mbus login</c> (the container image sets <c>MBUS_SERVER</c>, <c>MBUS_API_KEY</c>,
/// <c>MBUS_MACHINE_NAME</c>, <c>MBUS_LOCATION</c>).
/// </summary>
public sealed class EnvironmentRunnerHostContext : IRunnerHostContext
{
    private readonly Func<string, string?> _env;
    private readonly Func<HttpMessageHandler> _createHandler;
    private readonly IReadOnlyList<string> _extraDirectories;
    private readonly TimeProvider _time;

    /// <summary>Reads the variables through <paramref name="env"/>.</summary>
    /// <param name="env">Environment lookup (null when unset).</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="createHandler">The HTTP handler the service client sends through.</param>
    /// <param name="time">Clock for inventory times.</param>
    public EnvironmentRunnerHostContext(Func<string, string?> env, string homeDirectory, Func<HttpMessageHandler>? createHandler = null, TimeProvider? time = null)
    {
        _env = env;
        _createHandler = createHandler ?? (() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        _time = time ?? TimeProvider.System;
        HomeDirectory = homeDirectory;
        _extraDirectories = CommandLocator.DefaultExtraDirectories(homeDirectory);
        ServerUrl = RunnerEnvironment.ParseServer(RunnerEnvironment.NonEmpty(env(RunnerEnvVars.Server))
            ?? throw new InvalidOperationException($"Set {RunnerEnvVars.Server} to the server URL, or sign in with mbus login."));
        MachineId = RunnerEnvironment.NonEmpty(env(RunnerEnvVars.MachineId));
        MachineName = RunnerEnvironment.NonEmpty(env(RunnerEnvVars.MachineName)) ?? Environment.MachineName;
        Location = RunnerEnvironment.NonEmpty(env(RunnerEnvVars.Location));
        Policy = RunnerEnvironment.ReadPolicy(env);
        AgentIds = RunnerEnvironment.ReadAgentIds(env);
    }

    /// <summary>The real process environment and home folder.</summary>
    public static EnvironmentRunnerHostContext FromProcess() =>
        new(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <inheritdoc />
    public Uri ServerUrl { get; }

    /// <inheritdoc />
    public string? MachineId { get; }

    /// <inheritdoc />
    public string MachineName { get; }

    /// <inheritdoc />
    public string? Location { get; }

    /// <inheritdoc />
    public RunnerPolicy Policy { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, AgentKind> AgentIds { get; }

    /// <inheritdoc />
    public string HomeDirectory { get; }

    /// <inheritdoc />
    public HttpClient CreateServiceClient()
    {
        var client = new HttpClient(_createHandler(), disposeHandler: true) { BaseAddress = new Uri(ServerUrl.AbsoluteUri.TrimEnd('/') + "/") };
        foreach (var (name, value) in Headers())
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
        return client;
    }

    /// <inheritdoc />
    public IReadOnlyList<KeyValuePair<string, string>> CreateConnectHeaders(string method, string pathAndQuery) => Headers();

    private List<KeyValuePair<string, string>> Headers()
    {
        var apiKey = RunnerEnvironment.NonEmpty(_env(RunnerEnvVars.ApiKey))
            ?? throw new InvalidOperationException($"Set {RunnerEnvVars.ApiKey} to this machine's API key, or sign in with mbus login.");
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Authorization", $"{AuthSchemes.ApiKey} {apiKey}"),
            new(MbusHeaders.ClientComponent, "runner"),
            new(MbusHeaders.ClientVersion, RunnerEnvironment.Version),
        };
        if (RunnerEnvironment.NonEmpty(_env(RunnerEnvVars.Workspace)) is { } workspace)
        {
            headers.Add(new(MbusHeaders.Workspace, workspace));
        }
        return headers;
    }

    /// <inheritdoc />
    public string? LocateCommand(string command) => CommandLocator.Find(command, _env, _extraDirectories);

    /// <inheritdoc />
    // Container runners use environment configuration and have no persisted workspace trust to check.
    public Task<MachineInventory> BuildInventoryAsync(int slots, CancellationToken cancellationToken) =>
        Task.FromResult(RunnerEnvironment.BasicInventory(this, slots, _time.GetUtcNow()));

    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name) => _env(name);
}
