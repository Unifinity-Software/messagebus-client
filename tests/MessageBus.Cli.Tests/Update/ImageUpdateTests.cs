using MessageBus.Cli.Configuration;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Cli.Tests.Runner;
using MessageBus.Cli.Update;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Releases;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Update;

public sealed class ImageUpdateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Image_refuses_manual_updates_and_checks_even_with_a_signed_feed(bool check)
    {
        using var machine = new TestMachine { ImageManaged = true };
        using var feed = new TestReleaseFeed();
        var installed = File.ReadAllText(feed.InstallCurrent("1.0.0"));
        feed.Publish("2.0.0");
        machine.Env[ReleaseKey.EnvVar] = feed.PublicKey;
        machine.Env[UpdateEnvVars.Feed] = feed.FeedFolder;
        machine.Env[UpdateEnvVars.Target] = feed.Target;

        var run = await machine.RunAsync(check ? ["update", "--check"] : ["update", "--version", "2.0.0", "--yes"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(ImageUpdatePolicy.Reason, run.Error);
        Assert.Equal(installed, File.ReadAllText(feed.Target));
        Assert.Empty(feed.Versions());
    }

    [Fact]
    public async Task Image_advertises_and_reports_the_redeploy_reason_for_remote_updates()
    {
        using var machine = new TestMachine { ImageManaged = true };
        machine.Env[ReleaseKey.EnvVar] = "invalid key";
        machine.Env[UpdateEnvVars.Feed] = "invalid://feed";
        machine.Env[UpdateEnvVars.Target] = "\0";
        using var startup = new StringWriter();
        Assert.Null(RunnerUpdateSetup.TryCreate(machine.CreateHost(), new CliSettings { Stored = new(), MachineName = "test-runner" }, startup, null, out var ability));
        Assert.False(ability.CanUpdate);
        Assert.Equal(ImageUpdatePolicy.Reason, ability.Reason);
        Assert.Contains(ability.Reason, startup.ToString());

        var doctor = await machine.RunAsync("doctor");
        Assert.Contains("redeploy", doctor.Output);

        await using var runner = new RunnerHarness(extensions: parts => parts with { Updates = ability });
        var ct = TestContext.Current.CancellationToken;
        await runner.Engine.RescanAsync(ct);
        Assert.Equal(ability, (await runner.Service.NextInventoryAsync()).Updates);

        var request = new UpdateRequest { Version = "2.0.0", Mirror = "invalid://mirror", Channel = "preview", Pinned = true };
        var response = await runner.Routes.HandleAsync("POST", RunnerRoutes.Update, MessageBusJson.Serialize(request), ct);
        Assert.Equal(202, response.StatusCode);

        var progress = await runner.Service.NextUpdateProgressAsync();
        Assert.Equal(RunnerUpdateStage.Ended, progress.Stage);
        Assert.Equal(RunnerUpdateOutcome.NotInstalled, progress.Report?.Outcome);
        Assert.Equal(ability.Reason, progress.Report?.Reason);
        Assert.False(runner.Engine.GetStatus().Draining);
        Assert.False(runner.Engine.UpdateReady.IsCompleted);
    }
}
