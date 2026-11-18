using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Machines;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

/// <summary>Usage and progress from output shaped like what the real agent CLIs print.</summary>
public sealed class AgentOutputParserTests
{
    [Fact]
    public void Claude_stream_json_gives_progress_and_usage_summed_over_models()
    {
        string[] lines =
        [
            """{"type":"system","subtype":"init","cwd":"/runs/dlv_1-1/work","session_id":"5f0c","tools":["Bash","Edit","Read"],"mcp_servers":[{"name":"mbus","status":"connected"}],"model":"claude-sonnet-4-5-20250929","permissionMode":"acceptEdits","apiKeySource":"none"}""",
            """{"type":"assistant","message":{"id":"msg_01","type":"message","role":"assistant","model":"claude-sonnet-4-5-20250929","content":[{"type":"text","text":"I'll add the due date field first."}],"usage":{"input_tokens":4,"output_tokens":12}},"parent_tool_use_id":null,"session_id":"5f0c"}""",
            """{"type":"assistant","message":{"id":"msg_02","type":"message","role":"assistant","model":"claude-sonnet-4-5-20250929","content":[{"type":"tool_use","id":"toolu_01","name":"Bash","input":{"command":"dotnet test","description":"Run the tests"}}]},"parent_tool_use_id":null,"session_id":"5f0c"}""",
            """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01","type":"tool_result","content":"Passed!  - Failed: 0, Passed: 42","is_error":false}]},"parent_tool_use_id":null,"session_id":"5f0c"}""",
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":83456,"duration_api_ms":61234,"num_turns":9,"result":"Added due dates; all 42 tests pass.","session_id":"5f0c","total_cost_usd":0.31415,"usage":{"input_tokens":30,"cache_creation_input_tokens":12000,"cache_read_input_tokens":150000,"cache_creation":{"ephemeral_5m_input_tokens":4000,"ephemeral_1h_input_tokens":8000},"output_tokens":4200,"server_tool_use":{"web_search_requests":0},"service_tier":"standard"},"modelUsage":{"claude-haiku-4-5-20251001":{"inputTokens":900,"outputTokens":80,"cacheReadInputTokens":0,"cacheCreationInputTokens":0,"webSearchRequests":0,"costUSD":0.0013,"contextWindow":200000},"claude-sonnet-4-5-20250929":{"inputTokens":30,"outputTokens":4200,"cacheReadInputTokens":150000,"cacheCreationInputTokens":12000,"webSearchRequests":0,"costUSD":0.31285,"contextWindow":200000}},"permission_denials":[],"uuid":"8d2e"}""",
        ];
        var parser = new ClaudeOutputParser();

        var notes = lines.Select(parser.Observe).ToList();
        var outcome = parser.Complete();

        Assert.Equal([null, "I'll add the due date field first.", "Bash: Run the tests", null, null], notes);
        Assert.False(outcome.ReportedError);
        Assert.Equal("Added due dates; all 42 tests pass.", outcome.FinalText);
        var usage = outcome.Usage!;
        Assert.Equal(AgentKind.ClaudeCode, usage.AgentKind);
        Assert.Equal("anthropic", usage.Vendor);
        Assert.Equal("claude-sonnet-4-5-20250929", usage.Model);
        Assert.Equal(900 + 30 + 150000 + 12000, usage.TokensIn);
        Assert.Equal(150000, usage.CachedTokensIn); // cache reads only: cache writes cost more, not less (the original issue)
        Assert.Equal((12000, 8000), (usage.CacheWriteTokensIn, usage.CacheWrite1hTokensIn)); // writes cost more than input, an hour's most (the original issue)
        Assert.Equal(80 + 4200, usage.TokensOut);
        Assert.Equal(0.31415m, usage.EstimatedCostUsd);
        Assert.Equal(83.456, usage.ActiveSeconds);
    }

    [Fact]
    public void Claude_run_resumed_after_background_work_takes_the_runs_time_and_the_last_results_totals()
    {
        // A run that started its build as a background task (the original issue): one result when the turn ends to wait for it, another
        // after the build's notification resumes it. duration_ms is per turn; usage, modelUsage and cost are running totals.
        string[] lines =
        [
            """{"type":"system","subtype":"init","cwd":"/runs/dlv_1-1/work","session_id":"7a1b","tools":["Bash","Read"],"model":"claude-opus-4-5","permissionMode":"acceptEdits"}""",
            """{"type":"assistant","message":{"id":"msg_01","type":"message","role":"assistant","model":"claude-opus-4-5","content":[{"type":"tool_use","id":"toolu_01","name":"Bash","input":{"command":"dotnet build","description":"Build the solution","run_in_background":true}}],"usage":{"input_tokens":5,"cache_read_input_tokens":20000,"output_tokens":40}},"parent_tool_use_id":null,"session_id":"7a1b"}""",
            """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01","type":"tool_result","content":"Command running in background with ID: bg1","is_error":false}]},"parent_tool_use_id":null,"session_id":"7a1b"}""",
            """{"type":"assistant","message":{"id":"msg_02","type":"message","role":"assistant","model":"claude-opus-4-5","content":[{"type":"text","text":"The build is running; I'll check when it finishes."}],"usage":{"input_tokens":3,"cache_read_input_tokens":20100,"output_tokens":15}},"parent_tool_use_id":null,"session_id":"7a1b"}""",
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":61200,"duration_api_ms":9100,"num_turns":3,"result":"The build is running; I'll check when it finishes.","session_id":"7a1b","total_cost_usd":0.12,"usage":{"input_tokens":8,"cache_creation_input_tokens":2000,"cache_read_input_tokens":40100,"output_tokens":55},"modelUsage":{"claude-opus-4-5":{"inputTokens":8,"outputTokens":55,"cacheReadInputTokens":40100,"cacheCreationInputTokens":2000,"costUSD":0.12}},"permission_denials":[],"uuid":"r1"}""",
            """{"type":"assistant","message":{"id":"msg_03","type":"message","role":"assistant","model":"claude-opus-4-5","content":[{"type":"text","text":"Build passed; settling."}],"usage":{"input_tokens":4,"cache_read_input_tokens":21000,"output_tokens":30}},"parent_tool_use_id":null,"session_id":"7a1b"}""",
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":3844,"duration_api_ms":11800,"num_turns":1,"result":"Build passed; settling.","session_id":"7a1b","total_cost_usd":0.19,"usage":{"input_tokens":12,"cache_creation_input_tokens":2500,"cache_read_input_tokens":61100,"output_tokens":85},"modelUsage":{"claude-opus-4-5":{"inputTokens":12,"outputTokens":85,"cacheReadInputTokens":61100,"cacheCreationInputTokens":2500,"costUSD":0.19}},"permission_denials":[],"uuid":"r2"}""",
        ];
        var parser = new ClaudeOutputParser();
        foreach (var line in lines) parser.Observe(line);

        var outcome = parser.Complete();

        Assert.True(outcome.SpansTurns);
        Assert.Equal("Build passed; settling.", outcome.FinalText);
        var usage = outcome.Usage!;
        Assert.Equal(12 + 61100 + 2500, usage.TokensIn); // the last result's running totals, not their sum
        Assert.Equal(85, usage.TokensOut);
        Assert.Equal(0.19m, usage.EstimatedCostUsd);
        Assert.Equal(65.044, usage.ActiveSeconds); // both turns; the build's wait between them is in neither

        // The run took 22 minutes by the runner's clock: that is the run's time, not the last turn's 3.8 s.
        var settled = AgentRun.Compose(StopReason.None, null, null, 0, outcome, null, [], TimeSpan.FromMinutes(22));
        Assert.Equal(1320, settled.Usage!.ActiveSeconds);
    }

    [Fact]
    public void Claude_json_error_result_is_reported_and_usage_falls_back_to_the_usage_block()
    {
        var parser = new ClaudeOutputParser();
        parser.Observe("""{"type":"result","subtype":"error_max_turns","is_error":true,"duration_ms":5000,"num_turns":30,"session_id":"aa1","total_cost_usd":0.02,"usage":{"input_tokens":100,"cache_creation_input_tokens":0,"cache_read_input_tokens":900,"output_tokens":50}}""");

        var outcome = parser.Complete();

        Assert.True(outcome.ReportedError);
        Assert.Equal("error_max_turns", outcome.ErrorText);
        Assert.Equal(1000, outcome.Usage!.TokensIn);
        Assert.Equal(900, outcome.Usage.CachedTokensIn);
        Assert.Equal(50, outcome.Usage.TokensOut);
    }

    [Fact]
    public void Codex_jsonl_gives_progress_tokens_and_the_last_message_and_survives_a_recovered_error()
    {
        string[] lines =
        [
            """{"type":"thread.started","thread_id":"0199a213-81c0-7800-8aa1-bbab2a035a53"}""",
            """{"type":"turn.started"}""",
            """{"type":"item.completed","item":{"id":"item_0","type":"reasoning","text":"**Planning the change**"}}""",
            """{"type":"error","message":"Reconnecting... 1/5"}""",
            """{"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"bash -lc 'dotnet test'","aggregated_output":"","exit_code":null,"status":"in_progress"}}""",
            """{"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"bash -lc 'dotnet test'","aggregated_output":"Passed: 42","exit_code":0,"status":"completed"}}""",
            """{"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"Due dates added and tested."}}""",
            """{"type":"turn.completed","usage":{"input_tokens":24763,"cached_input_tokens":24448,"output_tokens":122}}""",
        ];
        var parser = new CodexOutputParser("gpt-5-codex");

        var notes = lines.Select(parser.Observe).Where(n => n is not null).ToList();
        var outcome = parser.Complete();

        Assert.Equal(["Running bash -lc 'dotnet test'", "Due dates added and tested."], notes);
        Assert.False(outcome.ReportedError);
        Assert.Equal("Due dates added and tested.", outcome.FinalText);
        Assert.Equal("gpt-5-codex", outcome.Usage!.Model);
        Assert.Equal(24763, outcome.Usage.TokensIn);
        Assert.Equal(24448, outcome.Usage.CachedTokensIn);
        Assert.Equal(122, outcome.Usage.TokensOut);
        Assert.Null(outcome.Usage.EstimatedCostUsd);
    }

    [Fact]
    public void Codex_turn_failed_is_reported_as_an_error()
    {
        var parser = new CodexOutputParser(null);
        parser.Observe("""{"type":"turn.started"}""");
        parser.Observe("""{"type":"turn.failed","error":{"message":"stream disconnected before completion"}}""");

        var outcome = parser.Complete();

        Assert.True(outcome.ReportedError);
        Assert.Equal("stream disconnected before completion", outcome.ErrorText);
    }

    [Fact]
    public void Codex_model_comes_from_the_run_before_the_settings()
    {
        // Codex's session log for the run's thread, shaped like ~/.codex/sessions/yyyy/MM/dd/rollout-….jsonl.
        var home = Directory.CreateTempSubdirectory("mbus-codex-").FullName;
        try
        {
            const string thread = "019a7c2e-5d1f-7a30-9c41-2b8e6f0d4a11";
            var day = Path.Combine(home, "sessions", DateTime.Now.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(day);
            File.WriteAllLines(Path.Combine(day, $"rollout-2026-11-13T14-22-19-{thread}.jsonl"),
            [
                """{"type":"session_meta","payload":{"id":"019a7c2e-5d1f-7a30-9c41-2b8e6f0d4a11","cli_version":"0.169.0"}}""",
                """{"type":"turn_context","payload":{"cwd":"/runs/dlv_1-1/work","model":"gpt-6.1-sol","effort":"high"}}""",
            ]);
            var fromLog = new CodexOutputParser("gpt-5", home);
            fromLog.Observe($$"""{"type":"thread.started","thread_id":"{{thread}}"}""");
            fromLog.Observe("""{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":5}}""");
            Assert.Equal("gpt-6.1-sol", fromLog.Complete().Usage!.Model);

            // A model named in the output itself (older Codex versions' session_configured) wins over both.
            var fromOutput = new CodexOutputParser("gpt-5", home);
            fromOutput.Observe("""{"id":"0","msg":{"type":"session_configured","session_id":"s1","model":"gpt-5.5"}}""");
            fromOutput.Observe($$"""{"type":"thread.started","thread_id":"{{thread}}"}""");
            Assert.Equal("gpt-5.5", fromOutput.Complete().Usage!.Model);

            // Nothing from the run: the settings' model; no settings either: unknown.
            Assert.Equal("gpt-5", new CodexOutputParser("gpt-5", home).Complete().Usage!.Model);
            Assert.Null(new CodexOutputParser(null, home).Complete().Usage!.Model);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Copilot_jsonl_gives_progress_the_last_message_and_the_runs_totals_over_its_per_call_usage()
    {
        // Shaped like copilot -p --output-format json (1.0.x): the session events Copilot also keeps in events.jsonl.
        string[] lines =
        [
            """{"type":"session.start","data":{"sessionId":"5c81","copilotVersion":"1.0.112","producer":"copilot-agent"},"id":"e1","timestamp":"2026-11-14T10:00:00Z","parentId":null}""",
            """{"type":"assistant.message","data":{"messageId":"m1","model":"gpt-6-luna","content":"I'll run the tests first.","toolRequests":[]},"id":"e2","timestamp":"2026-11-14T10:00:01Z","parentId":"e1"}""",
            """{"type":"tool.execution_start","data":{"toolCallId":"t1","toolName":"powershell","arguments":{"command":"dotnet test","description":"Run the tests"}},"id":"e3","timestamp":"2026-11-14T10:00:02Z","parentId":"e2"}""",
            """{"type":"assistant.usage","data":{"model":"gpt-6-luna","inputTokens":12000,"outputTokens":300,"cacheReadTokens":9000,"cacheWriteTokens":2000,"cost":1},"id":"e4","timestamp":"2026-11-14T10:00:03Z","parentId":"e2"}""",
            """{"type":"assistant.message","data":{"messageId":"m2","model":"gpt-6-luna","content":"Due dates added; 42 tests pass."},"id":"e5","timestamp":"2026-11-14T10:00:09Z","parentId":"e4"}""",
            """{"type":"assistant.usage","data":{"model":"gpt-6-luna","inputTokens":13000,"outputTokens":200,"cacheReadTokens":11000,"cacheWriteTokens":1000,"cost":0},"id":"e6","timestamp":"2026-11-14T10:00:10Z","parentId":"e5"}""",
        ];
        var parser = new CopilotOutputParser();

        var notes = lines.Select(parser.Observe).Where(n => n is not null).ToList();
        var perCall = parser.Complete();

        Assert.Equal(["I'll run the tests first.", "Running dotnet test", "Due dates added; 42 tests pass."], notes);
        Assert.False(perCall.ReportedError);
        Assert.Equal("Due dates added; 42 tests pass.", perCall.FinalText);
        Assert.Equal(("github", "gpt-6-luna"), (perCall.Usage!.Vendor, perCall.Usage.Model));
        Assert.Equal((25000L, 20000L, 3000L, 500L), (perCall.Usage.TokensIn, perCall.Usage.CachedTokensIn, perCall.Usage.CacheWriteTokensIn, perCall.Usage.TokensOut));
        Assert.Null(perCall.Usage.EstimatedCostUsd); // Copilot's cost counts premium requests, not dollars

        // The run's own totals at shutdown (they include sub-agents' calls) win over the per-call sum.
        parser.Observe("""{"type":"session.shutdown","data":{"shutdownType":"routine","currentModel":"gpt-6-luna","modelMetrics":{"gpt-6-luna":{"requests":{"count":3,"cost":1},"usage":{"inputTokens":51853,"outputTokens":1210,"cacheReadTokens":37179,"cacheWriteTokens":14662,"reasoningTokens":651}},"mai-code-1.1-flash":{"requests":{"count":1,"cost":0},"usage":{"inputTokens":6231,"outputTokens":549,"cacheReadTokens":0,"cacheWriteTokens":0}}}},"id":"e7","timestamp":"2026-11-14T10:00:11Z","parentId":"e6"}""");
        var totals = parser.Complete().Usage!;
        Assert.Equal("gpt-6-luna", totals.Model);
        Assert.Equal((51853L + 6231, 37179L, 14662L, 1210L + 549), (totals.TokensIn, totals.CachedTokensIn, totals.CacheWriteTokensIn, totals.TokensOut));

        var failed = new CopilotOutputParser();
        failed.Observe("""{"type":"session.error","data":{"errorType":"authentication","message":"No authentication information found."},"id":"e1","timestamp":"2026-11-14T10:00:00Z"}""");
        var failure = failed.Complete();
        Assert.Equal((true, "No authentication information found."), (failure.ReportedError, failure.ErrorText));
    }

    [Fact]
    public void OpenCode_json_gives_progress_the_last_messages_text_and_usage_with_cache_and_reasoning_counted()
    {
        // Shaped like opencode run --format json (2.3.0; 1.x prints the same events).
        string[] lines =
        [
            """{"type":"step_start","timestamp":1794650000000,"sessionID":"ses_1","part":{"id":"prt_1","sessionID":"ses_1","messageID":"msg_1","type":"step-start"}}""",
            """{"type":"tool_use","timestamp":1794650001000,"sessionID":"ses_1","part":{"id":"prt_2","sessionID":"ses_1","messageID":"msg_1","type":"tool","tool":"shell","state":{"status":"completed","input":{"command":"dotnet test"},"output":"Passed: 42","title":"shell"}}}""",
            """{"type":"step_finish","timestamp":1794650002000,"sessionID":"ses_1","part":{"id":"prt_3","sessionID":"ses_1","messageID":"msg_1","type":"step-finish","reason":"tool-calls","cost":0.0125,"tokens":{"input":800,"output":120,"reasoning":40,"cache":{"read":15000,"write":2000}}}}""",
            """{"type":"text","timestamp":1794650003000,"sessionID":"ses_1","part":{"id":"prt_4","sessionID":"ses_1","messageID":"msg_2","type":"text","text":"Due dates added. ","time":{"start":1,"end":2}}}""",
            """{"type":"text","timestamp":1794650003500,"sessionID":"ses_1","part":{"id":"prt_5","sessionID":"ses_1","messageID":"msg_2","type":"text","text":"All 42 tests pass.","time":{"start":2,"end":3}}}""",
            """{"type":"step_finish","timestamp":1794650004000,"sessionID":"ses_1","part":{"id":"prt_6","sessionID":"ses_1","messageID":"msg_2","type":"step-finish","reason":"stop","cost":0.0075,"tokens":{"input":200,"output":60,"reasoning":0,"cache":{"read":17000,"write":0}}}}""",
        ];
        var parser = new OpenCodeOutputParser("anthropic/claude-sonnet-5-5");

        var notes = lines.Select(parser.Observe).Where(n => n is not null).ToList();
        var outcome = parser.Complete();

        Assert.Equal(["Running dotnet test", "Due dates added.", "All 42 tests pass."], notes);
        Assert.Equal("Due dates added. All 42 tests pass.", outcome.FinalText);
        var usage = outcome.Usage!;
        Assert.Equal(("anthropic", "claude-sonnet-5-5"), (usage.Vendor, usage.Model));
        // OpenCode's input leaves out the cache and its output the reasoning; the contract's counts include them.
        Assert.Equal((800L + 15000 + 2000 + 200 + 17000, 32000L, 2000L, 120L + 40 + 60), (usage.TokensIn, usage.CachedTokensIn, usage.CacheWriteTokensIn, usage.TokensOut));
        Assert.Equal(0.02m, usage.EstimatedCostUsd);

        // Version 1's error shape, and a run with nothing priced (a subscription): no cost, so the price list estimates it.
        var failed = new OpenCodeOutputParser(null);
        failed.Observe("""{"type":"step_finish","timestamp":1,"sessionID":"s","part":{"type":"step-finish","cost":0,"tokens":{"input":5,"output":1,"reasoning":0,"cache":{"read":0,"write":0}}}}""");
        failed.Observe("""{"type":"error","timestamp":2,"sessionID":"s","error":{"name":"ProviderAuthError","data":{"providerID":"anthropic","message":"No API key found for anthropic"}}}""");
        var result = failed.Complete();
        Assert.Equal((true, "No API key found for anthropic"), (result.ReportedError, result.ErrorText));
        Assert.Null(result.Usage!.EstimatedCostUsd);
    }
}
