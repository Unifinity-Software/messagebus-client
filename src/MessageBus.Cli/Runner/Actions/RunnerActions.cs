using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner.Actions;

internal sealed record RunnerActionContext
{
    public required TaskDelivery Delivery { get; init; }
    public required string RunDirectory { get; init; }
    public required string WorkDirectory { get; init; }
    public string InputsDirectory => Path.Combine(WorkDirectory, ".mbus", "inputs");
    public IReadOnlyDictionary<string, string> InputBranches { get; init; } = new Dictionary<string, string>();
    public required TextWriter Log { get; init; }
    internal Exhibits? Exhibits { get; init; }

    internal Func<ProcessLaunch, CancellationToken, Task<(int ExitCode, IReadOnlyList<string> Tail)>>? RunProcessAsync { get; init; }
}

internal interface IRunnerAction
{
    string Kind { get; }

    Task<SettleResult> RunAsync(RunnerActionContext context, CancellationToken cancellationToken);
}

internal sealed class RunnerActions(IEnumerable<IRunnerAction> actions)
{
    private readonly Dictionary<string, IRunnerAction> _actions =
        actions.ToDictionary(action => action.Kind, StringComparer.Ordinal);

    public IRunnerAction? Find(RunnerAction action) => _actions.GetValueOrDefault(action.Kind);

    public SettleResult? Refusal(RunnerAction action) =>
        Find(action) is not null
            ? null
            : new SettleResult
            {
                Status = SettleStatus.Failed,
                FailureCategory = "refused",
                Error = $"This runner does not support the '{action.Kind}' action.",
            };
}
