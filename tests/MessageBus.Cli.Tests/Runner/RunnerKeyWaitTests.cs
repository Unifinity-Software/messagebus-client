using MessageBus.Cli.Runner;
using MessageBus.Cli.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class RunnerKeyWaitTests
{
    [Fact]
    public async Task Retries_with_bounded_backoff_then_recovers()
    {
        var time = new SignallingTimeProvider();
        var log = new StringWriter();
        var calls = 0;
        var loaded = new TaskCompletionSource();
        var waiting = RunnerKeyWait.WaitAsync(() =>
        {
            calls++;
            loaded.TrySetResult();
            return calls <= 7 ? throw new MachineKeyUnavailableException() : "existing identity";
        }, "login Keychain", log, time, CancellationToken.None);
        foreach (var seconds in new[] { 15, 30, 60, 120, 240, 300, 300 })
        {
            await loaded.Task;
            await time.TimerCreated.Task;
            var before = calls;
            loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            time.TimerCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            time.Clock.Advance(TimeSpan.FromSeconds(seconds - 1));
            Assert.Equal(before, calls);
            time.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        await waiting;
        Assert.Equal(8, calls);
        Assert.Contains("readable again", log.ToString());
        Assert.DoesNotContain("login again", log.ToString());
    }

    [Fact]
    public async Task Stop_cancels_the_wait_promptly()
    {
        using var stop = new CancellationTokenSource();
        var waiting = RunnerKeyWait.WaitAsync(() => throw new MachineKeyUnavailableException(), "Keychain", TextWriter.Null, new FakeTimeProvider(), stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task Missing_key_does_not_retry()
    {
        await RunnerKeyWait.WaitAsync(() => null, "Keychain", TextWriter.Null, new FakeTimeProvider(), CancellationToken.None);
    }

    private sealed class SignallingTimeProvider : TimeProvider
    {
        public FakeTimeProvider Clock { get; } = new();
        public TaskCompletionSource TimerCreated { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => Clock.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Clock.CreateTimer(callback, state, dueTime, period);
            TimerCreated.TrySetResult();
            return timer;
        }
    }
}
