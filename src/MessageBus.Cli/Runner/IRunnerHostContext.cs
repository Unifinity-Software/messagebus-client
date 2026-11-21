using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>
/// What the runner takes from the rest of <c>mbus</c>: who this machine is, how to call the server,
/// whose work it accepts, and how to find and list agents. The runner never reads settings files or
/// keys itself. <see cref="EnvironmentRunnerHostContext"/> builds one from <c>MBUS_*</c> variables
/// (containers); the <c>mbus runner</c> command builds one from the signed-in settings (the original issue).
/// </summary>
public interface IRunnerHostContext
{
    /// <summary>The messagebus.ai server (no trailing slash).</summary>
    Uri ServerUrl { get; }

    /// <summary>The enrolled machine (<c>mch_…</c>), when known (an API key alone doesn't say).</summary>
    string? MachineId { get; }

    /// <summary>The machine's display name.</summary>
    string MachineName { get; }

    /// <summary>The machine's Location label.</summary>
    string? Location { get; }

    /// <summary>Whose tasks this machine runs, and how much it may spend on other people's.</summary>
    RunnerPolicy Policy { get; }

    /// <summary>
    /// The agent registry's ids for this machine's agents (<c>agt_…</c> → kind), so a delivery's
    /// <c>agentId</c> picks the right CLI. A delivery may also name the kind itself (<c>claude-code</c>).
    /// </summary>
    IReadOnlyDictionary<string, AgentKind> AgentIds { get; }

    /// <summary>
    /// An HTTP client whose every request is authenticated as this machine, with
    /// <see cref="HttpClient.BaseAddress"/> set to the server URL plus a trailing slash. The runner
    /// sends relative paths without a leading slash (<c>api/runner/settle</c>).
    /// </summary>
    HttpClient CreateServiceClient();

    /// <summary>
    /// Headers that authenticate one connect request of the runner connection (the original issue), made fresh for
    /// each attempt (a machine signature covers the method, path and time).
    /// </summary>
    /// <param name="method">The request method (a WebSocket upgrade is GET).</param>
    /// <param name="pathAndQuery">Path and query exactly as sent.</param>
    IReadOnlyList<KeyValuePair<string, string>> CreateConnectHeaders(string method, string pathAndQuery);

    /// <summary>
    /// Authenticates and awaits one actual handshake. Built-in hosts retain the profile security reservation
    /// through completion; the default adapter preserves existing custom host implementations.
    /// </summary>
    Task ConnectAsync(MessageBus.Runner.Transport.ConnectRequest request, Func<CancellationToken, Task> connect,
        CancellationToken cancellationToken)
    {
        foreach (var (name, value) in CreateConnectHeaders(request.Method, request.PathAndQuery))
            request.SetHeader(name, value);
        return connect(cancellationToken);
    }

    /// <summary>Full path of a command the way a shell would find it (PATH, PATHEXT, known install folders), or null.</summary>
    string? LocateCommand(string command);

    /// <summary>Fresh role-key readability without scanning agents or machine tools; null when unavailable.</summary>
    Task<IReadOnlyDictionary<string, bool>?> CheckRoleKeysAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, bool>?>(null);

    /// <summary>This machine's inventory (agents found, capabilities) for the service, with <paramref name="slots"/> filled in.</summary>
    Task<MachineInventory> BuildInventoryAsync(int slots, CancellationToken cancellationToken);

    /// <summary>An environment variable of the runner's own process (null when unset).</summary>
    string? GetEnvironmentVariable(string name);

    /// <summary>
    /// The agent sign-ins stored for the runner service (the original issue): given to each run's agent process (only to it) where the
    /// runner's own environment doesn't set them. Read for each run, so storing new ones needs no restart.
    /// </summary>
    IReadOnlyDictionary<string, string> AgentEnvironment() => new Dictionary<string, string>();

    /// <summary>The user's home directory.</summary>
    string HomeDirectory { get; }

    /// <summary>Capacity observations in this runner settings scope.</summary>
    AgentCapacityStore CapacityStore => new Configuration.ConfigStore(GetEnvironmentVariable("MBUS_HOME") ?? Path.Combine(HomeDirectory, ".mbus")).AgentCapacity;
}

/// <summary>Whose work a machine accepts (set by its owner; see <see cref="AcceptsWorkFrom"/>).</summary>
public sealed record RunnerPolicy
{
    /// <summary>Anyone in the workspace (the service only delivers within the workspace).</summary>
    public static RunnerPolicy Workspace { get; } = new() { AcceptsWorkFrom = new AcceptsWorkFrom { Mode = AcceptsWorkFromMode.Workspace } };

    /// <summary>The machine owner's subject id; needed for the own-projects modes and the spend ceiling.</summary>
    public string? OwnerSubjectId { get; init; }

    /// <summary>The rule.</summary>
    public required AcceptsWorkFrom AcceptsWorkFrom { get; init; }

    /// <summary>
    /// For <see cref="AcceptsWorkFromMode.OwnProjectsAndGroups"/>: the people in the chosen groups
    /// (subject ids). The runner can't read Entra groups itself; whoever builds the policy expands them.
    /// </summary>
    public IReadOnlySet<string> GroupMemberSubjectIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Most this machine may spend per day (UTC) on work for other people, in US dollars (estimated); null for no ceiling.</summary>
    public decimal? DailySpendCeilingUsd { get; init; }

    /// <summary>Which repository credential runs get (the original issue); see <see cref="RunnerWorkPolicy.RepoCredentialMode"/>.</summary>
    public RepoCredentialMode RepoCredentialMode { get; init; } = RepoCredentialMode.PerRunToken;

    /// <summary>Whether this run uses the owner's own git sign-in: only for the owner's own projects, and only when they chose it.</summary>
    public bool UsesOwnerGit(TaskDelivery delivery) =>
        RepoCredentialMode == RepoCredentialMode.OwnerSession && OwnerSubjectId is { } owner && delivery.ProjectOwner == owner;
}
