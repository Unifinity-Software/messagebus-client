using MessageBus.Cli.Telemetry;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MessageBus.Cli.Tests.Telemetry;

/// <summary>The runner's status lines exported as logs (the original issue): stderr unchanged, the exported copy without agent output or credentials.</summary>
public sealed class RunnerLogBridgeTests
{
    [Fact]
    public void Exported_lines_keep_the_runner_s_own_words_but_not_agent_output_or_credentials()
    {
        var stderr = new StringWriter();
        var exported = new Collected();
        var bridge = new RunnerLogBridge(stderr, exported);

        bridge.WriteLine("dlv_01jcz7: failed (agent exited 1: Error: OPENAI_API_KEY=sk-proj-abcdefghijklmnopqrstuv is invalid)");
        bridge.WriteLine("No repository credential for acme/shop: https://x-access-token:ghs_0123456789abcdefghijABCDEF@github.com/acme/shop.git?token=t0k3n");
        bridge.Write("Heartbeat failed: 401 Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJlLXZhbHVl");
        bridge.Write("\n");

        Assert.Contains("sk-proj-abcdefghijklmnopqrstuv", stderr.ToString()); // the owner's own terminal still shows everything
        Assert.Equal("dlv_01jcz7: failed", exported.Lines[0].Text);
        Assert.Equal(LogLevel.Warning, exported.Lines[0].Level);
        var all = string.Join('\n', exported.Lines.Select(l => l.Text));
        foreach (var secret in new[] { "sk-proj", "ghs_0123456789", "t0k3n", "eyJhbGci", "OPENAI_API_KEY" })
            Assert.DoesNotContain(secret, all);
        Assert.Contains("No repository credential for acme/shop", all);
        Assert.Equal(3, exported.Lines.Count);
    }

    [Fact]
    public void Timestamped_lines_are_exported_without_the_time_and_still_without_agent_output()
    {
        var stderr = new StringWriter();
        var exported = new Collected();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 14, 2, 31, TimeSpan.Zero));
        foreach (var timeOnly in new[] { true, false })
        {
            // As the runner writes: the time in front (the original issue), then the bridge, then stderr.
            var log = new MessageBus.Cli.Output.TimestampedWriter(new RunnerLogBridge(stderr, exported), clock, timeOnly);
            log.WriteLine("dlv_01jcz7: failed (agent exited 1: the agent's own words)");
            log.Write("Runner connection: ");
            log.Write("connected\n");
        }

        var lines = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(4, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^(\d{4}-\d{2}-\d{2} )?\d{2}:\d{2}:\d{2} ", l));
        Assert.Equal(["dlv_01jcz7: failed", "Runner connection: connected", "dlv_01jcz7: failed", "Runner connection: connected"], exported.Lines.Select(l => l.Text));
    }

    private sealed class Collected : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }
}
