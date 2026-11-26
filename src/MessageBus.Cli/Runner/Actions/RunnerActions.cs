using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner.Actions;

internal interface IRunnerAction
{
    string Kind { get; }

    Task<SettleResult> RunAsync(TaskDelivery delivery, CancellationToken cancellationToken);
}

internal sealed class RunnerActions(IEnumerable<IRunnerAction> actions)
{
    private readonly Dictionary<string, IRunnerAction> _actions =
        actions.ToDictionary(action => action.Kind, StringComparer.Ordinal);

    public SettleResult? Refusal(RunnerAction action) =>
        _actions.ContainsKey(action.Kind)
            ? null
            : new SettleResult
            {
                Status = SettleStatus.Failed,
                FailureCategory = "refused",
                Error = $"This runner does not support the '{action.Kind}' action.",
            };
}
