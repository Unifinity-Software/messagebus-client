using MessageBus.Cli.Runner.Agents;
using MessageBus.Mcp;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class ResumedUsageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Claude_resume_subtracts_restored_cost_state_but_keeps_call_duration(bool error)
    {
        var folder = Path.Combine(Path.GetTempPath(), "usage-trace-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(folder, "agent");
        var work = Path.Combine(folder, "work");
        var transcript = SessionUsage.ClaudeFolder(home, work)!;
        transcript.Create();
        try
        {
            // cost-state is what current Claude Code persists/restores across --resume.
            File.WriteAllText(Path.Combine(transcript.FullName, "session.jsonl"),
                """{"type":"cost-state","totalCostUSD":1,"modelUsage":{"model-a":{"inputTokens":100,"cacheReadInputTokens":20,"cacheCreationInputTokens":10,"outputTokens":5,"costUSD":1}}}""" + "\n");
            var parser = ClaudeOutputParser.ForResume(home, work, "session");
            // A later background result supersedes the first total in the same process.
            parser.Observe("""{"type":"result","duration_ms":1000,"total_cost_usd":2,"modelUsage":{"model-a":{"inputTokens":200,"cacheReadInputTokens":40,"cacheCreationInputTokens":20,"outputTokens":10,"costUSD":2}}}""");
            parser.Observe("""{"type":"result","is_error":ERROR,"duration_ms":2000,"total_cost_usd":4,"modelUsage":{"model-a":{"inputTokens":300,"cacheReadInputTokens":60,"cacheCreationInputTokens":30,"outputTokens":15,"costUSD":3},"model-b":{"inputTokens":50,"outputTokens":10,"costUSD":1}}}""".Replace("ERROR", error ? "true" : "false"));
            var outcome = parser.Complete();
            Assert.Equal(error, outcome.ReportedError);
            var usage = outcome.Usage!;
            Assert.Equal(3m, usage.EstimatedCostUsd); // first delivery $1, resumed delivery $3, session $4
            Assert.Equal((310L, 20L, 40L, 20L), (usage.TokensIn, usage.TokensOut, usage.CachedTokensIn, usage.CacheWriteTokensIn));
            Assert.Equal(3, usage.ActiveSeconds);
            Assert.Equal(new decimal?[] { 2m, 1m }, usage.Models!.Select(m => m.EstimatedCostUsd));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Claude_reset_does_not_subtract_a_previous_session_total()
    {
        var folder = Path.Combine(Path.GetTempPath(), "usage-reset-" + Guid.NewGuid().ToString("N"));
        var transcript = SessionUsage.ClaudeFolder(folder, folder)!;
        transcript.Create();
        try
        {
            File.WriteAllText(Path.Combine(transcript.FullName, "session.jsonl"),
                """{"type":"cost-state","totalCostUSD":10,"modelUsage":{"model":{"inputTokens":1000,"outputTokens":100,"costUSD":10}}}""");
            var parser = ClaudeOutputParser.ForResume(folder, folder, "session");
            parser.Observe("""{"type":"result","total_cost_usd":1,"modelUsage":{"model":{"inputTokens":100,"outputTokens":10,"costUSD":1}}}""");
            Assert.Equal(1m, parser.Complete().Usage!.EstimatedCostUsd);
            Assert.Equal(100, parser.Complete().Usage!.TokensIn);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Codex_resumed_process_counts_only_its_completed_turns()
    {
        var parser = new CodexOutputParser("model");
        parser.Observe("""{"type":"thread.started","thread_id":"resumed-session"}""");
        parser.Observe("""{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":80,"output_tokens":10}}""");
        parser.Observe("""{"type":"turn.completed","usage":{"input_tokens":200,"cached_input_tokens":150,"output_tokens":20}}""");
        var usage = parser.Complete().Usage!;
        Assert.Equal((300L, 230L, 30L), (usage.TokensIn, usage.CachedTokensIn, usage.TokensOut));
    }
}
