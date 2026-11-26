namespace MessageBus.Contracts.Runner;

/// <summary>A service-provided workflow request for the runner. Unknown kinds can be read and refused.</summary>
public sealed record RunnerAction
{
    /// <summary>The action kind, as named by <see cref="RunnerActionKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Parameters for <c>run-command</c>.</summary>
    public RunCommandParameters? RunCommand { get; init; }

    /// <summary>Parameters for <c>open-pull-request</c>.</summary>
    public OpenPullRequestParameters? OpenPullRequest { get; init; }

    /// <summary>Parameters for <c>record-walkthrough</c>.</summary>
    public RecordWalkthroughParameters? RecordWalkthrough { get; init; }

    /// <summary>Parameters for <c>roll-back</c>.</summary>
    public RollBackParameters? RollBack { get; init; }
}

/// <summary>Well-known runner action kinds.</summary>
public static class RunnerActionKinds
{
    /// <summary>Run a command from the published workflow.</summary>
    public const string RunCommand = "run-command";

    /// <summary>Open a pull request.</summary>
    public const string OpenPullRequest = "open-pull-request";

    /// <summary>Record a walkthrough.</summary>
    public const string RecordWalkthrough = "record-walkthrough";

    /// <summary>Roll back a deployment.</summary>
    public const string RollBack = "roll-back";
}

/// <summary>Parameters for a command from the published workflow.</summary>
public sealed record RunCommandParameters
{
    /// <summary>The command specified by the published workflow.</summary>
    public required string Command { get; init; }

    /// <summary>A folder relative to the checkout root, which is the default.</summary>
    public string? Folder { get; init; }

    /// <summary>The command time limit in minutes. Defaults to 30.</summary>
    public int? TimeLimitMinutes { get; init; }
}

/// <summary>Parameters for opening a pull request.</summary>
public sealed record OpenPullRequestParameters
{
    /// <summary>The source step that produced the branch.</summary>
    public required string BranchFrom { get; init; }

    /// <summary>The destination branch.</summary>
    public required string Base { get; init; }

    /// <summary>Whether the published workflow requests auto-merge.</summary>
    public bool? AutoMerge { get; init; }
}

/// <summary>Parameters for recording a walkthrough.</summary>
public sealed record RecordWalkthroughParameters
{
    /// <summary>The page to record.</summary>
    public required string Url { get; init; }
}

/// <summary>Parameters for rolling back a deployment.</summary>
public sealed record RollBackParameters
{
    /// <summary>The deployment to roll back.</summary>
    public required string DeploymentId { get; init; }
}
