using System.Text.Json.Serialization;
using Groundhog.Protocol;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Releases;
using MessageBus.Contracts.Roles;

namespace MessageBus.Contracts.Runner;

/// <summary>
/// The runner connection (the original issue, the original issue). A runner opens an outbound WebSocket to
/// <see cref="ConnectPath"/> with subprotocol <see cref="SubProtocol"/>, authenticated as its
/// machine (<see cref="Security.AuthSchemes.Machine"/>). HTTP/2 then runs over the socket
/// (Groundhog's <see cref="WebSocketDuplexStream"/>) with the <b>service as the HTTP client</b>: the
/// service calls the runner's <see cref="RunnerRoutes"/>. The runner calls the service back over plain
/// HTTPS on <see cref="ServiceRoutes"/>. Queues stay the durable record: the socket is the fast path,
/// and a reconnecting runner is offered whatever is waiting.
/// </summary>
/// <remarks>
/// Taken from <c>Groundhog.Protocol</c> as is: the ping interval and silence timeout
/// (<see cref="ConnectionPool"/>), the reconnect schedule (<see cref="ReconnectBackoff"/>), the
/// WebSocket stream adapter and the close-code conventions (<see cref="RunnerCloseCodes"/>). Not taken:
/// Groundhog's subprotocol, paths and <c>X-Groundhog-*</c> headers (tunnel-specific; ours are
/// <see cref="SubProtocol"/>, <see cref="ConnectPath"/> and <see cref="Http.MbusHeaders"/>), and its
/// <c>GroundhogKey</c> credential (runners prove possession of a device key instead).
/// </remarks>
public static class RunnerProtocol
{
    /// <summary>Protocol version.</summary>
    public const int Version = 1;

    /// <summary>WebSocket subprotocol.</summary>
    public const string SubProtocol = "mbus.v1";

    /// <summary>WebSocket connect path on the service.</summary>
    public const string ConnectPath = "/api/runner/connect";

    /// <summary>The service pings this often (Groundhog's value).</summary>
    public static TimeSpan PingInterval => ConnectionPool.PingInterval;

    /// <summary>A connection silent for this long is dropped and the machine shown offline (Groundhog's value).</summary>
    public static TimeSpan SilenceTimeout => ConnectionPool.SilenceTimeout;

    /// <summary>Delay before reconnect attempt <paramref name="attempt"/> (1, 2, 4 … 30 s with jitter; Groundhog's schedule).</summary>
    /// <param name="attempt">Attempt number, starting at 1.</param>
    /// <param name="random">Jitter source.</param>
    public static TimeSpan ReconnectDelay(int attempt, Random random) => ReconnectBackoff.GetDelay(attempt, random);
}

/// <summary>
/// WebSocket close codes on the runner connection. Values shared with Groundhog keep Groundhog's
/// meaning; <see cref="UpdateRequired"/> and <see cref="PendingApproval"/> are ours (Groundhog has no
/// equivalent: it neither enforces a minimum agent version nor holds devices for approval). Draining is
/// a request (<see cref="RunnerRoutes.Drain"/>), not a close.
/// </summary>
public static class RunnerCloseCodes
{
    /// <summary>Normal close (the runner is stopping).</summary>
    public const int Normal = GroundhogCloseCodes.Normal;
    /// <summary>The service is restarting; reconnect with backoff.</summary>
    public const int GoingAway = 1001;
    /// <summary>The machine's credential was revoked or its owner left the workspace. Stop; don't reconnect.</summary>
    public const int CredentialRevoked = GroundhogCloseCodes.CredentialRevoked;
    /// <summary>The machine was removed from the workspace. Stop; don't reconnect.</summary>
    public const int MachineRemoved = GroundhogCloseCodes.TunnelGone;
    /// <summary>Another runner session for this machine connected; this one should exit.</summary>
    public const int SessionReplaced = GroundhogCloseCodes.SessionReplaced;
    /// <summary>Too many connections for this machine; reconnect with backoff.</summary>
    public const int TooManyConnections = GroundhogCloseCodes.TooManyConnections;
    /// <summary>This <c>mbus</c> is older than the service accepts (<see cref="Http.MbusHeaders.MinClientVersion"/>); update, then reconnect.</summary>
    public const int UpdateRequired = 4426;
    /// <summary>The machine is held for admin approval (unmanaged device); reconnect later with a long backoff.</summary>
    public const int PendingApproval = 4403;

    /// <summary>Codes after which the runner must not reconnect.</summary>
    /// <param name="code">The close code received.</param>
    public static bool IsTerminal(int code) => code is CredentialRevoked or MachineRemoved or SessionReplaced;
}

/// <summary>HTTP/2 routes the runner serves over its socket; the service calls them.</summary>
public static class RunnerRoutes
{
    /// <summary>GET setup status; POST an empty object to request the fixed Windows Git setup.</summary>
    public const string ToolSetup = "/tool-setup";
    /// <summary>POST <see cref="TaskDelivery"/> → <see cref="DeliveryDecision"/>. Answer within seconds; the run settles later on <see cref="ServiceRoutes.Settle"/>.</summary>
    public const string Deliver = "/deliveries";
    /// <summary>POST <see cref="CancelDelivery"/>: stop that run (within 5 seconds) and settle it as cancelled.</summary>
    public const string Cancel = "/deliveries/cancel";
    /// <summary>POST <see cref="DrainRequest"/>: take no new work, let running tasks finish.</summary>
    public const string Drain = "/drain";
    /// <summary>POST <see cref="RescanRequest"/>: scan for agents again and send inventory.</summary>
    public const string Rescan = "/rescan";
    /// <summary>POST <see cref="UpdateRequest"/>: update <c>mbus</c> now (after draining).</summary>
    public const string Update = "/update";
    /// <summary>GET → <see cref="RunnerStatus"/>: what the runner is doing.</summary>
    public const string Status = "/status";
    /// <summary>
    /// POST <see cref="SlotsRequest"/> → <see cref="RunnerStatus"/>: how many tasks to run at once, set from the
    /// dashboard (the original issue), never above the runner's ceiling (a maximum for the machine's size, the original issue); null: its own default (1). Raising takes effect at once; lowering lets
    /// running tasks finish and only holds back new ones. Sent on every connect and whenever the setting changes.
    /// </summary>
    public const string Slots = "/slots";
    /// <summary>
    /// POST <see cref="RunnerWorkPolicy"/>: whose work to run, sent when the machine's owner changes it (the original issue). The
    /// runner also reads it itself (<see cref="ServiceRoutes.Policy"/>) when it starts and whenever it connects.
    /// </summary>
    public const string Policy = "/policy";

    /// <summary>
    /// POST <see cref="ConnectAgentRequest"/> → <see cref="ConnectAgentResult"/> (the original issue): set up one agent to use mbus,
    /// as <c>mbus connect</c> does, after the person confirms on the machine itself. The runner answers at once (asked,
    /// or why it can't ask); the change happens only on a yes typed in the runner's own terminal, never silently.
    /// </summary>
    public const string Connect = "/connect";
}

/// <summary>Routes on the service the runner calls over HTTPS (machine-authenticated).</summary>
public static class ServiceRoutes
{
    /// <summary>POST an immutable Git plan to request or poll owner approval.</summary>
    public const string ToolSetupApproval = "/api/runner/tool-setup/approval";
    /// <summary>POST the plan and approval id to consume its one-use execution permission.</summary>
    public const string ToolSetupClaim = "/api/runner/tool-setup/claim";
    /// <summary>POST the final installation report for a claimed plan.</summary>
    public const string ToolSetupResult = "/api/runner/tool-setup/result";
    /// <summary>POST an empty object to request setup through this machine's connected runner.</summary>
    public const string ToolSetupStart = "/api/runner/tool-setup/start";
    /// <summary>GET setup status from this machine's connected runner.</summary>
    public const string ToolSetupStatus = "/api/runner/tool-setup/status";
    /// <summary>POST <see cref="RunnerHeartbeat"/> → <see cref="HeartbeatAck"/>: keep leases alive (every role's heartbeat interval).</summary>
    public const string Heartbeat = "/api/runner/heartbeat";
    /// <summary>POST <see cref="RunnerProgress"/>: progress on a delivery.</summary>
    public const string Progress = "/api/runner/progress";
    /// <summary>POST <see cref="RunnerSettle"/>: how a delivery ended.</summary>
    public const string Settle = "/api/runner/settle";
    /// <summary>PUT <see cref="MachineInventory"/>: agents and capabilities after a scan.</summary>
    public const string Inventory = "/api/runner/inventory";
    /// <summary>
    /// POST <see cref="RunnerRepoCredentialRequest"/> → 200 <see cref="RunnerRepoCredential"/>, or 204
    /// when the workspace has no connection that can issue one (the original issue).
    /// </summary>
    public const string RepoCredential = "/api/runner/repo-credential";
    /// <summary>
    /// POST <see cref="RunnerTelemetryCredentialRequest"/> → 200 <see cref="RunnerTelemetryCredential"/>, or 204 when the
    /// workspace sends no telemetry anywhere (the original issue).
    /// </summary>
    public const string TelemetryCredential = "/api/runner/telemetry-credential";
    /// <summary>GET → <see cref="RunnerWorkPolicy"/>: the calling machine's work policy, with its groups' members listed (the original issue).</summary>
    public const string Policy = "/api/runner/policy";
    /// <summary>
    /// POST <see cref="RunApprovalRequest"/> → <see cref="RunApprovalState"/> (the original issue): for a delivery with
    /// <see cref="TaskDelivery.RequireRunApproval"/>, asks the machine's owner to approve the run (the first call) and
    /// answers where that stands. The runner asks again every few seconds until it is approved or declined.
    /// </summary>
    public const string RunApproval = "/api/runner/run-approval";

    /// <summary>
    /// POST <see cref="Releases.RunnerUpdateProgress"/> (the original issue): where the runner's update stands, and how it ended when it
    /// ends without a restart. A service older than the original issue answers 404, which the runner ignores.
    /// </summary>
    public const string UpdateProgress = "/api/runner/update-progress";

    /// <summary>
    /// PUT <see cref="AgentCapacityReport"/> (the original issue): how much of one agent's usage allowance is left. A service older
    /// than the original issue answers 404, which the runner ignores.
    /// </summary>
    public const string AgentCapacity = "/api/runner/agent-capacity";
}

/// <summary>A run asks whether its machine's owner has approved it (the original issue).</summary>
public sealed record RunApprovalRequest
{
    /// <summary>The delivery waiting to run; it must be held by the calling machine.</summary>
    public required string DeliveryId { get; init; }
}

/// <summary>Where a run's approval stands.</summary>
public enum RunApprovalStatus
{
    /// <summary>Nobody has answered yet.</summary>
    [JsonStringEnumMemberName("pending")] Pending,
    /// <summary>The owner approved it: the agent may start.</summary>
    [JsonStringEnumMemberName("approved")] Approved,
    /// <summary>The owner declined it, or nobody answered in time: it goes to another machine.</summary>
    [JsonStringEnumMemberName("declined")] Declined,
}

/// <summary>The answer to <see cref="ServiceRoutes.RunApproval"/>.</summary>
public sealed record RunApprovalState
{
    /// <summary>The approval request (<c>apr_…</c>) the owner answers in the dashboard.</summary>
    public required string ApprovalId { get; init; }

    /// <summary>Where it stands.</summary>
    public required RunApprovalStatus Status { get; init; }

    /// <summary>Who is asked, by name (for the run's progress line).</summary>
    public string? ApproverName { get; init; }

    /// <summary>What they said, if they answered: a sentence for the thread, such as "Ada declined: not on my laptop".</summary>
    public string? Detail { get; init; }
}

/// <summary>A model's list price, in US dollars per million tokens (from the service's price table).</summary>
public sealed record TokenPrice
{
    /// <summary>Input.</summary>
    public decimal InputUsdPerMillion { get; init; }

    /// <summary>Output.</summary>
    public decimal OutputUsdPerMillion { get; init; }

    /// <summary>Input read from the prompt cache; null: priced as normal input.</summary>
    public decimal? CachedInputUsdPerMillion { get; init; }

    /// <summary>Input written to the five-minute prompt cache; null: priced as normal input.</summary>
    public decimal? CacheWriteUsdPerMillion { get; init; }

    /// <summary>Input written to the one-hour prompt cache; null: priced like a five-minute write.</summary>
    public decimal? CacheWrite1hUsdPerMillion { get; init; }
}

/// <summary>
/// The role's most cost per run (the original issue), with the price table to estimate a run's cost as it goes. The runner stops a
/// run whose estimated cost passes <see cref="MaxUsd"/> and settles it as failed.
/// </summary>
public sealed record RunCostCap
{
    /// <summary>The cap, in US dollars.</summary>
    public required decimal MaxUsd { get; init; }

    /// <summary>List prices by model name (as agent CLIs report it), for agents that report tokens but no cost.</summary>
    public IReadOnlyDictionary<string, TokenPrice> Prices { get; set; } = new Dictionary<string, TokenPrice>();
}

/// <summary>
/// Whose work a machine runs and how much it may spend on other people's (the original issue): its stored settings
/// (<see cref="MachineInfo"/>) with the chosen groups expanded to the people in them, since the runner can't read
/// groups itself. Environment variables on the machine still override each field (containers).
/// </summary>
public sealed record RunnerWorkPolicy
{
    /// <summary>The machine owner's subject id.</summary>
    public required string OwnerSubjectId { get; init; }

    /// <summary>The rule.</summary>
    public required AcceptsWorkFrom AcceptsWorkFrom { get; init; }

    /// <summary>For <see cref="AcceptsWorkFromMode.OwnProjectsAndGroups"/>: the people in the chosen groups (subject ids).</summary>
    public IReadOnlyList<string> GroupMemberSubjectIds { get; set; } = [];

    /// <summary>Most it may spend per day (UTC) on work for other people, in US dollars (estimated); null for no ceiling.</summary>
    public decimal? DailySpendCeilingUsd { get; init; }

    /// <summary>
    /// Which repository credential runs get (the original issue). <see cref="RepoCredentialMode.OwnerSession"/> lets runs of its owner's
    /// own projects use the owner's git sign-in on the machine; everyone else's runs still get a per-run token.
    /// </summary>
    public RepoCredentialMode RepoCredentialMode { get; init; } = RepoCredentialMode.PerRunToken;

    /// <summary>Where and what the runner updates to (the original issue): the workspace's release settings. Null from services older than the original issue.</summary>
    public RunnerReleasePolicy? Releases { get; init; }

    /// <summary>
    /// The workspace's display name now (the original issue), so mbus shows its current name rather than the one it was signed in
    /// under. Null from services older than the original issue.
    /// </summary>
    public string? WorkspaceName { get; init; }
}

/// <summary>A run asks for a credential for its task's repository (the original issue, used by the original issue's run isolation).</summary>
public sealed record RunnerRepoCredentialRequest
{
    /// <summary>The delivery being run; it must be held by the calling machine.</summary>
    public required string DeliveryId { get; init; }

    /// <summary>The task's repository, an https clone URL.</summary>
    public required string Repository { get; init; }

    /// <summary>Whether the run may push; the service also checks the role allows it.</summary>
    public bool CanPush { get; init; }
}

/// <summary>A short-lived credential for one repository.</summary>
public sealed record RunnerRepoCredential
{
    /// <summary>User name git sends (<c>x-access-token</c> for a GitHub App token).</summary>
    public required string UserName { get; init; }

    /// <summary>The token.</summary>
    public required string Secret { get; init; }

    /// <summary>When it stops working.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>A run asks where to send its agent's own telemetry (the original issue).</summary>
public sealed record RunnerTelemetryCredentialRequest
{
    /// <summary>The delivery being run; it must be held by the calling machine.</summary>
    public required string DeliveryId { get; init; }
}

/// <summary>
/// Where a run sends its OpenTelemetry (OTLP/HTTP), and the credential for it: good only for this workspace's telemetry
/// intake, only while the run lasts. The service forwards what arrives to the workspace's collector with the
/// workspace's own headers, so the run never sees them.
/// </summary>
public sealed record RunnerTelemetryCredential
{
    /// <summary>The header the run sends <see cref="Token"/> in (a bearer token works too; this one needs no space, for <c>OTEL_EXPORTER_OTLP_HEADERS</c>).</summary>
    public const string HeaderName = "x-mbus-run-telemetry";

    /// <summary>The intake's base path on the service (<c>/api/w/{ws}/otlp</c>); exporters add <c>/v1/traces</c> and so on.</summary>
    public required string Endpoint { get; init; }

    /// <summary>The credential, sent in <see cref="HeaderName"/>.</summary>
    public required string Token { get; init; }

    /// <summary>When it stops working at the latest (it also stops when the run ends).</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Whether the workspace takes traces.</summary>
    public bool Traces { get; init; }

    /// <summary>Whether the workspace takes metrics.</summary>
    public bool Metrics { get; init; }

    /// <summary>Whether the workspace takes logs.</summary>
    public bool Logs { get; init; }
}

/// <summary>Why a runner declined a delivery.</summary>
public enum DeclineReason
{
    /// <summary>Every slot is busy.</summary>
    [JsonStringEnumMemberName("no-free-slot")] NoFreeSlot,
    /// <summary>The machine's accepts-work-from policy doesn't cover this sender's project.</summary>
    [JsonStringEnumMemberName("not-accepted")] NotAccepted,
    /// <summary>The machine's daily spend ceiling for others' work is reached.</summary>
    [JsonStringEnumMemberName("spend-ceiling")] SpendCeiling,
    /// <summary>The agent named in the delivery isn't available on this machine.</summary>
    [JsonStringEnumMemberName("agent-unavailable")] AgentUnavailable,
    /// <summary>The runner is draining.</summary>
    [JsonStringEnumMemberName("draining")] Draining,
    /// <summary>The instructions' signature didn't verify.</summary>
    [JsonStringEnumMemberName("bad-instructions")] BadInstructions,
    /// <summary>
    /// The machine lacks something the task needs and the task isn't to blame: git doesn't work, or the
    /// agent can't use its sign-in. Not an attempt; the machine says so until it is fixed and scanned again.
    /// </summary>
    [JsonStringEnumMemberName("machine-not-ready")] MachineNotReady,
    /// <summary>
    /// The machine's owner declined the run, or didn't answer in time (the original issue). Not an attempt; the task goes back to the
    /// queue for another machine.
    /// </summary>
    [JsonStringEnumMemberName("not-approved")] NotApproved,
    /// <summary>
    /// A reply for a session inbox whose session is still open (or already being resumed) on this machine (the original issue):
    /// it waits a little and is offered again, so the session is never run twice at once. Not an attempt.
    /// </summary>
    [JsonStringEnumMemberName("session-open")] SessionOpen,
    /// <summary>This runner has no approved execution runtime for the required verification.</summary>
    [JsonStringEnumMemberName("verification-unavailable")] VerificationUnavailable,
}

/// <summary>A download link minted when the delivery was made (never stored in the message).</summary>
public sealed record DownloadLink
{
    /// <summary>The attachment id, or <c>body</c> for a body stored as a blob.</summary>
    public required string Id { get; init; }

    /// <summary>Read-only URL for that one blob.</summary>
    public required string Url { get; init; }

    /// <summary>When the link stops working (the lease's end).</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>A task pushed to a runner (<see cref="RunnerRoutes.Deliver"/>).</summary>
public sealed record TaskDelivery
{
    /// <summary>The service-pinned verification definition, supplied only for required verification.</summary>
    public VerificationSnapshot? Verification { get; init; }

    /// <summary>The delivery (<c>dlv_…</c>); progress, heartbeat and settle name it.</summary>
    public required string DeliveryId { get; init; }

    /// <summary>Which attempt (1 for the first).</summary>
    public required int Attempt { get; init; }

    /// <summary>The lease ends then unless heartbeats extend it.</summary>
    public required DateTimeOffset LeaseExpiresAt { get; init; }

    /// <summary>The agent on this machine that should run it.</summary>
    public required string AgentId { get; init; }

    /// <summary>The message, with its encrypted body.</summary>
    public required MessageEnvelope Envelope { get; init; }

    /// <summary>The role it was sent to, if any.</summary>
    public string? Role { get; init; }

    /// <summary>The role's live instructions (verify the signature before use).</summary>
    public InstructionVersion? Instructions { get; init; }

    /// <summary>How to run it.</summary>
    public required RunEnvironment RunEnvironment { get; init; }

    /// <summary>The role's heartbeat interval, in seconds.</summary>
    public required int HeartbeatSeconds { get; init; }

    /// <summary>Links for the body (when stored as a blob) and each attachment.</summary>
    public IReadOnlyList<DownloadLink> Links { get; set; } = [];

    /// <summary>The project's key (for the working tree and for accepts-work-from checks).</summary>
    public string? ProjectKey { get; init; }

    /// <summary>The subject id of the person who started the project (for accepts-work-from checks).</summary>
    public string? ProjectOwner { get; init; }

    /// <summary>
    /// The role wants its machine's owner to approve each run (the original issue): the runner asks (<see cref="ServiceRoutes.RunApproval"/>)
    /// and waits before it starts the agent; declined, it hands the task back (<see cref="DeclineReason.NotApproved"/>).
    /// </summary>
    public bool RequireRunApproval { get; init; }

    /// <summary>
    /// The model to run the agent with instead of its own default: the one the task names or the role's default for the
    /// agent (the original issue), or the role's cheaper model once its budget is used (the original issue, <see cref="EconomyModel"/>).
    /// </summary>
    public string? Model { get; init; }

    /// <summary><see cref="Model"/> is the role's cheaper model, its daily budget being used (the original issue).</summary>
    public bool EconomyModel { get; init; }

    /// <summary>The role's most cost per run, with prices to estimate it (the original issue); null for no cap.</summary>
    public RunCostCap? CostCap { get; init; }

    /// <summary>
    /// For a message to a session inbox (the original issue): the person whose session opened it. The runner resumes that session
    /// only for its machine's owner, and counts the run as that person's work.
    /// </summary>
    public string? InboxOwner { get; init; }
}

/// <summary>The runner's immediate answer to a delivery.</summary>
public sealed record DeliveryDecision
{
    /// <summary>Whether it will run it.</summary>
    public required bool Accepted { get; init; }

    /// <summary>Why not, when declined. The message returns to the queue without counting an attempt.</summary>
    public DeclineReason? Reason { get; init; }

    /// <summary>More detail for logs.</summary>
    public string? Detail { get; init; }
}

/// <summary>Stop a run (<see cref="RunnerRoutes.Cancel"/>).</summary>
public sealed record CancelDelivery
{
    /// <summary>The delivery.</summary>
    public required string DeliveryId { get; init; }

    /// <summary>Its thread.</summary>
    public required string ThreadId { get; init; }

    /// <summary>Why (shown in the thread).</summary>
    public string? Reason { get; init; }
}

/// <summary>Take no new work (<see cref="RunnerRoutes.Drain"/>).</summary>
public sealed record DrainRequest
{
    /// <summary>Owner of a reversible drain, for example an ACI idle-stop operation. Sent only when the runner reports SupportsManagedDrain.</summary>
    public string? ManagedDrainId { get; init; }

    /// <summary>Resumes only the managed drain with the matching ManagedDrainId. Other drains and running tasks are preserved.</summary>
    public bool Resume { get; init; }

    /// <summary>Why (for example an update).</summary>
    public string? Reason { get; init; }

    /// <summary>Cancel what is still running at this time; null to wait indefinitely.</summary>
    public DateTimeOffset? Deadline { get; init; }
}

/// <summary>Scan for agents again (<see cref="RunnerRoutes.Rescan"/>).</summary>
public sealed record RescanRequest
{
    /// <summary>Who asked (subject id), confirmed on the machine before anything changes.</summary>
    public string? RequestedBy { get; init; }
}

/// <summary>Set up one agent to use mbus (<see cref="RunnerRoutes.Connect"/>, the original issue).</summary>
public sealed record ConnectAgentRequest
{
    /// <summary>Which agent.</summary>
    public required AgentKind Agent { get; init; }

    /// <summary>Who asked, by name, for the question shown on the machine.</summary>
    public string? RequestedBy { get; init; }
}

/// <summary>What the runner did with a <see cref="ConnectAgentRequest"/>.</summary>
public enum ConnectAgentStatus
{
    /// <summary>The question is showing on the machine; the change happens only if the person there says yes.</summary>
    [JsonStringEnumMemberName("asked")] Asked,
    /// <summary>The runner has no terminal to ask in (it runs as a service or in a container): run <c>mbus connect</c> there.</summary>
    [JsonStringEnumMemberName("needs-terminal")] NeedsTerminal,
    /// <summary>The agent isn't installed on the machine.</summary>
    [JsonStringEnumMemberName("not-installed")] NotInstalled,
    /// <summary><c>mbus connect</c> can't set up this agent yet.</summary>
    [JsonStringEnumMemberName("unsupported")] Unsupported,
}

/// <summary>The runner's answer to a <see cref="ConnectAgentRequest"/>.</summary>
public sealed record ConnectAgentResult
{
    /// <summary>What happened.</summary>
    public required ConnectAgentStatus Status { get; init; }

    /// <summary>One sentence for the person who asked.</summary>
    public string? Message { get; init; }
}

/// <summary>How many tasks to run at once (<see cref="RunnerRoutes.Slots"/>, the original issue).</summary>
public sealed record SlotsRequest
{
    /// <summary>The dashboard's setting; the runner uses at most its own ceiling. Null: its own default (its owner's cap, else 1, the original issue).</summary>
    public int? Slots { get; init; }
}

/// <summary>
/// Update <c>mbus</c> (<see cref="RunnerRoutes.Update"/>; signed releases are the original issue). <see cref="Version"/> is
/// <c>latest</c> (whatever the channel has, never older than the running one) or the exact version the workspace pins (the original issue).
/// </summary>
public sealed record UpdateRequest
{
    /// <summary>Exact human pin; permits downgrades. False means an unpinned upgrade.</summary>
    public bool Pinned { get; init; }

    /// <summary>Install source: standalone (also when absent) or dotnet-tool. Never a package URL.</summary>
    public RunnerInstallKind? Via { get; init; }

    /// <summary>The version to install.</summary>
    public required string Version { get; init; }

    /// <summary>Release channel, for example <c>stable</c>.</summary>
    public string? Channel { get; init; }

    /// <summary>A mirror to download from instead of the server (the original issue): an https address laid out like the server's feed.</summary>
    public string? Mirror { get; init; }
}

/// <summary>What the runner is doing (<see cref="RunnerRoutes.Status"/>).</summary>
public sealed record RunnerStatus
{
    /// <summary>Latest local resource sample, when available.</summary>
    public RunnerResourceSample? Resources { get; init; }

    /// <summary>Whether this runner supports reversible drains identified by ManagedDrainId. Absent means false.</summary>
    public bool SupportsManagedDrain { get; init; }

    /// <summary>The current managed drain's owner, or null. A resume request must name this value.</summary>
    public string? ManagedDrainId { get; init; }

    /// <summary><c>mbus</c> version.</summary>
    public required string Version { get; init; }

    /// <summary>Total slots.</summary>
    public required int Slots { get; init; }

    /// <summary>Deliveries running now.</summary>
    public IReadOnlyList<string> ActiveDeliveryIds { get; set; } = [];

    /// <summary>Whether it is draining.</summary>
    public bool Draining { get; init; }

    /// <summary>
    /// The most slots the runner allows (the original issue): <see cref="SlotMachineMax"/> (the original issue; a runner older than that may report a
    /// lower cap its owner set on the machine); null from older runners.
    /// </summary>
    public int? SlotCeiling { get; init; }

    /// <summary>The most slots it allows on this machine's size (<see cref="Machines.SlotAdvice.MostForMachine"/>, the original issue); null from older runners.</summary>
    public int? SlotMachineMax { get; init; }
}

/// <summary>Keep leases alive (<see cref="ServiceRoutes.Heartbeat"/>).</summary>
public sealed record RunnerHeartbeat
{
    /// <summary>Latest resource sample, sent only after the service advertises support for this session.</summary>
    public RunnerResourceSample? Resources { get; init; }

    /// <summary>Deliveries running now.</summary>
    public IReadOnlyList<string> ActiveDeliveryIds { get; set; } = [];

    /// <summary>Total slots.</summary>
    public required int Slots { get; init; }

    /// <summary>Slots in use.</summary>
    public required int SlotsBusy { get; init; }

    /// <summary>Whether it is draining.</summary>
    public bool Draining { get; init; }

    /// <summary>
    /// The runner's connection session (<c>X-Mbus-Session</c>), so the answer can say whether the service
    /// holds that connection (<see cref="HeartbeatAck.RunnerConnected"/>).
    /// </summary>
    public string? SessionId { get; init; }
}

/// <summary>Workspace suspension reported to its connected machines.</summary>
public sealed record WorkspaceSuspensionStatus
{
    /// <summary>Message for workspace members.</summary>
    public required string Message { get; init; }
    /// <summary>Support reference for this suspension.</summary>
    public required string Reference { get; init; }
    /// <summary>Support contact address.</summary>
    public required string SupportEmail { get; init; }
    /// <summary>Whether running tasks must stop.</summary>
    public bool StopRunningTasks { get; init; }
}

/// <summary>The service’s answer to a runner heartbeat.</summary>
public sealed record HeartbeatAck
{
    /// <summary>Whether the service accepts resource samples for this runner session. Absent means false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SupportsResourceSamples { get; init; }

    /// <summary>Current workspace suspension; null when work can resume.</summary>
    public WorkspaceSuspensionStatus? Suspension { get; init; }

    /// <summary>Lease state per delivery.</summary>
    public IReadOnlyList<LeaseState> Leases { get; set; } = [];

    /// <summary>
    /// Whether the service has this runner's connection (<see cref="RunnerHeartbeat.SessionId"/>) open. False
    /// means the runner's connection is dead even if its socket still looks open (a proxy or relay in between
    /// kept it up after the service restarted): the runner reconnects. Null from services that don't say.
    /// </summary>
    public bool? RunnerConnected { get; init; }
}

/// <summary>One lease after a heartbeat.</summary>
public sealed record LeaseState
{
    /// <summary>The delivery.</summary>
    public required string DeliveryId { get; init; }

    /// <summary>The new lease end; null when the lease is gone (stop the run).</summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>A cancel was requested (stop the run and settle it as cancelled).</summary>
    public bool CancelRequested { get; init; }
}

/// <summary>Progress on a delivery (<see cref="ServiceRoutes.Progress"/>).</summary>
public sealed record RunnerProgress
{
    /// <summary>The delivery.</summary>
    public required string DeliveryId { get; init; }

    /// <summary>The report.</summary>
    public required ProgressUpdate Progress { get; init; }
}

/// <summary>How a delivery ended (<see cref="ServiceRoutes.Settle"/>).</summary>
public sealed record RunnerSettle
{
    /// <summary>The authenticated runner's verification outcome for this delivery attempt, when reported.</summary>
    public VerificationResult? Verification { get; init; }

    /// <summary>The leased attempt whose required checks were run.</summary>
    public int? VerificationAttempt { get; init; }

    /// <summary>The pinned definition digest from the verified delivery.</summary>
    public string? VerificationDigest { get; init; }

    /// <summary>The trusted <see cref="TaskDelivery.Attempt"/>, sent only when the target advertises <c>settleAttemptV1</c>. A stale attempt must never settle a newer lease.</summary>
    public int? Attempt { get; init; }

    /// <summary>The role whose key this machine refused while opening the task (the original issue).</summary>
    public string? UnreadableRole { get; init; }

    /// <summary>The delivery.</summary>
    public required string DeliveryId { get; init; }

    /// <summary>The result, from the process exit code and the agent's own <c>settle</c> call.</summary>
    public required SettleResult Result { get; init; }

    /// <summary>
    /// Set when the run failed because of the machine, not the task (<see cref="DeclineReason.MachineNotReady"/>:
    /// git is only macOS's placeholder, the agent isn't signed in). The result is then
    /// <see cref="Messaging.SettleStatus.Abandoned"/>; the service should put the task back without counting the
    /// attempt, as for a declined offer. Null for every other run.
    /// </summary>
    public DeclineReason? Declined { get; init; }
}
