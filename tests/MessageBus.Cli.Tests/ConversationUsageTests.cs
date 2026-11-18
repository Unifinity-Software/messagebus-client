using MessageBus.Contracts.Messaging;
using MessageBus.Mcp;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class ConversationUsageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_busy_journal_times_out_using_the_injected_clock()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mbus-usage-" + Guid.NewGuid().ToString("N"));
        var time = new FakeTimeProvider();
        using var first = await ConversationUsage.OpenAsync(folder, "codex", "session", time, Ct);
        var waiting = ConversationUsage.OpenAsync(folder, "codex", "session", time, Ct);
        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(31));
        await Assert.ThrowsAsync<IOException>(() => waiting);
    }

    [Fact]
    public void Delta_names_the_largest_model_in_the_new_interval()
    {
        var previous = new Usage { TokensIn = 100, Models = [new() { Model = "old", TokensIn = 100 }] };
        var total = new Usage
        {
            Model = "old", TokensIn = 120,
            Models = [new() { Model = "old", TokensIn = 101 }, new() { Model = "new", TokensIn = 19 }],
        };
        Assert.Equal("new", ConversationUsage.Delta(total, previous)!.Model);
    }
}
