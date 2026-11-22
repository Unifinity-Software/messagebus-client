using MessageBus.Cli.Runner.Agents;
using MessageBus.Cli.Runner;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Mcp;
using System.Text;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class ResumedUsageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claude_resume_subtracts_restored_cost_state_but_keeps_call_duration(bool error)
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
            var parser = await ClaudeOutputParser.ForResumeAsync(home, work, "session", TestContext.Current.CancellationToken);
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
    public async Task Claude_reset_does_not_subtract_a_previous_session_total()
    {
        var folder = Path.Combine(Path.GetTempPath(), "usage-reset-" + Guid.NewGuid().ToString("N"));
        var transcript = SessionUsage.ClaudeFolder(folder, folder)!;
        transcript.Create();
        try
        {
            File.WriteAllText(Path.Combine(transcript.FullName, "session.jsonl"),
                """{"type":"cost-state","totalCostUSD":10,"modelUsage":{"model":{"inputTokens":1000,"outputTokens":100,"costUSD":10}}}""");
            var parser = await ClaudeOutputParser.ForResumeAsync(folder, folder, "session", TestContext.Current.CancellationToken);
            parser.Observe("""{"type":"result","total_cost_usd":1,"modelUsage":{"model":{"inputTokens":100,"outputTokens":10,"costUSD":1}}}""");
            Assert.Equal(1m, parser.Complete().Usage!.EstimatedCostUsd);
            Assert.Equal(100, parser.Complete().Usage!.TokensIn);
            parser.Observe("""{"type":"result","total_cost_usd":12,"modelUsage":{"model":{"inputTokens":1200,"outputTokens":120,"costUSD":12}}}""");
            Assert.Equal(12m, parser.Complete().Usage!.EstimatedCostUsd);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("malformed-counts")]
    [InlineData("read-error")]
    public async Task An_unavailable_resume_baseline_counts_only_observed_calls_with_unknown_cost(string failure)
    {
        using var transcript = new Transcript();
        if (failure == "malformed") File.WriteAllText(transcript.Path, """{"type":"cost-state","totalCostUSD":"invalid","modelUsage":{}}""");
        if (failure == "malformed-counts") File.WriteAllText(transcript.Path,
            """{"type":"cost-state","totalCostUSD":1,"modelUsage":{"model":{"inputTokens":"invalid","outputTokens":3,"costUSD":1}}}""");
        var parser = await ClaudeOutputParser.ForResumeAsync(transcript.Home, transcript.Work, "session",
            TestContext.Current.CancellationToken, failure == "read-error"
                ? _ => new ProbeStream(Encoding.UTF8.GetBytes("{}"), _ => throw new IOException("Transient transcript read failure.")) : null);
        parser.Observe("""{"type":"assistant","message":{"id":"a","model":"current-model","usage":{"input_tokens":5,"cache_read_input_tokens":2,"cache_creation_input_tokens":1,"output_tokens":3}}}""");
        parser.Observe("""{"type":"assistant","message":{"id":"a","model":"current-model","usage":{"input_tokens":5,"cache_read_input_tokens":2,"cache_creation_input_tokens":1,"output_tokens":5}}}""");
        parser.Observe("""{"type":"assistant","message":{"id":"b","model":"helper-model","usage":{"input_tokens":4,"output_tokens":2}}}""");
        parser.Observe("""{"type":"result","duration_ms":3000,"total_cost_usd":999,"modelUsage":{"old-model":{"inputTokens":9999,"outputTokens":9999,"costUSD":999}}}""");
        var usage = parser.Complete().Usage!;
        Assert.Equal((12L, 7L, 2L, 1L), (usage.TokensIn, usage.TokensOut, usage.CachedTokensIn, usage.CacheWriteTokensIn));
        Assert.Equal(AgentKind.ClaudeCode, usage.AgentKind);
        Assert.Equal("anthropic", usage.Vendor);
        Assert.Equal("current-model", usage.Model);
        Assert.Equal(3, usage.ActiveSeconds);
        Assert.Null(usage.EstimatedCostUsd);
        Assert.Equal(new[] { "current-model", "helper-model" }, usage.Models!.Select(m => m.Model));
        Assert.All(usage.Models!, m => Assert.Null(m.EstimatedCostUsd));
    }

    [Fact]
    public async Task Resume_baseline_reads_a_bounded_tail_after_huge_lines_and_uses_the_last_valid_cost_state()
    {
        using var transcript = new Transcript();
        var content = new string('x', 8 * 1024 * 1024) + "\n"
            + """{"type":"cost-state","totalCostUSD":1,"modelUsage":{"model":{"inputTokens":10,"outputTokens":1,"costUSD":1}}}""" + "\n"
            + """{"type":"cost-state","totalCostUSD":3,"modelUsage":{"model":{"inputTokens":30,"outputTokens":3,"costUSD":3}}}""" + "\n"
            + """{"type":"cost-state","totalCostUSD":999,"modelUsage":""";
        var stream = new ProbeStream(Encoding.UTF8.GetBytes(content));
        var parser = await ClaudeOutputParser.ForResumeAsync(transcript.Home, transcript.Work, "session",
            TestContext.Current.CancellationToken, _ => stream);
        parser.Observe("""{"type":"result","total_cost_usd":7,"modelUsage":{"model":{"inputTokens":70,"outputTokens":7,"costUSD":7}}}""");
        var usage = parser.Complete().Usage!;
        Assert.Equal(4m, usage.EstimatedCostUsd);
        Assert.Equal(40, usage.TokensIn);
        Assert.InRange(stream.BytesRead, 1, 4 * 1024 * 1024 + 1);
    }

    [Fact]
    public async Task Cancelling_a_baseline_read_propagates_instead_of_starting_with_unknown_cost()
    {
        using var transcript = new Transcript();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ClaudeOutputParser.ForResumeAsync(transcript.Home, transcript.Work, "session", cancellation.Token,
                _ => new ProbeStream(Encoding.UTF8.GetBytes("{}"), token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); })));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("folder/session")]
    [InlineData("folder\\session")]
    public async Task A_session_identity_cannot_select_a_transcript_outside_its_directory(string session)
    {
        using var transcript = new Transcript();
        var parser = await ClaudeOutputParser.ForResumeAsync(transcript.Home, transcript.Work, session,
            TestContext.Current.CancellationToken, _ => throw new InvalidOperationException("An invalid session must not be opened."));
        parser.Observe("""{"type":"result","total_cost_usd":99,"usage":{"input_tokens":900}}""");
        Assert.Null(parser.Complete().Usage!.EstimatedCostUsd);
        Assert.Equal(0, parser.Complete().Usage!.TokensIn);
    }

    [Fact]
    public async Task A_locked_transcript_does_not_stop_a_resumed_agent_run_or_charge_session_totals()
    {
        if (!OperatingSystem.IsWindows()) return; // The real sharing-denial fixture uses Windows file sharing rules.
        using var transcript = new Transcript();
        File.WriteAllText(transcript.Path, """{"type":"cost-state","totalCostUSD":12,"modelUsage":{}}""");
        await using var harness = new RunnerHarness(
            policy: new RunnerPolicy { OwnerSubjectId = RunnerHarness.Owner, AcceptsWorkFrom = new AcceptsWorkFrom { Mode = AcceptsWorkFromMode.OwnProjects } },
            environment: new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = transcript.Home });
        harness.Sessions.Save(new SessionRecord
        {
            InboxId = "inb_resume", WorkspaceId = "ws_test", AgentId = "claude-code", AgentKind = "claude-code",
            Folder = transcript.Work, SessionId = "session", CreatedAt = harness.Time.GetUtcNow(), ExpiresAt = harness.Time.GetUtcNow().AddHours(1),
        });
        var delivery = harness.Task("dlv_resume_io") with { Role = null, Instructions = null, InboxOwner = RunnerHarness.Owner };
        delivery = delivery with { Envelope = delivery.Envelope with { Target = new InboxTarget { InboxId = "inb_resume" }, Type = "task.implement.reply" } };
        using var locked = new FileStream(transcript.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True((await harness.DeliverAsync(delivery)).Accepted);
        var result = (await harness.Service.SettledAsync(delivery.DeliveryId)).Result;
        Assert.Equal(SettleStatus.Succeeded, result.Status);
        Assert.True(File.Exists(System.IO.Path.Combine(transcript.Work, "fake-agent-args.txt")));
        Assert.Null(result.Usage!.EstimatedCostUsd);
        Assert.Equal(0, result.Usage.TokensIn);
        Assert.Equal(AgentKind.ClaudeCode, result.Usage.AgentKind);
        Assert.Equal("anthropic", result.Usage.Vendor);
        Assert.Equal(4.2, result.Usage.ActiveSeconds);
    }

    private sealed class Transcript : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "usage-baseline-" + Guid.NewGuid().ToString("N"));
        public string Home => System.IO.Path.Combine(_root, "agent");
        public string Work => System.IO.Path.Combine(_root, "work");
        public string Path { get; }

        public Transcript()
        {
            Directory.CreateDirectory(Work);
            var directory = SessionUsage.ClaudeFolder(Home, Work)!;
            directory.Create();
            Path = System.IO.Path.Combine(directory.FullName, "session.jsonl");
        }

        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class ProbeStream(byte[] bytes, Action<CancellationToken>? beforeRead = null) : MemoryStream(bytes, writable: false)
    {
        public int BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            beforeRead?.Invoke(cancellationToken);
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
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
