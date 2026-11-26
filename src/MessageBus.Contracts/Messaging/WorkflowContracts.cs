namespace MessageBus.Contracts.Messaging;

/// <summary>Protocol capabilities for workflow-aware clients and services.</summary>
public static class WorkflowCapabilities
{
    /// <summary>The token in the service's server-info capabilities list.</summary>
    public const string Workflows = "workflows";
}

/// <summary>Service-provided context for a task dispatched by a workflow step.</summary>
public sealed record StepContext
{
    /// <summary>The workflow job id.</summary>
    public required string JobId { get; init; }

    /// <summary>The step id in the published workflow.</summary>
    public required string StepId { get; init; }

    /// <summary>The step's display name.</summary>
    public required string StepName { get; init; }

    /// <summary>The execution round of the step.</summary>
    public required int Round { get; init; }

    /// <summary>The outcome words the step can report.</summary>
    public required IReadOnlyList<string> AllowedOutcomes { get; init; }

    /// <summary>Pointers to earlier step results supplied as inputs.</summary>
    public required IReadOnlyList<StepInput> Inputs { get; init; }
}

/// <summary>A pointer to an input from an earlier workflow step.</summary>
public sealed record StepInput
{
    /// <summary>The source step id.</summary>
    public required string FromStep { get; init; }

    /// <summary>The thread containing the source result.</summary>
    public required string ThreadId { get; init; }

    /// <summary>The input to read, such as <c>result</c> or <c>branch</c>.</summary>
    public required string What { get; init; }
}

/// <summary>Display metadata for a workflow exhibit.</summary>
public sealed record ExhibitRef
{
    /// <summary>The exhibit's display name.</summary>
    public required string Name { get; init; }

    /// <summary>The exhibit's media type.</summary>
    public required string MediaType { get; init; }

    /// <summary>The exhibit's size in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>An optional caption.</summary>
    public string? Caption { get; init; }
}
