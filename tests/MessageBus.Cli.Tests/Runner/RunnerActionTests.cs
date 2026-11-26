using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class RunnerActionTests
{
    [Fact]
    public async Task An_unknown_action_is_refused_before_creating_a_workspace()
    {
        await using var runner = new RunnerHarness();
        var delivery = runner.Task("dlv_unknown_action") with
        {
            RunnerAction = new RunnerAction { Kind = "future-action" },
        };

        var decision = await runner.DeliverAsync(delivery);

        Assert.False(decision.Accepted);
        var result = Assert.IsType<SettleResult>(decision.Result);
        Assert.Equal(SettleStatus.Failed, result.Status);
        Assert.Equal("refused", result.FailureCategory);
        Assert.False(Directory.Exists(runner.WorkTreeOf(delivery.DeliveryId)));
    }
}
