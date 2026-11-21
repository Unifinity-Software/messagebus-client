using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class VerificationDeliveryTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Verification_deliveries_are_declined_before_creating_a_workspace(bool withRequest, bool withSnapshot)
    {
        await using var runner = new RunnerHarness();
        var task = runner.Task("dlv_verified");
        var request = new VerificationRequest { Environment = "frontend", RequiredChecks = ["build"] };
        var snapshot = new VerificationSnapshot
        {
            Environment = "frontend", Recipe = "node-npm-ci", Version = "1", Digest = new string('a', 64),
            NodeVersion = "24.1.0", NpmVersion = "11.3.0", Repository = "gh:acme/api", Remote = "https://github.com/acme/api.git",
            Revision = new string('b', 40), BaseRevision = new string('c', 40), RequiredChecks = ["build"],
        };

        var decision = await runner.DeliverAsync(task with
        {
            Envelope = task.Envelope with { Verification = withRequest ? request : null },
            Verification = withSnapshot ? snapshot : null,
        });

        Assert.False(decision.Accepted);
        Assert.Equal(DeclineReason.VerificationUnavailable, decision.Reason);
        Assert.False(Directory.Exists(Path.Combine(runner.WorkRoot, task.DeliveryId + "-1")));
    }
}
