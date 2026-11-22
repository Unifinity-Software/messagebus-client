using System.Text.Json.Serialization;

namespace MessageBus.Contracts.Machines;

/// <summary>A coding agent CLI the bus knows how to detect and start.</summary>
public enum AgentKind
{
    /// <summary>Anthropic's Claude Code (<c>claude -p</c>).</summary>
    [JsonStringEnumMemberName("claude-code")] ClaudeCode,
    /// <summary>OpenAI's Codex CLI (<c>codex exec</c>).</summary>
    [JsonStringEnumMemberName("codex")] Codex,
    /// <summary>GitHub Copilot CLI (<c>copilot -p</c>).</summary>
    [JsonStringEnumMemberName("copilot")] Copilot,
    /// <summary>OpenCode (<c>opencode run</c>).</summary>
    [JsonStringEnumMemberName("opencode")] OpenCode,
    /// <summary>Google Antigravity CLI (<c>agy -p</c>).</summary>
    [JsonStringEnumMemberName("antigravity")] Antigravity,
    /// <summary>Any other command configured by the machine's owner.</summary>
    [JsonStringEnumMemberName("custom")] Custom,
}

/// <summary>Operating system of a machine.</summary>
public enum MachineOs
{
    /// <summary>Windows.</summary>
    [JsonStringEnumMemberName("windows")] Windows,
    /// <summary>macOS.</summary>
    [JsonStringEnumMemberName("macos")] MacOs,
    /// <summary>Linux.</summary>
    [JsonStringEnumMemberName("linux")] Linux,
}

/// <summary>Processor architecture of a machine.</summary>
public enum CpuArchitecture
{
    /// <summary>x86-64.</summary>
    [JsonStringEnumMemberName("x64")] X64,
    /// <summary>ARM64 (Apple silicon, Windows on ARM).</summary>
    [JsonStringEnumMemberName("arm64")] Arm64,
}

/// <summary>Where a runner runs, as it detects it (the original issue). Lets the Runners page filter and group by it.</summary>
public enum DeploymentTarget
{
    /// <summary>A computer: a PC, Mac, build box or server, not in a container.</summary>
    [JsonStringEnumMemberName("pc")] Pc,
    /// <summary>A Docker or Podman container.</summary>
    [JsonStringEnumMemberName("docker")] Docker,
    /// <summary>An Azure Container App.</summary>
    [JsonStringEnumMemberName("azure-container-apps")] AzureContainerApps,
    /// <summary>A Kubernetes pod.</summary>
    [JsonStringEnumMemberName("kubernetes")] Kubernetes,
    /// <summary>An Azure Container Instance.</summary>
    [JsonStringEnumMemberName("azure-container-instances")] AzureContainerInstances,
}

/// <summary>Optional, self-reported deployment details. These are setup hints, never authorization for cloud actions.</summary>
public sealed record MachineDeployment
{
    /// <summary>The detailed deployment target; the inventory's original target remains a compatible coarse category.</summary>
    public required DeploymentTarget Target { get; init; }
    /// <summary>The reported Azure ARM resource ID. An administrator must separately verify any lifecycle binding.</summary>
    public string? AzureResourceId { get; init; }
}

/// <summary>Whose machine it is, which decides how it enrolled and its defaults.</summary>
public enum MachineKind
{
    /// <summary>A person's own machine, signed in through dashboard approval. Accepts work from its owner's projects by default.</summary>
    [JsonStringEnumMemberName("personal")] Personal,
    /// <summary>A shared runner (build box, container) enrolled with a one-time token. Accepts work from anyone in the workspace by default.</summary>
    [JsonStringEnumMemberName("shared")] Shared,
}

/// <summary>Whose tasks a machine will run (its owner sets this).</summary>
public enum AcceptsWorkFromMode
{
    /// <summary>Only projects its owner started.</summary>
    [JsonStringEnumMemberName("own-projects")] OwnProjects,
    /// <summary>Its owner's projects and projects started by people in the chosen groups.</summary>
    [JsonStringEnumMemberName("own-projects-and-groups")] OwnProjectsAndGroups,
    /// <summary>Anyone in the workspace.</summary>
    [JsonStringEnumMemberName("workspace")] Workspace,
}

/// <summary>Which repository credential a run gets on this machine.</summary>
public enum RepoCredentialMode
{
    /// <summary>A short-lived token minted for the run, scoped to the task's repository (shared runners).</summary>
    [JsonStringEnumMemberName("per-run-token")] PerRunToken,
    /// <summary>A fine-grained token the owner stored in the OS keychain.</summary>
    [JsonStringEnumMemberName("keychain-token")] KeychainToken,
    /// <summary>The owner's own signed-in session (an explicit opt-in).</summary>
    [JsonStringEnumMemberName("owner-session")] OwnerSession,
}

/// <summary>Whether an agent can take work right now.</summary>
public enum AgentPresence
{
    /// <summary>Its runner is connected and it has a free slot.</summary>
    [JsonStringEnumMemberName("online")] Online,
    /// <summary>Its runner is connected and every slot is busy.</summary>
    [JsonStringEnumMemberName("busy")] Busy,
    /// <summary>Its runner is not connected (or the silence timeout passed).</summary>
    [JsonStringEnumMemberName("offline")] Offline,
}

/// <summary>
/// Capability strings a machine advertises and a role or capability target requires. A capability
/// is <c>{prefix}:{value}</c>; a machine meets a requirement when it advertises the exact string.
/// </summary>
public static class CapabilityNames
{
    /// <summary>Operating system: <c>os:windows</c>, <c>os:macos</c>, <c>os:linux</c>.</summary>
    public const string OsPrefix = "os";
    /// <summary>A repository the machine can work on: <c>repo:owner/name</c>.</summary>
    public const string RepoPrefix = "repo";
    /// <summary>A tool installed on the machine: <c>tool:dotnet</c>, <c>tool:node</c>, <c>tool:docker</c>.</summary>
    public const string ToolPrefix = "tool";
    /// <summary>A tier the owner assigns: <c>tier:gpu</c>, <c>tier:fast</c>.</summary>
    public const string TierPrefix = "tier";
    /// <summary>A coding agent installed and signed in: <c>agent:claude-code</c>.</summary>
    public const string AgentPrefix = "agent";
    /// <summary>Where the machine is, as its owner names it: <c>site:office</c>.</summary>
    public const string SitePrefix = "site";
    /// <summary>
    /// A model an agent reports it can run (the original issue): <c>model:claude-fable-5-2</c>. Only that agent offers it, not the
    /// other agents on its machine.
    /// </summary>
    public const string ModelPrefix = "model";

    /// <summary>An owner-enrolled runner passed the executable preflight for the isolated Node npm recipe, version 1.</summary>
    public const string NodeVerificationV1 = "verification:node-npm-ci-v1";

    /// <summary>
    /// The kinds a machine's owner may add from the dashboard (the original issue). The others (<c>os:</c>, <c>agent:</c>, <c>tool:</c>) are
    /// only what <c>mbus</c> finds on the machine, so an added one can never pass for them.
    /// </summary>
    public static readonly IReadOnlyList<string> OwnerPrefixes = [TierPrefix, RepoPrefix, SitePrefix];

    /// <summary><c>{prefix}:{value}</c>, lowercased.</summary>
    /// <param name="prefix">One of the prefix constants.</param>
    /// <param name="value">The value.</param>
    public static string Format(string prefix, string value) => $"{prefix}:{value}".ToLowerInvariant();
}

/// <summary>Whose tasks a machine accepts.</summary>
public sealed record AcceptsWorkFrom
{
    /// <summary>The rule.</summary>
    public required AcceptsWorkFromMode Mode { get; init; }

    /// <summary>The Entra group ids, for <see cref="AcceptsWorkFromMode.OwnProjectsAndGroups"/>.</summary>
    public IReadOnlyList<string> GroupIds { get; set; } = [];
}

/// <summary>A coding agent found on a machine by <c>mbus login</c> / <c>mbus connect --rescan</c>.</summary>
public sealed record DetectedAgent
{
    /// <summary>Which agent.</summary>
    public required AgentKind Kind { get; init; }

    /// <summary>Whether it is installed.</summary>
    public required bool Found { get; init; }

    /// <summary>Its version, when found.</summary>
    public string? Version { get; init; }

    /// <summary>Whether its command is on PATH.</summary>
    public bool OnPath { get; init; }

    /// <summary>Full path of its executable, when found.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Whether its MCP configuration points at the local <c>mbus mcp</c> server.</summary>
    public bool McpConfigured { get; init; }

    /// <summary>Whether it is signed in to its vendor.</summary>
    public bool SignedIn { get; init; }

    /// <summary>Model vendor, when known (<c>anthropic</c>, <c>openai</c>, <c>github</c>).</summary>
    public string? Vendor { get; init; }

    /// <summary>
    /// Why this agent can't take work on this machine now, with the fix (git doesn't work, the agent can't use
    /// its sign-in from the runner's session); null when it can. Such an agent is not offered work.
    /// </summary>
    public string? NotReady { get; init; }

    /// <summary>
    /// The model it runs when a task names none (the original issue), as its settings say; null when they don't say (it is never guessed).
    /// </summary>
    public string? DefaultModel { get; init; }

    /// <summary>The models it can run, as the agent itself lists them (the original issue); empty when it can't list them.</summary>
    public IReadOnlyList<string> Models { get; set; } = [];

    /// <summary>
    /// The agent couldn't list every model it can run (the original issue), so <see cref="Models"/> holds only what is known (its default,
    /// if any): it may run others.
    /// </summary>
    public bool ModelsPartial { get; init; }
}

/// <summary>
/// What a machine reports about itself: after <c>mbus login</c>, on <c>mbus connect --rescan</c>, and
/// over the runner connection (<see cref="Runner.ServiceRoutes.Inventory"/>).
/// </summary>
public sealed record MachineInventory
{
    /// <summary>Role keys checked by this scan: true when readable, false only for a verified refusal; absent while awaiting a grant.</summary>
    public IReadOnlyDictionary<string, bool>? RoleKeysReadable { get; init; }

    /// <summary>The machine (<c>mch_…</c>).</summary>
    public required string MachineId { get; init; }

    /// <summary>Host name.</summary>
    public required string HostName { get; init; }

    /// <summary>Operating system.</summary>
    public required MachineOs Os { get; init; }

    /// <summary>OS version string.</summary>
    public string? OsVersion { get; init; }

    /// <summary>Processor architecture.</summary>
    public required CpuArchitecture Architecture { get; init; }

    /// <summary>Where it runs (the original issue); null from an <c>mbus</c> older than that.</summary>
    public DeploymentTarget? Target { get; init; }

    /// <summary>Optional deployment details, reported by the runner and untrusted.</summary>
    public MachineDeployment? Deployment { get; init; }

    /// <summary>Free-text location label, for example <c>Mac mini</c>.</summary>
    public string? Location { get; init; }

    /// <summary>Version of <c>mbus</c>.</summary>
    public required string CliVersion { get; init; }

    /// <summary>How many tasks the runner runs at once (0 when no runner is set up).</summary>
    public int Slots { get; init; }

    /// <summary>
    /// The most tasks at once the runner allows (the original issue): <see cref="SlotMachineMax"/> (the original issue; a runner older than that may
    /// report a lower cap its owner set on the machine); null when no runner reported it.
    /// </summary>
    public int? SlotCeiling { get; init; }

    /// <summary>
    /// The most tasks at once the runner allows on this machine's size (<see cref="SlotAdvice.MostForMachine"/>, the original issue);
    /// null from a runner older than that.
    /// </summary>
    public int? SlotMachineMax { get; init; }

    /// <summary>Logical processors, reported by the runner (the original issue).</summary>
    public int? CpuCount { get; init; }

    /// <summary>Memory available to the runner, in bytes (the original issue).</summary>
    public long? MemoryBytes { get; init; }

    /// <summary>Capabilities, see <see cref="CapabilityNames"/>.</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = [];

    /// <summary>Every agent kind checked, found or not.</summary>
    public IReadOnlyList<DetectedAgent> Agents { get; set; } = [];

    /// <summary>How the runner keeps runs apart and which repository credential they get (the original issue); null from a scan without a runner.</summary>
    public RunIsolationReport? Isolation { get; init; }

    /// <summary>How the runner's last update ended (the original issue); null from a scan without a runner, or one that never updated itself.</summary>
    public Releases.RunnerUpdateReport? LastUpdate { get; init; }

    /// <summary>Whether this runner's mbus can update itself, and why not (the original issue); null from a scan without a runner or an older mbus.</summary>
    public Releases.RunnerUpdateAbility? Updates { get; init; }

    /// <summary>When the scan ran.</summary>
    public required DateTimeOffset ScannedAt { get; init; }
}

/// <summary>What a machine's runner says about run isolation (the original issue), for the Runners page. Each text is the runner's own wording.</summary>
public sealed record RunIsolationReport
{
    /// <summary>How runs are kept apart from the machine and each other, one short sentence.</summary>
    public required string Summary { get; init; }

    /// <summary>Which repository credential runs get, one short sentence.</summary>
    public required string Credentials { get; init; }

    /// <summary>What run isolation can't enforce on this machine, one sentence each.</summary>
    public IReadOnlyList<string> Limitations { get; set; } = [];

    /// <summary>
    /// The level runs get (the original issue): <c>os-sandbox</c>, <c>basic</c> or <c>off</c>, so the Runners page can name it in two
    /// words; null from an older mbus.
    /// </summary>
    public string? Level { get; init; }

    /// <summary>
    /// Whether a role's network setting limits runs here (the original issue): <c>enforced</c> by the OS sandbox, <c>advisory</c>
    /// (set up for the run but a program can go around it) or <c>open</c>; null from an older mbus.
    /// </summary>
    public string? Network { get; init; }
}

/// <summary>An enrolled machine as the service stores and the Runners page shows it.</summary>
public sealed record MachineInfo
{
    /// <summary>Machine id (<c>mch_…</c>).</summary>
    public required string MachineId { get; init; }

    /// <summary>The workspace it is enrolled in.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Its owner's subject id (the person who signed it in, or who issued the enrollment token).</summary>
    public required string OwnerSubjectId { get; init; }

    /// <summary>Personal or shared.</summary>
    public required MachineKind Kind { get; init; }

    /// <summary>Display name (defaults to the host name).</summary>
    public required string Name { get; init; }

    /// <summary>Free-text location label, set at enrollment and editable later.</summary>
    public string? Location { get; init; }

    /// <summary>Operating system.</summary>
    public required MachineOs Os { get; init; }

    /// <summary>Processor architecture.</summary>
    public required CpuArchitecture Architecture { get; init; }

    /// <summary>Where it runs, as its <c>mbus</c> last reported (the original issue); null until one that knows has reported.</summary>
    public DeploymentTarget? Target { get; init; }

    /// <summary>Optional deployment details from the last inventory; never an authorized cloud binding.</summary>
    public MachineDeployment? Deployment { get; init; }

    /// <summary>Version of <c>mbus</c> last reported.</summary>
    public string? CliVersion { get; init; }

    /// <summary>Concurrent task slots.</summary>
    public int Slots { get; init; }

    /// <summary>
    /// Tasks at once as set from the dashboard (the original issue); null leaves it to the machine (1).
    /// Stored while the machine is offline and sent to its runner when it connects.
    /// </summary>
    public int? SlotSetting { get; init; }

    /// <summary>
    /// The most tasks at once its runner allows: <see cref="SlotMachineMax"/> (the original issue; a runner older than that may report
    /// a lower cap its owner set on the machine); null until a runner reports it.
    /// </summary>
    public int? SlotCeiling { get; init; }

    /// <summary>
    /// The most its runner allows on this machine's size (the original issue); null until a runner that knows reports it.
    /// </summary>
    public int? SlotMachineMax { get; init; }

    /// <summary>Logical processors, as its runner reported.</summary>
    public int? CpuCount { get; init; }

    /// <summary>Memory available to its runner, in bytes.</summary>
    public long? MemoryBytes { get; init; }

    /// <summary>The most tasks at once this machine is likely to handle well (<see cref="SlotAdvice.SuggestedMax"/>); null when unknown.</summary>
    public int? SuggestedSlots => SlotAdvice.SuggestedMax(CpuCount, MemoryBytes);

    /// <summary>Slots in use right now.</summary>
    public int SlotsBusy { get; init; }

    /// <summary>Capabilities its <c>mbus</c> found at the last scan.</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = [];

    /// <summary>
    /// Capabilities its owner added (the original issue), such as <c>tier:gpu</c> or <c>site:office</c>: only the kinds in
    /// <see cref="CapabilityNames.OwnerPrefixes"/>. Its agents offer them to role requirements with <see cref="Capabilities"/>.
    /// </summary>
    public IReadOnlyList<string> CustomCapabilities { get; set; } = [];

    /// <summary>Agents found on it at the last scan.</summary>
    public IReadOnlyList<DetectedAgent> Agents { get; set; } = [];

    /// <summary>Run isolation and credentials, as its runner last reported (the original issue); null until a runner reports it.</summary>
    public RunIsolationReport? Isolation { get; init; }

    /// <summary>How its runner's last update ended, as the runner reported when it next started (the original issue); null until one does.</summary>
    public Releases.RunnerUpdateReport? LastUpdate { get; init; }

    /// <summary>Whether its runner's mbus can update itself, and why not (the original issue); null until a runner says.</summary>
    public Releases.RunnerUpdateAbility? Updates { get; init; }

    /// <summary>The update under way on it (the original issue); null when none is.</summary>
    public Releases.MachineUpdateStatus? Update { get; init; }

    /// <summary>Automatic updates (the original issue) leave it on the version it runs; it is still updated when someone asks.</summary>
    public bool KeepVersion { get; init; }

    /// <summary>Whose tasks it runs.</summary>
    public required AcceptsWorkFrom AcceptsWorkFrom { get; init; }

    /// <summary>Which repository credential runs get.</summary>
    public RepoCredentialMode RepoCredentialMode { get; set; } = RepoCredentialMode.PerRunToken;

    /// <summary>Most it may spend per day on work for other people, in US dollars (estimated); null for no ceiling.</summary>
    public decimal? DailySpendCeilingUsd { get; init; }

    /// <summary>Fingerprint (SHA-256, lowercase hex) of its device public key.</summary>
    public required string KeyFingerprint { get; init; }

    /// <summary>Whether its runner is connected now.</summary>
    public bool Online { get; init; }

    /// <summary>Held until an admin approves it (unmanaged device under the 'approve runners from unmanaged devices' policy).</summary>
    public bool PendingApproval { get; init; }

    /// <summary>When it enrolled.</summary>
    public required DateTimeOffset EnrolledAt { get; init; }

    /// <summary>When it was last heard from: any signed request counts, <c>mbus doctor</c>'s own too.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>
    /// When its runner was last connected (the original issue): written when one connects and disconnects, and by its heartbeats (at
    /// most once a minute). Other signed requests don't change it. Null when no runner has connected.
    /// </summary>
    public DateTimeOffset? RunnerSeenAt { get; init; }
}

/// <summary>How many tasks at once a machine is likely to handle well (the original issue).</summary>
public static class SlotAdvice
{
    /// <summary>Processors each task is allowed for: the agent plus the builds and tests it runs.</summary>
    public const int ProcessorsPerTask = 2;

    /// <summary>Memory each task is allowed for (a coding agent and its tools), in bytes.</summary>
    public const long MemoryPerTask = 2L << 30;

    /// <summary>Memory kept back for the system and its owner's own work, in bytes.</summary>
    public const long MemoryReserved = 4L << 30;

    /// <summary>The suggested most tasks at once (at least 1), or null when the machine didn't report its processors and memory.</summary>
    /// <param name="cpuCount">Logical processors.</param>
    /// <param name="memoryBytes">Memory, in bytes.</param>
    public static int? SuggestedMax(int? cpuCount, long? memoryBytes) =>
        cpuCount is int cpus and > 0 && memoryBytes is long memory and > 0
            ? Math.Max(1, (int)Math.Min(cpus / ProcessorsPerTask, (memory - MemoryReserved) / MemoryPerTask))
            : null;

    /// <summary>The firm upper bound on tasks at once for a machine whose owner set no cap (the original issue).</summary>
    public const int MostForMachineBound = 16;

    /// <summary>
    /// The most tasks at once the dashboard may choose for a machine (the original issue): twice the
    /// suggestion, at least 2, never more than <see cref="MostForMachineBound"/>. The suggestion allows for builds
    /// and tests on every task at once, while an agent mostly waits for its model, so twice it still works; the
    /// dashboard warns above the suggestion. The bound keeps a large machine from opening more agent sessions
    /// than a person can follow or a model account allows.
    /// </summary>
    /// <param name="cpuCount">Logical processors.</param>
    /// <param name="memoryBytes">Memory, in bytes.</param>
    public static int MostForMachine(int? cpuCount, long? memoryBytes) =>
        Math.Clamp(2 * (SuggestedMax(cpuCount, memoryBytes) ?? 1), 2, MostForMachineBound);
}

/// <summary>One coding agent on one machine, in the agent registry. Agents hold roles.</summary>
public sealed record AgentInfo
{
    /// <summary>Roles whose messages this agent cannot open until a key check succeeds.</summary>
    public IReadOnlyList<string> UnreadableRoles { get; set; } = [];

    /// <summary>The signed-in account's last reported usage windows.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Runner.AgentCapacity? Capacity { get; init; }

    /// <summary>Agent id (<c>agt_…</c>).</summary>
    public required string AgentId { get; init; }

    /// <summary>Its machine.</summary>
    public required string MachineId { get; init; }

    /// <summary>The machine's owner.</summary>
    public required string OwnerSubjectId { get; init; }

    /// <summary>Which agent.</summary>
    public required AgentKind Kind { get; init; }

    /// <summary>Its version.</summary>
    public string? Version { get; init; }

    /// <summary>Model vendor, when known.</summary>
    public string? Vendor { get; init; }

    /// <summary>Display name, for example <c>Mac mini · Claude Code</c>.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The machine's location label.</summary>
    public string? Location { get; init; }

    /// <summary>Roles it holds.</summary>
    public IReadOnlyList<string> Roles { get; set; } = [];

    /// <summary>Whether it can take work right now.</summary>
    public required AgentPresence Presence { get; init; }

    /// <summary>Free slots on its machine.</summary>
    public int SlotsFree { get; init; }

    /// <summary>When it was last heard from.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>What it offers to role requirements: its machine's capabilities (OS, tools such as <c>tool:dotnet</c>) and its own <c>agent:</c> kind.</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = [];

    /// <summary>Why it can't take work now (see <see cref="DetectedAgent.NotReady"/>); null when it can.</summary>
    public string? NotReady { get; init; }

    /// <summary>The model it runs when a task names none, as its machine reported (the original issue); null when unknown.</summary>
    public string? DefaultModel { get; init; }

    /// <summary>The models it reported it can run (the original issue); each is also a capability <c>model:&lt;id&gt;</c>.</summary>
    public IReadOnlyList<string> Models { get; set; } = [];

    /// <summary>Its agent couldn't list every model it can run, so <see cref="Models"/> may be missing some (the original issue).</summary>
    public bool ModelsPartial { get; init; }
}
