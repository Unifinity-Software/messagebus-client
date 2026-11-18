namespace MessageBus.Contracts.Runner;

// How much of a signed-in agent account's usage allowance is left (the original issue): a Claude subscription's 5-hour and
// weekly limits, Codex's rate-limit windows. Only what the agent's CLI reports; never scraped or guessed, never a
// plan name, account id or email. Values are strings, not enums, so a newer runner's window names don't break an
// older service: unknown names are ignored.

/// <summary>Well-known <see cref="CapacityWindow.Window"/> names and <see cref="CapacityWindow.Status"/> values.</summary>
public static class CapacityNames
{
    /// <summary>A rolling 5-hour window (Claude five_hour; Codex window_minutes 300).</summary>
    public const string FiveHour = "five-hour";
    /// <summary>A rolling 7-day window (Claude seven_day; Codex window_minutes 10080).</summary>
    public const string Weekly = "weekly";
    /// <summary>Claude's weekly limit for Opus models only (seven_day_opus).</summary>
    public const string WeeklyOpus = "weekly-opus";
    /// <summary>Claude's weekly limit for Sonnet models only (seven_day_sonnet).</summary>
    public const string WeeklySonnet = "weekly-sonnet";
    /// <summary>Codex's primary window when its length is not one of the above.</summary>
    public const string Primary = "primary";
    /// <summary>Codex's secondary window when its length is not one of the above.</summary>
    public const string Secondary = "secondary";

    /// <summary>Room left (Claude allowed).</summary>
    public const string Ok = "ok";
    /// <summary>Close to the limit (Claude allowed_warning).</summary>
    public const string Warning = "warning";
    /// <summary>At the limit; work waits or fails until it resets (Claude rejected; Codex rate_limit_reached_type set).</summary>
    public const string Limited = "limited";

    /// <summary>Most windows one report or one agent carries.</summary>
    public const int MaxWindows = 6;
}

/// <summary>One usage window as the agent's CLI last reported it.</summary>
public sealed record CapacityWindow
{
    /// <summary>One of the <see cref="CapacityNames"/> window names.</summary>
    public required string Window { get; init; }

    /// <summary>The window's length in minutes, when the CLI says (Codex window_minutes).</summary>
    public int? WindowMinutes { get; init; }

    /// <summary>How much of the window is used, 0-100; null when the CLI gave only a status.</summary>
    public double? UsedPercent { get; init; }

    /// <summary><see cref="CapacityNames.Ok"/>, <see cref="CapacityNames.Warning"/> or <see cref="CapacityNames.Limited"/>; null when not reported.</summary>
    public string? Status { get; init; }

    /// <summary>When the window resets, as the CLI reported it.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>
    /// When the CLI reported this window: the source event's own time (Codex session log timestamp), or when the
    /// runner read the line. Never re-stamped when an older reading is sent again.
    /// </summary>
    public required DateTimeOffset MeasuredAt { get; init; }
}

/// <summary>The last time a run hit its account's limit.</summary>
public sealed record CapacityLimitHit
{
    /// <summary>The window that hit its limit, when known.</summary>
    public string? Window { get; init; }

    /// <summary>When the run hit it.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>When the CLI said it resets; null when it didn't say. Never read out of error text.</summary>
    public DateTimeOffset? ResetsAt { get; init; }
}

/// <summary>
/// PUT <see cref="ServiceRoutes.AgentCapacity"/>: one agent kind's windows on the calling machine, sent after each
/// run that reported any, and straight away when a run hits a limit. The runner keeps the latest reading per window
/// in a local file and always sends every window it holds, each with its own <see cref="CapacityWindow.MeasuredAt"/>.
/// </summary>
public sealed record AgentCapacityReport
{
    /// <summary>The agent kind on the calling machine, as in the inventory (claude-code, codex, ...).</summary>
    public required string AgentKind { get; init; }

    /// <summary>At most <see cref="CapacityNames.MaxWindows"/> windows.</summary>
    public IReadOnlyList<CapacityWindow> Windows { get; set; } = [];

    /// <summary>The last limit hit, when there was one.</summary>
    public CapacityLimitHit? LimitHit { get; init; }
}

/// <summary>An agent's capacity as the service holds it, on <see cref="Machines.AgentInfo"/>; missing when unknown.</summary>
public sealed record AgentCapacity
{
    /// <summary>Each window's latest reading, merged by window name (a newer reading of a window replaces an older one).</summary>
    public IReadOnlyList<CapacityWindow> Windows { get; set; } = [];

    /// <summary>The last limit hit, when there was one.</summary>
    public CapacityLimitHit? LimitHit { get; init; }

    /// <summary>When the service last accepted a report for this agent.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
