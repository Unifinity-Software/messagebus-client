using System.Text.Json;

namespace MessageBus.Contracts.Mcp;

/// <summary>List workflows in the current workspace.</summary>
public sealed record ListWorkflowsInput;

/// <summary>The workflows available in the current workspace.</summary>
public sealed record ListWorkflowsResult
{
    /// <summary>The workspace's workflows.</summary>
    public required IReadOnlyList<WorkflowSummary> Workflows { get; init; }
}

/// <summary>Workflow information used when choosing what to run.</summary>
public sealed record WorkflowSummary
{
    /// <summary>Workflow id.</summary>
    public required string Id { get; init; }

    /// <summary>Workflow name.</summary>
    public required string Name { get; init; }

    /// <summary>One-line description.</summary>
    public required string Description { get; init; }

    /// <summary>Draft, published or archived.</summary>
    public required string State { get; init; }

    /// <summary>Current published version, when available.</summary>
    public int? PublishedVersion { get; init; }

    /// <summary>Workflow settings, including who can start it and where it runs.</summary>
    public required JsonElement Settings { get; init; }
}

/// <summary>Start a job with a published workflow.</summary>
public sealed record StartJobInput
{
    /// <summary>Tracker work item key, such as <c>gh:owner/repo#42</c>.</summary>
    public required string WorkItem { get; init; }

    /// <summary>Workflow id or name, matched case-insensitively.</summary>
    public required string Workflow { get; init; }

    /// <summary>Optional context for the job.</summary>
    public string? Instructions { get; init; }

    /// <summary>Optional spending limit in USD.</summary>
    public decimal? LimitUsd { get; init; }

    /// <summary>Optional runner scope, workspace or owner-runners.</summary>
    public string? RunsOn { get; init; }
}

/// <summary>The HTTP request to start a job with a resolved published workflow.</summary>
public sealed record StartJobRequest
{
    /// <summary>Tracker work item key, such as <c>gh:owner/repo#42</c>.</summary>
    public required string WorkItem { get; init; }

    /// <summary>The resolved workflow id.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>Optional context for the job.</summary>
    public string? Instructions { get; init; }

    /// <summary>Optional spending limit in USD.</summary>
    public decimal? LimitUsd { get; init; }

    /// <summary>Optional runner scope, workspace or owner-runners.</summary>
    public string? RunsOn { get; init; }

    /// <summary>Optional issue title read locally; trimmed and limited to 200 characters.</summary>
    public string? WorkItemTitle { get; init; }

    /// <summary>Optional issue body read locally; limited to 30,000 characters.</summary>
    public string? WorkItemBody { get; init; }
}

/// <summary>The job started by the service.</summary>
public sealed record StartJobResult
{
    /// <summary>Job id.</summary>
    public required string JobId { get; init; }

    /// <summary>Dashboard link for the job.</summary>
    public required string Url { get; init; }

    /// <summary>Initial job state.</summary>
    public required string State { get; init; }

    /// <summary>Workflow id, name and version selected by the service.</summary>
    public JsonElement? Workflow { get; init; }
}

/// <summary>Read a job's progress by id.</summary>
public sealed record JobStatusInput
{
    /// <summary>Job id returned when the job started.</summary>
    public required string JobId { get; init; }
}

/// <summary>The job's current state, spending and step runs.</summary>
public sealed record JobStatusResult
{
    /// <summary>Job id.</summary>
    public required string JobId { get; init; }

    /// <summary>Dashboard link for the job.</summary>
    public required string Url { get; init; }

    /// <summary>Current job state.</summary>
    public required string State { get; init; }

    /// <summary>Amount spent in USD.</summary>
    public required decimal SpentUsd { get; init; }

    /// <summary>Spending limit in USD, if set.</summary>
    public decimal? LimitUsd { get; init; }

    /// <summary>Ids of the active steps.</summary>
    public required IReadOnlyList<string> ActiveStepIds { get; init; }

    /// <summary>One entry per step run.</summary>
    public required IReadOnlyList<JobStepRun> Steps { get; init; }

    /// <summary>Whether the job is driven by a workflow or a session, when reported.</summary>
    public string? DrivenBy { get; init; }

    /// <summary>The person who took over the job and when, as reported by the service.</summary>
    public JsonElement? TakenOver { get; init; }

    /// <summary>The job's tracker work item as JSON, when available.</summary>
    public JsonElement? WorkItem { get; init; }

    /// <summary>The job's workflow path as JSON, when available.</summary>
    public JsonElement? Path { get; init; }
}

/// <summary>The current state of a single step run.</summary>
public sealed record JobStepRun
{
    /// <summary>Step id.</summary>
    public required string StepId { get; init; }

    /// <summary>Execution round.</summary>
    public required int Round { get; init; }

    /// <summary>Step run state.</summary>
    public required string State { get; init; }

    /// <summary>Outcome, if known.</summary>
    public string? Outcome { get; init; }

    /// <summary>Agent, if assigned.</summary>
    public string? Agent { get; init; }

    /// <summary>Machine id, if assigned.</summary>
    public string? MachineId { get; init; }

    /// <summary>What the step is waiting on, if anything.</summary>
    public JsonElement? WaitingOn { get; init; }

    /// <summary>The step run's task thread id, if assigned.</summary>
    public string? ThreadId { get; init; }

    /// <summary>The step run's error, if reported.</summary>
    public string? Error { get; init; }

    /// <summary>The step run's cost in USD, if reported.</summary>
    public decimal? CostUsd { get; init; }
}
