using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

/// <summary>The thread's error (and the runner's log line, which prints it) never carries raw Codex <c>--json</c> events.</summary>
public sealed class RunErrorTextTests
{
    [Fact]
    public void Only_runner_preparation_failures_carry_a_no_attempt_category()
    {
        var setup = AgentRun.Compose(StopReason.None, null, (SettleStatus.Failed, "Setup failed"),
            null, null, null, [], TimeSpan.Zero);
        Assert.Equal("not-started", setup.FailureCategory);
        var claimed = new SettleInput
        {
            ThreadId = "thr_attempt",
            Result = new SettleResult { Status = SettleStatus.Failed, FailureCategory = "not-started" },
        };
        var attempted = AgentRun.Compose(StopReason.None, null, null, 1, null, claimed, [], TimeSpan.Zero);
        Assert.Null(attempted.FailureCategory);
    }

    [Fact]
    public void Refused_settle_only_explains_a_missing_answer_and_keeps_earlier_failure_precedence()
    {
        SettleResult Run(int exit, string? refusal) => AgentRun.Compose(StopReason.None, null, null, exit,
            null, null, [], TimeSpan.Zero, workTree: "kept-work", refusal: refusal);
        Assert.Equivalent(Run(1, null), Run(1, "An answer was refused."), strict: true);
        Assert.Equal(SettleStatus.Abandoned, Run(1, "An answer was refused.").Status);

        var missing = Run(0, new string('x', 10_000));
        Assert.StartsWith(AgentRun.ResultNotReceived, missing.Error);
        Assert.Contains(new string('x', 100), missing.Error);
        Assert.Contains("Its work is kept on this machine in kept-work for 24 hours.", missing.Error);
        Assert.True(missing.Error!.Length < AgentRun.AgentErrorLength + 200);

        // A later corrected settle wins over an earlier refusal.
        Assert.Equal(SettleStatus.Succeeded, AgentRun.Compose(StopReason.None, null, null, 0, null,
            new SettleInput { ThreadId = "thr_fixed", Result = new SettleResult { Status = SettleStatus.Succeeded } },
            [], TimeSpan.Zero, refusal: "An answer was refused.").Status);
    }

    // Recorded from a real run that settled itself as failed and then exited 0.
    private static readonly string[] Recorded =
    [
        """{"type":"thread.started","thread_id":"019a8c2e-5b1d-7c40-9e2f-3a7d1c6b8e40"}""",
        """{"type":"turn.started"}""",
        """{"type":"item.started","item":{"id":"item_14","type":"command_execution","command":"bash -lc 'ls captures'","aggregated_output":"","exit_code":null,"status":"in_progress"}}""",
        """{"type":"item.completed","item":{"id":"item_15","type":"agent_message","text":"No capture files were created. I’m settling the task as failed because the capture step never ran."}}""",
        """{"type":"item.started","item":{"id":"item_16","type":"mcp_tool_call","server":"mbus","tool":"settle","arguments":{"threadId":"thr_1","result":{"status":"failed"}},"result":null,"error":null,"status":"in_progress"}}""",
        """{"type":"item.completed","item":{"id":"item_16","type":"mcp_tool_call","server":"mbus","tool":"settle","arguments":{"threadId":"thr_1","result":{"status":"failed"}},"result":{"content":[]},"error":null,"status":"completed"}}""",
        """{"type":"turn.completed","usage":{"input_tokens":51234,"cached_input_tokens":48000,"output_tokens":910}}""",
    ];

    [Fact]
    public void Codex_runs_end_with_a_readable_error_never_raw_events()
    {
        SettleResult Run(string[] lines, int exitCode, SettleResult? settled)
        {
            var parser = new CodexOutputParser("gpt-5-codex");
            foreach (var line in lines) parser.Observe(line);
            var agentSettle = settled is null ? null : new SettleInput { ThreadId = "thr_1", Result = settled };
            return AgentRun.Compose(StopReason.None, null, null, exitCode, parser.Complete(), agentSettle, lines, TimeSpan.FromMinutes(3));
        }

        // Settled as failed by the agent, with only a summary: its summary is the error.
        var failed = Run(Recorded, 0, new SettleResult { Status = SettleStatus.Failed, Summary = "No capture files were created.", FrictionKinds = ["refused", "unknown-kind"] });
        Assert.Equal(SettleStatus.Failed, failed.Status);
        Assert.Equal("No capture files were created.", failed.Error);
        Assert.Equal(new[] { "refused" }, failed.FrictionKinds);

        // Settled as succeeded: no error at all.
        var succeeded = Run(Recorded, 0, new SettleResult { Status = SettleStatus.Succeeded, Summary = "Captured." });
        Assert.Equal(SettleStatus.Succeeded, succeeded.Status);
        Assert.Null(succeeded.Error);

        // Crashed without settling: the agent's last message, one line, no JSON.
        var crashed = Run(Recorded[..4], 1, null);
        Assert.Equal(SettleStatus.Failed, crashed.Status);
        Assert.Equal("The agent exited with code 1. Its last message: No capture files were created. I’m settling the task as failed because the capture step never ran.", crashed.Error);

        // Crashed before saying anything, with an API error passed on as JSON: its message, capped, no JSON.
        string[] apiError = [Recorded[0], Recorded[1], """{"type":"turn.failed","error":{"message":"{\"error\":{\"type\":\"server_error\",\"message\":\"The server had an error while processing your request.\"}}"}}"""];
        var turnFailed = Run(apiError, 1, null);
        Assert.Equal("The agent exited with code 1. The server had an error while processing your request.", turnFailed.Error);

        // Nothing readable at all: no JSON fragments, just the exit code.
        var silent = Run(Recorded[..3], 137, null);
        Assert.Equal(SettleStatus.Abandoned, silent.Status);
        Assert.Equal("The agent stopped unexpectedly and exited with code 137.", silent.Error);
        Assert.DoesNotContain('{', failed.Error + crashed.Error + turnFailed.Error + silent.Error);

        // A crash that printed a bracketed error to stderr: that line is plain text, kept.
        string[] errno = [Recorded[0], "[Errno 13] Permission denied: '/work/out'"];
        Assert.Equal("The agent stopped unexpectedly and exited with code 1. [Errno 13] Permission denied: '/work/out'", Run(errno, 1, null).Error);

        // Unwrapped JSON that is still JSON is dropped, not shown raw.
        Assert.Null(AgentRun.Readable("""{"message":"{\"code\":7}"}"""));

        // The agent's own error keeps its lines and isn't cut to one short line; JSON is still unwrapped.
        var ownError = "Step 1 failed.\n" + new string('x', 600);
        var own = Run(Recorded, 0, new SettleResult { Status = SettleStatus.Failed, Summary = "Failed.", Error = ownError });
        Assert.Equal(ownError, own.Error);
        var ownJson = Run(Recorded, 0, new SettleResult { Status = SettleStatus.Failed, Error = """{"error":{"message":"Quota exceeded."}}""" });
        Assert.Equal("Quota exceeded.", ownJson.Error);
    }
}
