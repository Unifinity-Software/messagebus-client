using System.Text.Json.Serialization;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;

namespace MessageBus.Contracts.Roles;

/// <summary>How a message to a role reaches the role's agents.</summary>
public enum DeliveryMode
{
    /// <summary>Exactly one agent gets it (the default).</summary>
    [JsonStringEnumMemberName("compete")] Compete,
    /// <summary>N chosen agents each get their own copy (optionally from distinct vendors).</summary>
    [JsonStringEnumMemberName("broadcast")] Broadcast,
    /// <summary>Offered to every agent; the first N to accept get it, the rest see it as taken.</summary>
    [JsonStringEnumMemberName("first-n")] FirstN,
    /// <summary>One agent answers and the answer returns to the asker.</summary>
    [JsonStringEnumMemberName("request-reply")] RequestReply,
}

/// <summary>What happens to a role's new work once its daily budget is used (the original issue).</summary>
public enum BudgetAction
{
    /// <summary>Messages wait in the queue until the budget resets (midnight UTC) or is raised.</summary>
    [JsonStringEnumMemberName("hold")] Hold,
    /// <summary>Messages go to dead letters, where they can be replayed later.</summary>
    [JsonStringEnumMemberName("dead-letter")] DeadLetter,
    /// <summary>
    /// Work goes on, run with the role's cheaper model (<see cref="RoleLimits.EconomyModel"/>) until the budget resets
    /// (the original issue). The runner passes that model to the agent CLI; what those runs cost still counts toward the day's spend.
    /// </summary>
    [JsonStringEnumMemberName("economy")] Economy,
}

/// <summary>When a broadcast or first-N thread counts as answered (the original issue).</summary>
public enum ReplyMode
{
    /// <summary>When every copy has finished (the default).</summary>
    [JsonStringEnumMemberName("wait-for-all")] WaitForAll,
    /// <summary>When the first copy succeeds; the copies still waiting are withdrawn and the running ones asked to stop.</summary>
    [JsonStringEnumMemberName("first-reply")] FirstReply,
}

/// <summary>Who answers requests to join a role that requires approval (the original issue).</summary>
public enum JoinApprovers
{
    /// <summary>The role's owners, or the workspace's admins when it has none (the default).</summary>
    [JsonStringEnumMemberName("owners")] Owners,
    /// <summary>Only people whose workspace role may approve joins (admins and owners of the workspace).</summary>
    [JsonStringEnumMemberName("workspace-admins")] WorkspaceAdmins,
    /// <summary>
    /// The workspace members who joined through one Entra group (<see cref="RoleAccess.JoinApproverGroupId"/>), and the
    /// workspace's admins (the original issue). The workspace's admins are asked when nobody has joined through the group yet.
    /// </summary>
    [JsonStringEnumMemberName("group")] Group,
}

/// <summary>How retries are spaced.</summary>
public enum BackoffKind
{
    /// <summary>Doubling from the initial delay up to the maximum.</summary>
    [JsonStringEnumMemberName("exponential")] Exponential,
    /// <summary>The initial delay every time.</summary>
    [JsonStringEnumMemberName("fixed")] Fixed,
}

/// <summary>How the runner prepares the task's working tree.</summary>
public enum CheckoutMode
{
    /// <summary>A fresh git worktree from a clone of the referenced repository.</summary>
    [JsonStringEnumMemberName("clone")] Clone,
    /// <summary>A fresh worktree from the message's git bundle attachment.</summary>
    [JsonStringEnumMemberName("bundle")] Bundle,
    /// <summary>An empty directory.</summary>
    [JsonStringEnumMemberName("empty")] Empty,
}

/// <summary>Whether Codex on Windows uses its own sandbox.</summary>
public enum CodexWindowsSandbox
{
    /// <summary>Keep Codex's sandbox (the default).</summary>
    [JsonStringEnumMemberName("sandbox")] Sandbox,
    /// <summary>Use the runner's isolation, as other agents on Windows do.</summary>
    [JsonStringEnumMemberName("same-as-other-agents")] SameAsOtherAgents,
}

/// <summary>What the runner keeps of the agent's output.</summary>
public enum LogCapture
{
    /// <summary>Nothing.</summary>
    [JsonStringEnumMemberName("none")] None,
    /// <summary>The last lines, on the machine only.</summary>
    [JsonStringEnumMemberName("local")] Local,
    /// <summary>The full output, encrypted and uploaded to the run-logs container.</summary>
    [JsonStringEnumMemberName("upload")] Upload,
}

/// <summary>What a run may reach over the network.</summary>
public enum NetworkAccess
{
    /// <summary>Nothing.</summary>
    [JsonStringEnumMemberName("none")] None,
    /// <summary>Only the hosts in <see cref="PermissionProfile.AllowedHosts"/>.</summary>
    [JsonStringEnumMemberName("allowlist")] Allowlist,
    /// <summary>Anything.</summary>
    [JsonStringEnumMemberName("open")] Open,
}

/// <summary>Where a version of a role's instructions came from.</summary>
public enum InstructionSource
{
    /// <summary>Saved in the dashboard and signed by an admin's device key.</summary>
    [JsonStringEnumMemberName("dashboard")] Dashboard,
    /// <summary>Taken from <c>.messagebus/roles/&lt;name&gt;.md</c> in the linked repository at a commit.</summary>
    [JsonStringEnumMemberName("repo")] Repo,
    /// <summary>The built-in default.</summary>
    [JsonStringEnumMemberName("built-in")] BuiltIn,
}

/// <summary>State of a request to join a role.</summary>
public enum JoinRequestStatus
{
    /// <summary>Waiting for a role owner.</summary>
    [JsonStringEnumMemberName("pending")] Pending,
    /// <summary>Approved; remembered for this agent and role.</summary>
    [JsonStringEnumMemberName("approved")] Approved,
    /// <summary>Rejected.</summary>
    [JsonStringEnumMemberName("rejected")] Rejected,
}

/// <summary>What an alert rule watches.</summary>
public enum AlertMetric
{
    /// <summary>Messages waiting.</summary>
    [JsonStringEnumMemberName("queue-depth")] QueueDepth,
    /// <summary>Age of the oldest waiting message, in seconds.</summary>
    [JsonStringEnumMemberName("oldest-waiting-seconds")] OldestWaitingSeconds,
    /// <summary>Messages dead-lettered in the window.</summary>
    [JsonStringEnumMemberName("dead-letters")] DeadLetters,
    /// <summary>Failed tasks as a percentage of finished ones in the window.</summary>
    [JsonStringEnumMemberName("failure-rate")] FailureRate,
    /// <summary>Minutes with no online agent holding the role.</summary>
    [JsonStringEnumMemberName("no-holder-minutes")] NoHolderMinutes,
    /// <summary>Percentage of a project's budget spent.</summary>
    [JsonStringEnumMemberName("budget-percent")] BudgetPercent,
}

/// <summary>How a role's messages are delivered (Roles page, Delivery tab).</summary>
public sealed record DeliverySettings
{
    /// <summary>Preferred coding agent kinds, in order; empty means no preference.</summary>
    public IReadOnlyList<Machines.AgentKind> PreferredAgentKinds { get; set; } = [];

    /// <summary>Seconds to wait for each preferred kind before allowing the next kind (0–3600).</summary>
    public int AgentKindWaitSeconds { get; set; } = 30;

    /// <summary>The mode.</summary>
    public DeliveryMode Mode { get; set; } = DeliveryMode.Compete;

    /// <summary>N, for broadcast and first-N.</summary>
    public int? Count { get; init; }

    /// <summary>For broadcast: pick agents from different model vendors.</summary>
    public bool DistinctVendors { get; init; }

    /// <summary>For broadcast and first-N: whether the thread waits for every copy or the first success wins (the original issue).</summary>
    public ReplyMode Replies { get; set; } = ReplyMode.WaitForAll;
}

/// <summary>How retries are spaced.</summary>
public sealed record RetryBackoff
{
    /// <summary>Exponential or fixed.</summary>
    public BackoffKind Kind { get; set; } = BackoffKind.Exponential;

    /// <summary>First delay, in seconds.</summary>
    public int InitialSeconds { get; set; } = 30;

    /// <summary>Longest delay, in seconds.</summary>
    public int MaxSeconds { get; set; } = 900;
}

/// <summary>A role's limits (Roles page, Limits and alerts tab). Defaults are the design's v1 values.</summary>
public sealed record RoleLimits
{
    /// <summary>Deliveries before a message is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long a delivery is leased before it returns to the queue, unless a heartbeat extends it.</summary>
    public int LeaseTimeoutSeconds { get; set; } = 1800;

    /// <summary>How often runners heartbeat a lease.</summary>
    public int HeartbeatSeconds { get; set; } = 60;

    /// <summary>How long an undelivered message lives before it expires.</summary>
    public int MessageLifetimeSeconds { get; set; } = 86400;

    /// <summary>How retries are spaced.</summary>
    public RetryBackoff RetryBackoff { get; set; } = new();

    /// <summary>Priority given to messages that don't set one.</summary>
    public Priority DefaultPriority { get; set; } = Priority.Normal;

    /// <summary>How long a dedupe key is remembered.</summary>
    public int DedupeWindowSeconds { get; set; } = 86400;

    /// <summary>Largest attachment accepted; 0 when the role doesn't accept attachments.</summary>
    public long MaxAttachmentBytes { get; set; } = MessageLimits.DefaultMaxAttachmentBytes;

    /// <summary>Most the role's runs may cost per UTC day, in US dollars (estimated from reported usage); null for no budget (the original issue).</summary>
    public decimal? DailyBudgetUsd { get; init; }

    /// <summary>What happens to new work once <see cref="DailyBudgetUsd"/> is used.</summary>
    public BudgetAction WhenBudgetReached { get; set; } = BudgetAction.Hold;

    /// <summary>Most of the role's tasks running at once across the workspace; null for no limit (the original issue).</summary>
    public int? MaxConcurrentRuns { get; init; }

    /// <summary>Most open tasks one thread may send to this role; null for no limit (the original issue).</summary>
    public int? MaxFanOutPerThread { get; init; }

    /// <summary>
    /// Most one run may cost, in US dollars (estimated from the usage the agent reports as it works); null for no cap
    /// (the original issue). The runner stops a run that passes it and settles it as failed. Agents that report usage only when they
    /// finish (Codex) can't be stopped early.
    /// </summary>
    public decimal? MaxCostPerRunUsd { get; init; }

    /// <summary>
    /// The model runs use once the daily budget is used, when <see cref="WhenBudgetReached"/> is
    /// <see cref="BudgetAction.Economy"/> (the original issue): the name the agent CLI takes (for example <c>claude-haiku-4-5</c>).
    /// </summary>
    public string? EconomyModel { get; init; }
}

/// <summary>An alert rule, for a role, a project or the whole workspace (the original issue; roles show theirs on the Limits and alerts tab).</summary>
public sealed record AlertRule
{
    /// <summary>Rule id (<c>alr_…</c>).</summary>
    public required string RuleId { get; init; }

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>The role it watches, if role-scoped.</summary>
    public string? Role { get; init; }

    /// <summary>The project it watches, if project-scoped.</summary>
    public string? ProjectId { get; init; }

    /// <summary>What it watches.</summary>
    public required AlertMetric Metric { get; init; }

    /// <summary>Fires when the metric reaches this value.</summary>
    public required double Threshold { get; init; }

    /// <summary>Window the metric is measured over, in seconds.</summary>
    public int WindowSeconds { get; set; } = 300;

    /// <summary>Alert destinations to notify (ids from the original issue).</summary>
    public IReadOnlyList<string> DestinationIds { get; set; } = [];

    /// <summary>Whether it is on.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Who owns a role and who may join it (Access tab).</summary>
public sealed record RoleAccess
{
    /// <summary>Who answers join requests when <see cref="RequireApprovalToJoin"/> is on (the original issue).</summary>
    public JoinApprovers JoinApprovers { get; set; } = JoinApprovers.Owners;

    /// <summary>For <see cref="JoinApprovers.Group"/>: the Entra group (object id, one of the workspace's group mappings) whose members answer (the original issue).</summary>
    public string? JoinApproverGroupId { get; init; }

    /// <summary>
    /// Each run waits for the owner of the machine it lands on to approve it before the agent starts (the original issue). A run the
    /// owner declines (or leaves unanswered) goes back to the queue for another machine.
    /// </summary>
    public bool RequireRunApproval { get; init; }

    /// <summary>Owners (subject ids): they approve joins and edit the role.</summary>
    public IReadOnlyList<string> Owners { get; set; } = [];

    /// <summary>Joining needs an owner's approval (otherwise open).</summary>
    public bool RequireApprovalToJoin { get; init; }

    /// <summary>Who may send to it (subject ids, group ids or role names); empty for anyone in the workspace.</summary>
    public IReadOnlyList<string> AllowedSenders { get; set; } = [];
}

/// <summary>What a run may do; enforced by the runner, not just passed to the agent.</summary>
public sealed record PermissionProfile
{
    /// <summary>Profile name, for example one of <see cref="PermissionProfiles"/>.</summary>
    public required string Name { get; init; }

    /// <summary>Agent tools allowed (agent-specific names); empty for the agent's defaults.</summary>
    public IReadOnlyList<string> AllowedTools { get; set; } = [];

    /// <summary>Network access.</summary>
    public NetworkAccess Network { get; set; } = NetworkAccess.Allowlist;

    /// <summary>Hosts reachable when <see cref="Network"/> is <see cref="NetworkAccess.Allowlist"/>.</summary>
    public IReadOnlyList<string> AllowedHosts { get; set; } = [];

    /// <summary>May edit files in the worktree.</summary>
    public bool CanEditFiles { get; set; } = true;

    /// <summary>May run shell commands.</summary>
    public bool CanRunCommands { get; set; } = true;

    /// <summary>May push branches (never to protected branches; never approve or merge).</summary>
    public bool CanPush { get; init; }

    /// <summary>
    /// May run the repository's tests even when <see cref="CanRunCommands"/> is off (the original issue): the runner finds the test
    /// commands (<c>dotnet test</c>, <c>npm test</c> …) in the working copy and lets the agent run those alone.
    /// </summary>
    public bool CanRunTests { get; init; }
}

/// <summary>Names of the built-in permission profiles.</summary>
public static class PermissionProfiles
{
    /// <summary>Edits files and runs commands in its worktree; network by allowlist; no push.</summary>
    public const string Worker = "worker";
    /// <summary>Reads only: no edits, no commands beyond read-only ones.</summary>
    public const string Reviewer = "reviewer";
    /// <summary>A resumed orchestrator: mbus tools and read-only repository access only.</summary>
    public const string Orchestrator = "orchestrator";

    /// <summary>The commands a <see cref="Reviewer"/> run may use (Claude Code rule syntax): git's read-only commands (the original issue).</summary>
    public static IReadOnlyList<string> ReviewerCommands { get; } =
        ["Bash(git log:*)", "Bash(git diff:*)", "Bash(git show:*)", "Bash(git status:*)", "Bash(git blame:*)"];

    /// <summary>
    /// The settings a role with the built-in profile <paramref name="name"/> starts with (the original issue): a worker edits and runs
    /// commands; a reviewer reads, with git's read-only commands only; an orchestrator reads, with no network. Other
    /// names start with the record's defaults.
    /// </summary>
    public static PermissionProfile Defaults(string name) => name switch
    {
        Reviewer => new PermissionProfile { Name = name, CanEditFiles = false, CanRunCommands = false, AllowedTools = [.. ReviewerCommands] },
        Orchestrator => new PermissionProfile { Name = name, CanEditFiles = false, CanRunCommands = false, Network = NetworkAccess.None },
        _ => new PermissionProfile { Name = name },
    };

    /// <summary>
    /// <paramref name="profile"/>, or its built-in <see cref="Defaults"/> when it is a reviewer or orchestrator profile
    /// still holding the record's untouched defaults (roles stored before the original issue got those: edits and commands on).
    /// </summary>
    public static PermissionProfile WithBuiltInDefaults(PermissionProfile profile)
    {
        var untouched = profile is { CanEditFiles: true, CanRunCommands: true, CanPush: false, CanRunTests: false, Network: NetworkAccess.Allowlist }
            && (profile.AllowedTools?.Count ?? 0) == 0 && (profile.AllowedHosts?.Count ?? 0) == 0;
        return untouched && profile.Name is Reviewer or Orchestrator ? Defaults(profile.Name) : profile;
    }
}

/// <summary>How the runner runs a task for the role (Run environment tab).</summary>
public sealed record RunEnvironment
{
    /// <summary>Codex's Windows sandbox mode; the machine owner can require the sandbox.</summary>
    public CodexWindowsSandbox CodexOnWindows { get; set; } = CodexWindowsSandbox.Sandbox;

    /// <summary>Named tools from the agent's own settings kept for runs; empty turns this off.</summary>
    public IReadOnlyList<string> KeepAgentTools { get; set; } = [];

    /// <summary>How the worktree is prepared.</summary>
    public CheckoutMode Checkout { get; set; } = CheckoutMode.Bundle;

    /// <summary>A command run in the worktree before the agent starts (for example <c>dotnet restore</c>).</summary>
    public string? SetupCommand { get; init; }

    /// <summary>The run is stopped after this many seconds.</summary>
    public int TimeoutSeconds { get; set; } = 3600;

    /// <summary>What is kept of the agent's output.</summary>
    public LogCapture Logs { get; set; } = LogCapture.Local;

    /// <summary>What the run may do.</summary>
    public PermissionProfile PermissionProfile { get; set; } = new() { Name = PermissionProfiles.Worker };

    /// <summary>Which model the role's tasks run on (the original issue).</summary>
    public RoleModels Models { get; set; } = new();
}

/// <summary>
/// A role's models (the original issue): the model each agent kind runs when a task names none, and the models a task may name.
/// The service enforces both; the role's cheaper model (<see cref="RoleLimits.EconomyModel"/>) still takes over once its
/// daily budget is used.
/// </summary>
public sealed record RoleModels
{
    /// <summary>The model to run per agent kind when a task names none; an agent kind not listed runs its own default.</summary>
    public IReadOnlyList<AgentModelDefault> Defaults { get; set; } = [];

    /// <summary>The models a task may name; empty allows any model an agent reports.</summary>
    public IReadOnlyList<string> Allowed { get; set; } = [];

    /// <summary>The default for <paramref name="kind"/>, or null.</summary>
    public string? DefaultFor(Machines.AgentKind kind) => (Defaults ?? []).FirstOrDefault(d => d.Agent == kind)?.Model;

    /// <summary>Whether a task may name <paramref name="model"/>.</summary>
    public bool Allows(string model) => (Allowed ?? []).Count == 0 || Allowed.Contains(model, StringComparer.OrdinalIgnoreCase);
}

/// <summary>The model one agent kind runs for a role when a task names none (the original issue).</summary>
public sealed record AgentModelDefault
{
    /// <summary>The agent kind.</summary>
    public required Machines.AgentKind Agent { get; init; }

    /// <summary>The model id as the agent takes it, for example <c>claude-opus-5-5</c>.</summary>
    public required string Model { get; init; }
}

/// <summary>One version of a role's instructions. Each save is a new version; one is live.</summary>
public sealed record InstructionVersion
{
    /// <summary>The role.</summary>
    public required string Role { get; init; }

    /// <summary>Version number, 1 for the first.</summary>
    public required int Version { get; init; }

    /// <summary>The instructions, markdown.</summary>
    public required string Content { get; init; }

    /// <summary>SHA-256 of the UTF-8 content, lowercase hex. The signature covers it.</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>Where it came from.</summary>
    public required InstructionSource Source { get; init; }

    /// <summary>For repo instructions: the repository and commit, for example <c>owner/repo@a1b2c3d</c>.</summary>
    public string? SourceRef { get; init; }

    /// <summary>Who saved or signed it (subject id).</summary>
    public string? SignedBy { get; init; }

    /// <summary>The signing device key's id.</summary>
    public string? SigningKeyId { get; init; }

    /// <summary>Signature over <see cref="ContentSha256"/>, base64url. Runners verify it before use.</summary>
    public string? Signature { get; init; }

    /// <summary>When it was saved.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Whether this is the live version.</summary>
    public bool Live { get; init; }
}

/// <summary>An agent role with every setting on the Roles page tabs.</summary>
public sealed record AgentRole
{
    /// <summary>Name (see the role catalog).</summary>
    public required string Name { get; init; }

    /// <summary>The workspace.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Label for display.</summary>
    public required string DisplayName { get; init; }

    /// <summary>What the role does, one or two sentences (shown to senders by <c>describe_role</c>).</summary>
    public string? Description { get; init; }

    /// <summary>One of the role catalog.</summary>
    public bool BuiltIn { get; init; }

    /// <summary>Delivery settings.</summary>
    public DeliverySettings Delivery { get; set; } = new();

    /// <summary>Capabilities an agent's machine must have to be offered its messages.</summary>
    public IReadOnlyList<string> RequiredCapabilities { get; set; } = [];

    /// <summary>Limits.</summary>
    public RoleLimits Limits { get; set; } = new();

    /// <summary>Alert rules on this role.</summary>
    public IReadOnlyList<AlertRule> AlertRules { get; set; } = [];

    /// <summary>Owners and joining.</summary>
    public RoleAccess Access { get; set; } = new();

    /// <summary>How runs are set up.</summary>
    public RunEnvironment RunEnvironment { get; set; } = new();

    /// <summary>The live instruction version.</summary>
    public int LiveInstructionVersion { get; init; }

    /// <summary>When it was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it was last changed.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A request from an agent to join a role that requires approval.</summary>
public sealed record RoleJoinRequest
{
    /// <summary>Request id (<c>rjr_…</c>).</summary>
    public required string RequestId { get; init; }

    /// <summary>The role.</summary>
    public required string Role { get; init; }

    /// <summary>The agent asking.</summary>
    public required string AgentId { get; init; }

    /// <summary>Its machine.</summary>
    public required string MachineId { get; init; }

    /// <summary>The agent's owner.</summary>
    public required string RequestedBy { get; init; }

    /// <summary>State.</summary>
    public required JoinRequestStatus Status { get; init; }

    /// <summary>When it was asked.</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>Who decided.</summary>
    public string? DecidedBy { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset? DecidedAt { get; init; }
}

/// <summary>One role as the <c>directory</c> tool lists it: who holds it and how much room there is.</summary>
public sealed record DirectoryRole
{
    /// <summary>Role name.</summary>
    public required string Name { get; init; }

    /// <summary>Label.</summary>
    public required string DisplayName { get; init; }

    /// <summary>What it does.</summary>
    public string? Description { get; init; }

    /// <summary>Built in or custom.</summary>
    public bool BuiltIn { get; init; }

    /// <summary>How messages to it are delivered.</summary>
    public required DeliverySettings Delivery { get; init; }

    /// <summary>Capabilities its agents need.</summary>
    public IReadOnlyList<string> RequiredCapabilities { get; set; } = [];

    /// <summary>Agents holding it.</summary>
    public IReadOnlyList<AgentInfo> Agents { get; set; } = [];

    /// <summary>Holders online now (their runner is connected), busy or not.</summary>
    public int AgentsOnline { get; init; }

    /// <summary>Of <see cref="AgentsOnline"/>, those with a free slot now (could start a task at once).</summary>
    public int AgentsFree { get; init; }

    /// <summary>Free slots across its online holders.</summary>
    public int FreeSlots { get; init; }

    /// <summary>Messages waiting.</summary>
    public int QueueDepth { get; init; }
}
