using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Machines;

namespace MessageBus.Contracts.Projects;

/// <summary>The immutable choice of machines allowed to execute a project's work.</summary>
public enum ProjectExecutionScope
{
    /// <summary>Any otherwise eligible workspace runner (also the legacy default).</summary>
    [JsonStringEnumMemberName("workspace")] Workspace,
    /// <summary>Only approved personal machines registered to the project's original creator.</summary>
    [JsonStringEnumMemberName("owner-runners")] OwnerRunners,
}

/// <summary>Where a project stands.</summary>
public enum ProjectStatus
{
    /// <summary>Work is going on.</summary>
    [JsonStringEnumMemberName("active")] Active,
    /// <summary>The orchestrator called <c>complete_project</c>.</summary>
    [JsonStringEnumMemberName("completed")] Completed,
    /// <summary>The orchestrator completed it as failed.</summary>
    [JsonStringEnumMemberName("failed")] Failed,
    /// <summary>Cancelled; its open tasks were cancelled with it.</summary>
    [JsonStringEnumMemberName("cancelled")] Cancelled,
}

/// <summary>Who can see a project.</summary>
public enum ProjectVisibilityMode
{
    /// <summary>The person who started it and the members of their groups (the default).</summary>
    [JsonStringEnumMemberName("creator-groups")] CreatorGroups,
    /// <summary>Everyone in the workspace.</summary>
    [JsonStringEnumMemberName("workspace")] Workspace,
    /// <summary>Only the listed people and groups.</summary>
    [JsonStringEnumMemberName("listed")] Listed,
}

/// <summary>Who can see a project.</summary>
public sealed record ProjectVisibility
{
    /// <summary>The rule.</summary>
    public ProjectVisibilityMode Mode { get; set; } = ProjectVisibilityMode.CreatorGroups;

    /// <summary>People (subject ids), for <see cref="ProjectVisibilityMode.Listed"/>.</summary>
    public IReadOnlyList<string> SubjectIds { get; set; } = [];

    /// <summary>Entra group ids, for <see cref="ProjectVisibilityMode.Listed"/>.</summary>
    public IReadOnlyList<string> GroupIds { get; set; } = [];
}

/// <summary>
/// The work item a project came from, written <c>{system}:{path}[#{item}]</c>:
/// <c>ado:org/project#123</c>, <c>gh:owner/repo#42</c>, <c>gh:owner/repo</c>, or
/// <c>file:samples/work-items/add-due-dates.md</c>. Its canonical text is the project key, so
/// <c>ensure_project</c> with the same source returns the same project.
/// </summary>
public sealed record SourceRef
{
    /// <summary>Azure DevOps work item: <c>ado:org/project#id</c>.</summary>
    public const string AzureDevOps = "ado";
    /// <summary>GitHub issue or repository: <c>gh:owner/repo#n</c>.</summary>
    public const string GitHub = "gh";
    /// <summary>A file in the caller's repository: <c>file:path</c>.</summary>
    public const string File = "file";

    /// <summary>The system, lowercase: <see cref="AzureDevOps"/>, <see cref="GitHub"/>, <see cref="File"/> or another scheme.</summary>
    public required string System { get; init; }

    /// <summary>What comes after <c>{system}:</c> and before <c>#</c>.</summary>
    public required string Path { get; init; }

    /// <summary>What comes after the last <c>#</c>, if anything (work item, issue or PR number).</summary>
    public string? Item { get; init; }

    /// <summary>The canonical text, used as the project key.</summary>
    public override string ToString() => Item is null ? $"{System}:{Path}" : $"{System}:{Path}#{Item}";

    /// <summary>
    /// Parses the text form. The system is lowercased and must be letters, digits, <c>.</c> or
    /// <c>-</c> starting with a letter; the path must be non-empty; no whitespace or control
    /// characters anywhere. For <c>ado</c> and <c>gh</c> the path must have at least two
    /// <c>/</c>-separated parts and the item, when present, must be a number.
    /// </summary>
    /// <param name="text">For example <c>ado:contoso/web#123</c>.</param>
    /// <param name="source">The parsed reference, or null.</param>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SourceRef? source)
    {
        source = null;
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        }

        var colon = text.IndexOf(':');
        if (colon <= 0) return false;
        var system = text[..colon].ToLowerInvariant();
        if (!char.IsAsciiLetter(system[0])) return false;
        foreach (var c in system)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) return false;
        }

        var rest = text[(colon + 1)..];
        string? item = null;
        var hash = rest.LastIndexOf('#');
        if (hash >= 0)
        {
            item = rest[(hash + 1)..];
            rest = rest[..hash];
            if (item.Length == 0) return false;
        }
        if (rest.Length == 0) return false;

        if (system is AzureDevOps or GitHub)
        {
            var parts = rest.Split('/');
            if (parts.Length < 2 || parts.Any(p => p.Length == 0)) return false;
            if (item is not null && !item.All(char.IsAsciiDigit)) return false;
        }

        source = new SourceRef { System = system, Path = rest, Item = item };
        return true;
    }
}

/// <summary>Running totals for a project, updated as its tasks settle. Usage numbers are estimates.</summary>
public sealed record ProjectTotals
{
    /// <summary>Number of friction notes by kind, across every recorded run.</summary>
    public IReadOnlyDictionary<string, long> Friction { get; set; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, long>.Empty;
    /// <summary>The same counts grouped by task role; '-' means a task with no role.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> FrictionByRole { get; set; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyDictionary<string, long>>.Empty;

    /// <summary>Tasks (threads) in the project.</summary>
    public int Tasks { get; init; }
    /// <summary>Tasks settled successfully.</summary>
    public int TasksDone { get; init; }
    /// <summary>Tasks queued, delivered or working.</summary>
    public int TasksWorking { get; init; }
    /// <summary>Tasks cancelled, separate from failures.</summary>
    public int TasksCancelled { get; init; }
    /// <summary>Tasks failed or dead-lettered.</summary>
    public int TasksFailed { get; init; }
    /// <summary>Failed tasks still needing attention.</summary>
    public int TasksFailedUnhandled { get; init; }
    /// <summary>Failed tasks redone by a later successful task.</summary>
    public int TasksRedone { get; init; }
    /// <summary>Open tasks waiting for a ready machine.</summary>
    public int TasksWaitingOnMachine { get; init; }
    /// <summary>Seconds from the project's start to now (or its end).</summary>
    public double WallSeconds { get; init; }
    /// <summary>Sum of agents' active seconds.</summary>
    public double AgentSeconds { get; init; }
    /// <summary>Sum of input tokens.</summary>
    public long TokensIn { get; init; }
    /// <summary>Sum of output tokens.</summary>
    public long TokensOut { get; init; }
    /// <summary>Sum of estimated cost in US dollars, over the runs whose cost is known.</summary>
    public decimal EstimatedCostUsd { get; init; }
    /// <summary>Tasks with a run whose cost is unknown (a model with no price), so <see cref="EstimatedCostUsd"/> is a lower bound.</summary>
    public int TasksCostUnknown { get; init; }
    /// <summary>Pull requests referenced by settle results.</summary>
    public int PullRequests { get; init; }
    /// <summary>Distinct agents that worked on it.</summary>
    public IReadOnlyList<string> Agents { get; set; } = [];
    /// <summary>Distinct machines that worked on it.</summary>
    public IReadOnlyList<string> Machines { get; set; } = [];
    /// <summary>Distinct model vendors.</summary>
    public IReadOnlyList<string> Vendors { get; set; } = [];
    /// <summary>Distinct models.</summary>
    public IReadOnlyList<string> Models { get; set; } = [];
}

/// <summary>One task’s waiting and running times.</summary>
public sealed record ProjectActivity
{
    /// <summary>The task thread.</summary>
    public required string ThreadId { get; init; }
    /// <summary>The current task status.</summary>
    public required ThreadStatus Status { get; init; }
    /// <summary>When the task was sent.</summary>
    public required DateTimeOffset SentAt { get; init; }
    /// <summary>When an agent first started it.</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>When the task finished.</summary>
    public DateTimeOffset? EndedAt { get; init; }
}

/// <summary>A project grouping the threads for one piece of work.</summary>
public sealed record ProjectInfo
{
    /// <summary>The execution policy chosen at creation; independent of project visibility and roles.</summary>
    public ProjectExecutionScope ExecutionScope { get; init; }

    /// <summary>Pinned mbus registration subject, never the OS login or a delegate's identity.</summary>
    public string? ExecutionOwnerSubjectId { get; init; }

    /// <summary>The coordinating session and its last check-in.</summary>
    public ProjectCoordinator? Coordinator { get; init; }
    /// <summary>State computed at read time.</summary>
    public ProjectState State { get; init; }
    /// <summary>Actions waiting for a person, in priority order.</summary>
    public IReadOnlyList<ProjectNeed> NeedsYou { get; set; } = [];
    /// <summary>Unanswered approval requests for this project.</summary>
    public int PendingApprovals { get; init; }

    /// <summary>Project id (<c>prj_…</c>).</summary>
    public required string ProjectId { get; init; }

    /// <summary>The workspace.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>The project key (canonical <see cref="SourceRef"/> text), unique in the workspace.</summary>
    public required string Key { get; init; }

    /// <summary>Title.</summary>
    public required string Title { get; init; }

    /// <summary>The parsed key, when it parses.</summary>
    public SourceRef? Source { get; init; }

    /// <summary>Other references (the work item, the repository).</summary>
    public IReadOnlyList<Ref> Refs { get; set; } = [];

    /// <summary>Who started it (the person behind the orchestrating agent).</summary>
    public required SenderIdentity StartedBy { get; init; }

    /// <summary>Where it stands.</summary>
    public required ProjectStatus Status { get; init; }

    /// <summary>Who can see it.</summary>
    public ProjectVisibility Visibility { get; set; } = new();

    /// <summary>Most it may spend (estimated, US dollars); alerts fire at 80% and 100%. Null for no cap.</summary>
    public decimal? BudgetCapUsd { get; init; }

    /// <summary>How the project enforces its budget cap.</summary>
    public string BudgetMode { get; set; } = "reserve";

    /// <summary>Most tasks it may have open at once; null for the workspace default.</summary>
    public int? MaxFanOut { get; init; }

    /// <summary>The orchestrator's summary from <c>complete_project</c>.</summary>
    public string? Summary { get; init; }

    /// <summary>When it was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it was completed, failed or cancelled.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// When a finished project was archived: put away, out of the default list and search. Its status,
    /// tasks, totals and audit entries stay as they were. Null while it isn't archived.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>When something last happened in it: a task was sent or moved, or the project finished.</summary>
    public DateTimeOffset LastActivityAt { get; init; }

    /// <summary>Running totals.</summary>
    public ProjectTotals Totals { get; set; } = new();

    /// <summary>The newest 200 tasks, in sent order.</summary>
    public IReadOnlyList<ProjectActivity> Activity { get; set; } = [];
}

/// <summary>The session coordinating a project.</summary>
public sealed record ProjectCoordinator
{
    /// <summary>The coordinating agent kind, when known.</summary>
    public AgentKind? AgentKind { get; init; }
    /// <summary>The coordinating agent, when known.</summary>
    public string? AgentId { get; init; }
    /// <summary>The coordinating machine, when known.</summary>
    public string? MachineId { get; init; }
    /// <summary>When the session last checked in.</summary>
    public required DateTimeOffset CheckedInAt { get; init; }
}

/// <summary>The state of a project at read time.</summary>
public enum ProjectState
{
    /// <summary>Tasks are open or the coordinator checked in recently.</summary>
    [JsonStringEnumMemberName("working")] Working,
    /// <summary>No tasks are open and the coordinator has stopped checking in.</summary>
    [JsonStringEnumMemberName("stalled")] Stalled,
    /// <summary>The project has finished.</summary>
    [JsonStringEnumMemberName("done")] Done,
}

/// <summary>Something a person must act on.</summary>
public enum ProjectNeed
{
    /// <summary>An approval waits for a person.</summary>
    [JsonStringEnumMemberName("approval")] Approval,
    /// <summary>A task waits for a ready machine.</summary>
    [JsonStringEnumMemberName("machine-not-ready")] MachineNotReady,
    /// <summary>Estimated spending reached eighty percent of the cap.</summary>
    [JsonStringEnumMemberName("budget")] Budget,
    /// <summary>No tasks are open and the coordinator has stopped checking in.</summary>
    [JsonStringEnumMemberName("stalled")] Stalled,
}
