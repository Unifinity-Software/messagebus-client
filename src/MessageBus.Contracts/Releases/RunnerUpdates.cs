using System.Text.Json.Serialization;

namespace MessageBus.Contracts.Releases;

/// <summary>
/// A workspace's release settings as its runners need them (the original issue), in <see cref="Runner.RunnerWorkPolicy.Releases"/>:
/// the channel to install from, a version to stay on, and a mirror to download from.
/// </summary>
public sealed record RunnerReleasePolicy
{
    /// <summary>The release channel (<see cref="ReleaseFeed.Channels"/>).</summary>
    public required string Channel { get; init; }

    /// <summary>The version to install and stay on; null for the channel's latest.</summary>
    public string? PinnedVersion { get; init; }

    /// <summary>An https feed to download from instead of the server; null for the server's own.</summary>
    public string? Mirror { get; init; }
}

/// <summary>How mbus is installed on a machine; an omitted kind means standalone.</summary>
public enum RunnerInstallKind
{
    /// <summary>A standalone executable.</summary>
    [JsonStringEnumMemberName("standalone")] Standalone,
    /// <summary>A .NET global tool.</summary>
    [JsonStringEnumMemberName("dotnet-tool")] DotnetTool,
}

/// <summary>How a runner's last update ended (the original issue), as the runner itself recorded it.</summary>
public enum RunnerUpdateOutcome
{
    /// <summary>The new version is in place and the runner started on it.</summary>
    [JsonStringEnumMemberName("updated")] Updated,
    /// <summary>The new version didn't start on the machine (or the runner came back on the old one), so the old one stays.</summary>
    [JsonStringEnumMemberName("rolled-back")] RolledBack,
    /// <summary>Nothing was replaced: the release was refused (signature, download, platform) or couldn't be reached.</summary>
    [JsonStringEnumMemberName("not-installed")] NotInstalled,
}

/// <summary>
/// The result of a runner's last update (the original issue): the runner writes it next to its settings when an update ends and sends
/// it with its inventory (<see cref="Machines.MachineInventory.LastUpdate"/>) each time it starts, so the Runners page
/// shows it once the runner is back.
/// </summary>
public sealed record RunnerUpdateReport
{
    /// <summary>How it ended.</summary>
    public required RunnerUpdateOutcome Outcome { get; init; }

    /// <summary>The version it ran before.</summary>
    public required string FromVersion { get; init; }

    /// <summary>The version it was asked for or installed; null when it never learned which (the release list couldn't be read).</summary>
    public string? ToVersion { get; init; }

    /// <summary>Why it didn't update, in the runner's words; null when it did.</summary>
    public string? Reason { get; init; }

    /// <summary>When the update ended, by the runner's clock.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>When the service first received this report (set by the service, which compares it with rollouts, the original issue).</summary>
    public DateTimeOffset? ReceivedAt { get; init; }
}

/// <summary>
/// Whether a runner's mbus can update itself (the original issue), as the runner says with its inventory
/// (<see cref="Machines.MachineInventory.Updates"/>). When it cannot update, the Runners page shows why.
/// </summary>
public sealed record RunnerUpdateAbility
{
    /// <summary>Install source: standalone (also when absent) or dotnet-tool.</summary>
    public RunnerInstallKind? Via { get; init; }

    /// <summary>Whether an update request installs a release on it.</summary>
    public required bool CanUpdate { get; init; }

    /// <summary>Why it can't, one sentence for the Runners page; null when it can.</summary>
    public string? Reason { get; init; }
}

/// <summary>Where a runner's update stands between being asked and ending (the original issue).</summary>
public enum RunnerUpdateStage
{
    /// <summary>The service sent the request to the runner.</summary>
    [JsonStringEnumMemberName("requested")] Requested,
    /// <summary>The runner takes no new work and waits for its running tasks to finish.</summary>
    [JsonStringEnumMemberName("waiting")] Waiting,
    /// <summary>The runner is downloading, checking and installing the release.</summary>
    [JsonStringEnumMemberName("installing")] Installing,
    /// <summary>The release is in place and the runner is starting again on it.</summary>
    [JsonStringEnumMemberName("restarting")] Restarting,
    /// <summary>The update ended without a restart (not installed, or nothing to install); only sent by the runner, never stored.</summary>
    [JsonStringEnumMemberName("ended")] Ended,
    /// <summary>A dotnet-tool runner keeps taking work while other sessions hold the install (the original issue); added last so existing values keep their numbers.</summary>
    [JsonStringEnumMemberName("waiting-for-sessions")] WaitingForSessions,
}

/// <summary>
/// What a runner tells the service while it updates (the original issue, <see cref="Runner.ServiceRoutes.UpdateProgress"/>): each
/// stage as it reaches it, and how the update ended when it ends without a restart.
/// </summary>
public sealed record RunnerUpdateProgress
{
    /// <summary>Other sessions holding the install while waiting.</summary>
    public RunnerUpdateBlockedBy? BlockedBy { get; init; }

    /// <summary>The stage it reached.</summary>
    public required RunnerUpdateStage Stage { get; init; }

    /// <summary>The version it is installing, when it knows.</summary>
    public string? Version { get; init; }

    /// <summary>With <see cref="RunnerUpdateStage.Ended"/>: how it ended. The runner keeps running on the version it had.</summary>
    public RunnerUpdateReport? Report { get; init; }
}

/// <summary>
/// An update under way on a machine (the original issue), on <see cref="Machines.MachineInfo.Update"/> from the moment its runner is
/// asked until the update ends; the Runners page shows it instead of a passing message.
/// </summary>
public sealed record MachineUpdateStatus
{
    /// <summary>Other sessions holding the install while waiting.</summary>
    public RunnerUpdateBlockedBy? BlockedBy { get; init; }

    /// <summary>The runs this update is waiting for, projected for the viewer; omitted in other stages.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RunnerUpdateWaitingTask>? WaitingFor { get; init; }

    /// <summary>Where it stands (never <see cref="RunnerUpdateStage.Ended"/>).</summary>
    public required RunnerUpdateStage Stage { get; init; }

    /// <summary>The version asked for; null for whatever the channel has.</summary>
    public string? Version { get; init; }

    /// <summary>When the runner was asked.</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>When it reached this stage.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Asked by the workspace's automatic updates (the original issue), not by a person.</summary>
    public bool Automatic { get; init; }

    /// <summary>
    /// When its runner disconnected during the update; null while it is connected. Runners restart to finish an update,
    /// so the page gives it a grace period to come back before saying it went offline (the original issue).
    /// </summary>
    public DateTimeOffset? OfflineAt { get; init; }
}

/// <summary>A run holding up a runner update. Hidden threads retain their place without disclosing their title or id.</summary>
public sealed record RunnerUpdateWaitingTask
{
    /// <summary>The thread id, or an empty string when the viewer cannot see it.</summary>
    public required string ThreadId { get; init; }
    /// <summary>The dashboard title or summary, or a placeholder for a hidden thread.</summary>
    public required string Title { get; init; }
    /// <summary>The agent's wire kind, such as claude-code or codex.</summary>
    public required string AgentKind { get; init; }
    /// <summary>The agent's name in the registry.</summary>
    public required string AgentDisplayName { get; init; }
    /// <summary>When the current attempt acquired its lease.</summary>
    public required DateTimeOffset StartedAt { get; init; }
}

/// <summary>Sessions holding a runner installation.</summary>
public sealed record RunnerUpdateBlockedBy
{
    /// <summary>Number of holding sessions.</summary>
    public required int Count { get; init; }
}
