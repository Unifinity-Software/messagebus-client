using System.Text.Json.Serialization;

namespace MessageBus.Contracts.Mcp;

/// <summary>A project tasks leave in a move, and how many.</summary>
public sealed record MovedFrom
{
    /// <summary>The source project id, or null for no project.</summary>
    public string? ProjectId { get; init; }
    /// <summary>The source project title.</summary>
    public string? Title { get; init; }
    /// <summary>How many tasks leave this project.</summary>
    public int Tasks { get; init; }
}

/// <summary>What a move did (or, for a dry run, would do).</summary>
public sealed record MoveThreadsResult
{
    /// <summary>Every thread that moves: the ones asked for, then their sub-tasks.</summary>
    public IReadOnlyList<string> ThreadIds { get; set; } = [];

    /// <summary>How many of them are tasks (a join isn't).</summary>
    public int Tasks { get; init; }

    /// <summary>How many of those are still queued or running.</summary>
    public int OpenTasks { get; init; }

    /// <summary>The projects the tasks leave.</summary>
    public IReadOnlyList<MovedFrom> From { get; set; } = [];
    /// <summary>The destination project, or null for no project.</summary>
    public string? ToProjectId { get; init; }
    /// <summary>The destination project title.</summary>
    public string? ToTitle { get; init; }

    /// <summary>The estimated cost the tasks take with them.</summary>
    public decimal EstimatedCostUsd { get; init; }

    /// <summary>The new project's budget cap, if it has one.</summary>
    public decimal? BudgetCapUsd { get; init; }

    /// <summary>The new project's estimated cost once they are in it.</summary>
    public decimal? CostAfterUsd { get; init; }

    /// <summary>The new project's cost reaches its budget cap with them (allowed for finished tasks: it's bookkeeping).</summary>
    public bool OverBudgetCap { get; init; }

    /// <summary>Preview the move without changing anything.</summary>
    public bool DryRun { get; init; }
}

/// <summary>Move a task sent under the wrong project.</summary>
public sealed record MoveInput
{
    /// <summary>The thread to move.</summary>
    public required string ThreadId { get; init; }
    /// <summary>The destination project id, or null for no project.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required string? ProjectId { get; init; }
    /// <summary>Move its sub-tasks too; defaults to true.</summary>
    public bool IncludeSubtasks { get; set; } = true;
    /// <summary>Why it is moving, recorded in its timeline and the audit log.</summary>
    public string? Reason { get; init; }
    /// <summary>Preview the move without changing anything.</summary>
    public bool DryRun { get; init; }
}
