using System.Diagnostics;
using MessageBus.Cli.Readiness;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

/// <summary>
/// The runner end to end on this machine: deliveries through the real route handlers, real agent
/// processes (the fake agent, answering in claude's and codex's JSON), settles through the real HTTP
/// service client. Time only moves when a test advances the fake clock.
/// </summary>
public sealed class RunnerTaskTests
{
    [Fact]
    public async Task A_fresh_runner_role_key_refusal_is_reported_on_the_callback()
    {
        await using var h = new RunnerHarness(roleKeysReadable: new Dictionary<string, bool> { ["developer"] = false });
        var environment = new RunEnvironment { Checkout = CheckoutMode.Empty, SetupCommand = "echo developer > ../settle.json.role-key" };
        var delivery = h.Task("dlv_refused_key", directives: d => d["mode"] = "settle-failed", environment: environment) with { Role = "developer" };
        Assert.True((await h.DeliverAsync(delivery)).Accepted);
        var settle = await h.Service.SettledAsync(delivery.DeliveryId);
        Assert.Equal("developer", settle.UnreadableRole);
    }

    [Fact]
    public async Task A_forged_role_key_hint_cannot_report_a_refusal()
    {
        await using var h = new RunnerHarness();
        var environment = new RunEnvironment { Checkout = CheckoutMode.Empty, SetupCommand = "echo developer > ../settle.json.role-key" };
        Assert.True((await h.DeliverAsync(h.Task("dlv_key", directives: d => d["mode"] = "settle-failed", environment: environment))).Accepted);
        var settle = await h.Service.SettledAsync("dlv_key");
        Assert.Null(settle.UnreadableRole);
        Assert.Null(settle.Declined); // Other roles remain usable; this isn't whole-machine readiness.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_a_refused_settle_explains_the_missing_answer_and_keeps_the_unreturned_work(bool refusedSettle)
    {
        const string refusal = "The workspace key was replaced. Compare its fingerprint, then run mbus keys trust workspace.";
        await using var h = new RunnerHarness(keepRunFolders: false, extensions: e => e with { Tools = new RefusedTools(refusedSettle ? refusal : null) });
        Assert.True((await h.DeliverAsync(h.Task("dlv_refused", directives: d => d["settle"] = "no"))).Accepted);
        var result = (await h.Service.SettledAsync("dlv_refused")).Result;
        await h.Engine.StopAsync();
        Assert.Equal(SettleStatus.Failed, result.Status);
        Assert.StartsWith(AgentRun.ResultNotReceived, result.Error);
        if (refusedSettle) Assert.Contains("the agent's answer was refused: " + refusal, result.Error);
        else Assert.DoesNotContain("refused:", result.Error);
        Assert.DoesNotContain("unrelated error", result.Error);
        Assert.Contains($"Its work is kept on this machine in {h.WorkTreeOf("dlv_refused")} for 24 hours.", result.Error);
        Assert.True(Directory.Exists(h.WorkTreeOf("dlv_refused")));
    }

    private sealed class RefusedTools(string? refusal) : IRunTools
    {
        public Task<RunToolsSession> StartAsync(RunToolsRequest request, CancellationToken cancellationToken)
        {
            request.Refused?.Invoke(McpToolNames.Wait, new McpError { Code = McpErrorCodes.InvalidInput, Message = "An earlier error." });
            if (refusal is not null)
                request.Refused?.Invoke(McpToolNames.Settle, new McpError { Code = McpErrorCodes.Forbidden, Message = refusal });
            request.Refused?.Invoke(McpToolNames.Directory, new McpError { Code = McpErrorCodes.InvalidInput, Message = "A later unrelated error." });
            return Task.FromResult(new RunToolsSession(new Dictionary<string, string>(), () => ValueTask.CompletedTask));
        }
    }

    [Theory]
    [InlineData("agt_codex", "refuse")]
    [InlineData("claude-code", "refuse")]
    [InlineData("claude-code", "allow")]
    [InlineData("agt_codex", "allow")]
    public async Task Tools_that_cannot_be_kept_fail_before_checkout_setup_or_agent_start(string agent, string policy)
    {
        await using var h = new RunnerHarness(environment: new Dictionary<string, string> { [RunnerOptions.AgentToolsVariable] = policy });
        var approvalRequested = false;
        h.Service.OnRunApproval = _ =>
        {
            approvalRequested = true;
            return new RunApprovalState { ApprovalId = "apr_unexpected", Status = RunApprovalStatus.Approved };
        };
        var setupMarker = Path.Combine(h.Scratch, "setup-ran.txt");
        var environment = new RunEnvironment
        {
            Checkout = CheckoutMode.Empty, KeepAgentTools = ["playwright"],
            SetupCommand = $"echo ran > \"{setupMarker}\"",
        };
        Assert.True((await h.DeliverAsync(h.Task("dlv_kept", agent, environment: environment) with { RequireRunApproval = true })).Accepted);
        var result = (await h.Service.SettledAsync("dlv_kept")).Result;
        Assert.Equal(SettleStatus.Failed, result.Status);
        var expected = policy == "refuse"
            ? "This computer's owner doesn't let runs keep extra agent tools, and this role's runs keep playwright. Run this role on another computer, or ask the owner to allow it (MBUS_RUNNER_AGENT_TOOLS)."
            : agent == "claude-code"
                ? "This role's runs keep playwright, but Claude Code on this computer doesn't have it set up. Add it to Claude Code's settings here (claude mcp add --scope user), or run this role on another computer."
                : "This role's runs keep playwright, but Codex on this computer doesn't have it set up. Add it to Codex's settings here, or run this role on another computer.";
        Assert.Equal(expected, result.Error);
        Assert.False(approvalRequested);
        Assert.Null(result.ExitCode);
        Assert.Null(result.Usage);
        Assert.False(File.Exists(setupMarker));
    }

    [Fact]
    public async Task A_kept_Claude_Code_tool_reaches_the_run_and_its_settings_leave_the_run_folder_with_the_agent()
    {
        // the original issue: from the runner's home .claude.json; the run's mcp.json holds its token, so it goes even from a kept folder.
        var log = new StringWriter();
        await using var h = new RunnerHarness(log: log);
        Directory.CreateDirectory(h.Home);
        File.WriteAllText(Path.Combine(h.Home, ".claude.json"), """{ "mcpServers": { "playwright": { "command": "npx", "env": { "TOKEN": "s3cret" } } } }""");
        var environment = new RunEnvironment { Checkout = CheckoutMode.Empty, KeepAgentTools = ["playwright"] };
        Assert.True((await h.DeliverAsync(h.Task("dlv_tool", environment: environment))).Accepted);
        var result = (await h.Service.SettledAsync("dlv_tool")).Result;
        await h.Engine.StopAsync();

        Assert.Equal(SettleStatus.Succeeded, result.Status);
        var args = File.ReadAllLines(Path.Combine(h.WorkTreeOf("dlv_tool"), "fake-agent-args.txt"));
        Assert.Contains("mcp__playwright", args[Array.IndexOf(args, "--allowedTools") + 1].Split(','));
        Assert.False(File.Exists(args[Array.IndexOf(args, "--mcp-config") + 1]));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(h.WorkTreeOf("dlv_tool"))!, "prompt.md"))); // the folder itself is kept
        Assert.Contains("Keeps these Claude Code tools: playwright.", log.ToString());
        Assert.DoesNotContain("s3cret", log.ToString());
    }

    [Fact]
    public async Task Codex_windows_mode_reaches_the_launch_saved_prompt_log_and_settle()
    {
        var log = new StringWriter();
        await using var h = new RunnerHarness(log: log, sandbox: _ => new EnabledIsolation());
        var environment = new RunEnvironment
        {
            Checkout = CheckoutMode.Empty,
            SetupCommand = "echo {} > global.json", CodexOnWindows = CodexWindowsSandbox.SameAsOtherAgents,
            PermissionProfile = PermissionProfiles.Defaults(PermissionProfiles.Worker),
        };
        Assert.True((await h.DeliverAsync(h.Task("dlv_windows_mode", "agt_codex", environment: environment))).Accepted);
        var result = (await h.Service.SettledAsync("dlv_windows_mode")).Result;
        Assert.Equal(SettleStatus.Succeeded, result.Status);
        var work = h.WorkTreeOf("dlv_windows_mode");
        var args = File.ReadAllLines(Path.Combine(work, "fake-agent-args.txt"));
        var optedOut = OperatingSystem.IsWindows();
        Assert.Equal(optedOut ? "danger-full-access" : "workspace-write", args[Array.IndexOf(args, "--sandbox") + 1]);
        var prompt = File.ReadAllText(Path.Combine(Path.GetDirectoryName(work)!, "prompt.md"));
        Assert.DoesNotContain(MessageBus.Cli.Runner.Agents.CodexAdapter.DotnetPromptNote, prompt);
        Assert.DoesNotContain(MessageBus.Cli.Runner.Agents.CodexAdapter.DotnetSettleNote, result.Summary!);
        Assert.Equal(OperatingSystem.IsWindows(),
            log.ToString().Contains("Codex on Windows:", StringComparison.Ordinal));
    }

    private sealed class EnabledIsolation : IRunSandbox
    {
        public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile) => launch;
    }

    [Fact]
    public async Task A_sign_in_stored_for_the_runner_service_reaches_the_agent_but_not_the_setup_command()
    {
        // the original issue: mbus runner install stored the token from the installing shell; the service's own environment lacks it.
        const string token = "sk-ant-oat01-stored-for-the-service";
        await using var h = new RunnerHarness(agentEnvironment: new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = token });
        var setup = OperatingSystem.IsWindows() ? "echo %CLAUDE_CODE_OAUTH_TOKEN%> setup-env.txt" : "echo \"$CLAUDE_CODE_OAUTH_TOKEN\" > setup-env.txt";
        var environment = new RunEnvironment { Checkout = CheckoutMode.Empty, SetupCommand = setup };

        Assert.True((await h.DeliverAsync(h.Task("dlv_token", directives: d => d["env"] = "CLAUDE_CODE_OAUTH_TOKEN", environment: environment))).Accepted);
        Assert.Equal(SettleStatus.Succeeded, (await h.Service.SettledAsync("dlv_token")).Result.Status);

        Assert.Equal($"CLAUDE_CODE_OAUTH_TOKEN={token}", File.ReadAllText(Path.Combine(h.WorkTreeOf("dlv_token"), "env.txt")).Trim());
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(h.WorkTreeOf("dlv_token"), "setup-env.txt")));
    }

    [Theory]
    [InlineData(null, null, "fixture-openai")]
    [InlineData("  ", "fixture-stored-codex", "fixture-stored-codex")]
    [InlineData("fixture-explicit-codex", "fixture-stored-codex", "fixture-explicit-codex")]
    public async Task Codex_gets_only_its_selected_key_and_setup_and_mcp_receive_no_key(string? explicitKey, string? storedKey, string expected)
    {
        var session = new Dictionary<string, string> { ["OPENAI_API_KEY"] = "fixture-openai" };
        if (explicitKey is not null) session["CODEX_API_KEY"] = explicitKey;
        var stored = new Dictionary<string, string> { ["OPENAI_API_KEY"] = "fixture-stored-openai" };
        if (storedKey is not null) stored["CODEX_API_KEY"] = storedKey;
        await using var h = new RunnerHarness(environment: session, agentEnvironment: stored);
        var setup = OperatingSystem.IsWindows()
            ? "(if defined CODEX_API_KEY exit /b 9) & (if defined OPENAI_API_KEY exit /b 10) & echo ready> setup.txt"
            : "test -z \"${CODEX_API_KEY+x}${OPENAI_API_KEY+x}\" && echo ready > setup.txt";
        var run = new RunEnvironment { Checkout = CheckoutMode.Empty, SetupCommand = setup };

        Assert.True((await h.DeliverAsync(h.Task("dlv_key_codex", "agt_codex", environment: run,
            directives: d => { d["env"] = "CODEX_API_KEY,OPENAI_API_KEY"; d["settle"] = "mcp-env"; }))).Accepted);
        Assert.Equal(SettleStatus.Succeeded, (await h.Service.SettledAsync("dlv_key_codex")).Result.Status);
        var work = h.WorkTreeOf("dlv_key_codex");
        Assert.Equal("ready", File.ReadAllText(Path.Combine(work, "setup.txt")).Trim());
        Assert.Equal([$"CODEX_API_KEY={expected}", "OPENAI_API_KEY=<unset>"], File.ReadAllLines(Path.Combine(work, "env.txt")));
        var args = File.ReadAllLines(Path.Combine(work, "fake-agent-args.txt"));
        Assert.Contains("shell_environment_policy.set.CODEX_API_KEY=\"\"", args);
        Assert.Contains("mcp_servers.mbus.env_vars=[]", args);
        Assert.DoesNotContain(args, arg => arg.Contains(expected, StringComparison.Ordinal));
        Assert.DoesNotContain("API_KEY", File.ReadAllText(Path.Combine(work, "mcp-env.txt")));

        Assert.True((await h.DeliverAsync(h.Task("dlv_key_other", directives: d => d["env"] = "CODEX_API_KEY,OPENAI_API_KEY"))).Accepted);
        Assert.Equal(SettleStatus.Succeeded, (await h.Service.SettledAsync("dlv_key_other")).Result.Status);
        Assert.Equal(["CODEX_API_KEY=<unset>", "OPENAI_API_KEY=<unset>"], File.ReadAllLines(Path.Combine(h.WorkTreeOf("dlv_key_other"), "env.txt")));
    }

    [Theory]
    [InlineData("codex-missing-auth", "no API credential reached the provider", false)]
    [InlineData("codex-ineligible", "Codex-eligible sign-in or API project", false)]
    [InlineData("codex-missing-auth", "no API credential reached the provider", true)]
    [InlineData("codex-ineligible", "Codex-eligible sign-in or API project", true)]
    public async Task A_Codex_provider_refusal_is_actionable_and_never_echoes_the_key(string mode, string advice, bool wrapped)
    {
        const string key = "fixture-provider-refusal";
        var log = new StringWriter();
        await using var h = new RunnerHarness(environment: new Dictionary<string, string> { ["OPENAI_API_KEY"] = key }, log: log,
            sandbox: wrapped ? _ => new FileEnvironmentSandbox(key) : null,
            extensions: e => e with { Readiness = new RunnerReadiness(log) });
        var run = new RunEnvironment { Checkout = CheckoutMode.Empty, Logs = LogCapture.Local };
        Assert.True((await h.DeliverAsync(h.Task("dlv_codex_refusal", "agt_codex", environment: run, directives: d => d["mode"] = mode))).Accepted);
        var settle = await h.Service.SettledAsync("dlv_codex_refusal");
        await h.Engine.StopAsync();

        Assert.True(settle.Declined == DeclineReason.MachineNotReady, settle.Result.Error ?? settle.Result.Summary);
        Assert.Equal(SettleStatus.Abandoned, settle.Result.Status);
        Assert.Contains(advice, settle.Result.Error);
        Assert.DoesNotContain(key, settle.Result.Error);
        Assert.DoesNotContain(key, log.ToString());
        var logFile = Path.Combine(Path.GetDirectoryName(h.WorkTreeOf("dlv_codex_refusal"))!, "agent.log");
        Assert.Contains("[redacted]", File.ReadAllText(logFile));
        Assert.DoesNotContain(key, File.ReadAllText(logFile));
    }

    [Fact]
    public async Task Two_tasks_run_at_once_in_separate_worktrees_and_settle_with_usage()
    {
        await using var h = new RunnerHarness(slots: 2);
        var gate = Path.Combine(h.Scratch, "release");

        // dlv_a names a model, as the service's claim does for a task that asked for one (the original issue, the original issue).
        var claude = await h.DeliverAsync(h.Task("dlv_a", "claude-code", directives: d => { d["wait"] = gate; d["settle"] = "yes"; }) with { Model = "claude-opus-5-5" });
        var codex = await h.DeliverAsync(h.Task("dlv_b", "agt_codex", directives: d => d["wait"] = gate));
        var third = await h.DeliverAsync(h.Task("dlv_c"));

        Assert.True(claude.Accepted);
        Assert.True(codex.Accepted);
        Assert.False(third.Accepted);
        Assert.Equal(DeclineReason.NoFreeSlot, third.Reason);

        var status = MessageBusJson.Deserialize<RunnerStatus>((await h.Routes.HandleAsync("GET", RunnerRoutes.Status, null, TestContext.Current.CancellationToken)).Json!)!;
        Assert.Equal(["dlv_a", "dlv_b"], status.ActiveDeliveryIds.Order());

        File.WriteAllText(gate, "");
        var a = (await h.Service.SettledAsync("dlv_a")).Result;
        var b = (await h.Service.SettledAsync("dlv_b")).Result;

        // Each agent ran in its own worktree (it writes its delivery id into its working directory).
        Assert.Equal("dlv_a", File.ReadAllText(Path.Combine(h.WorkTreeOf("dlv_a"), "fake-agent.txt")));
        Assert.Equal("dlv_b", File.ReadAllText(Path.Combine(h.WorkTreeOf("dlv_b"), "fake-agent.txt")));
        // The claim's model reaches Claude Code's command line.
        var claudeArgs = File.ReadAllLines(Path.Combine(h.WorkTreeOf("dlv_a"), "fake-agent-args.txt"));
        Assert.Equal("claude-opus-5-5", claudeArgs[Array.IndexOf(claudeArgs, "--model") + 1]);

        // Claude: settled through its settle call; usage from claude's result JSON.
        Assert.Equal(SettleStatus.Succeeded, a.Status);
        Assert.Equal(0, a.ExitCode);
        Assert.StartsWith("Worked in ", a.Summary);
        Assert.Equal("0123abc", Assert.Single(a.Refs).Sha);
        Assert.Equal(AgentKind.ClaudeCode, a.Usage!.AgentKind);
        Assert.Equal("claude-sonnet-4-5", a.Usage.Model);
        Assert.Equal(12 + 2000 + 500, a.Usage.TokensIn);
        Assert.Equal(300, a.Usage.TokensOut);
        Assert.Equal(0.0421m, a.Usage.EstimatedCostUsd);
        Assert.Equal(4.2, a.Usage.ActiveSeconds);

        // Codex: usage from codex's JSONL.
        Assert.Equal(SettleStatus.Succeeded, b.Status);
        Assert.Equal(AgentKind.Codex, b.Usage!.AgentKind);
        Assert.Equal("openai", b.Usage.Vendor);
        Assert.Equal(24763, b.Usage.TokensIn);
        Assert.Equal(122, b.Usage.TokensOut);
    }

    [Fact]
    public async Task Codex_gives_the_mbus_mcp_server_the_runs_variables_so_its_settle_reaches_the_runner()
    {
        // Codex starts MCP servers without the agent's environment; the fake codex settles only through what its
        // `-c mcp_servers.mbus.env.*` arguments give the server (see FakeAgent).
        await using var h = new RunnerHarness();
        var codexHome = Directory.CreateDirectory(Path.Combine(h.Home, ".codex")).FullName;
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), "model = \"gpt-5\"\n\n[mcp_servers.mbus]\ncommand = \"mbus\"\nargs = [\"mcp\"]\n");

        await h.DeliverAsync(h.Task("dlv_codex", "agt_codex", directives: d => d["settle"] = "mcp-env"));
        var settled = (await h.Service.SettledAsync("dlv_codex")).Result;

        Assert.StartsWith("Worked in ", settled.Summary);
        Assert.Equal("0123abc", Assert.Single(settled.Refs).Sha);
        var env = File.ReadAllLines(Path.Combine(h.WorkTreeOf("dlv_codex"), "mcp-env.txt")).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal("dlv_codex", env["MBUS_DELIVERY_ID"]);
        Assert.Equal("agt_codex", env["MBUS_AGENT"]);
        Assert.Equal("developer", env["MBUS_ROLE"]);
        Assert.Contains("\"dlv_codex\"", File.ReadAllText(env["MBUS_DELIVERY_FILE"]));
        // Codex's own settings say how to start the server, so the runner doesn't.
        Assert.Empty(File.ReadAllLines(Path.Combine(h.WorkTreeOf("dlv_codex"), "mcp-server.txt")));

        // the original issue: without the mbus server in Codex's settings (no mbus connect) the runner defines all of it: env alone,
        // for a server with no command, would stop Codex from starting.
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), "model = \"gpt-5\"\n");
        await h.DeliverAsync(h.Task("dlv_plain", "agt_codex", directives: d => d["settle"] = "mcp-env"));
        var plain = (await h.Service.SettledAsync("dlv_plain")).Result;
        Assert.Equal(SettleStatus.Succeeded, plain.Status);
        Assert.Equal("0123abc", Assert.Single(plain.Refs).Sha);
        var mbus = MessageBus.Cli.Mcp.McpLaunchCommand.ForCurrentProcess();
        Assert.Equal([$"command={mbus.Command}", $"args={string.Join(" ", mbus.Args)}"],
            File.ReadAllLines(Path.Combine(h.WorkTreeOf("dlv_plain"), "mcp-server.txt")));
        Assert.Contains("MBUS_DELIVERY_ID=dlv_plain", File.ReadAllLines(Path.Combine(h.WorkTreeOf("dlv_plain"), "mcp-env.txt")));
    }

    [Fact]
    public async Task Cancel_kills_the_agent_and_its_children_within_five_seconds()
    {
        // the original issue: a cancelled run never sends an answer the agent tried to settle with earlier.
        var keptSent = 0;
        await using var h = new RunnerHarness(extensions: e => e with
        {
            SendKeptAnswer = (_, _, _) =>
            {
                Interlocked.Increment(ref keptSent);
                return Task.FromResult(true);
            },
        });
        Assert.True((await h.DeliverAsync(h.Task("dlv_cancel", directives: d => d["mode"] = "hang"))).Accepted);
        await h.Service.AgentProgressAsync("dlv_cancel");
        var pids = File.ReadAllText(Path.Combine(h.WorkTreeOf("dlv_cancel"), "pids.txt")).Split(' ').Select(int.Parse).ToArray();

        var clock = Stopwatch.StartNew();
        var body = MessageBusJson.Serialize(new CancelDelivery { DeliveryId = "dlv_cancel", ThreadId = "thr_dlv_cancel", Reason = "Not needed any more." });
        Assert.Equal(202, (await h.Routes.HandleAsync("POST", RunnerRoutes.Cancel, body, TestContext.Current.CancellationToken)).StatusCode);
        var settle = await h.Service.SettledAsync("dlv_cancel");
        foreach (var pid in pids)
        {
            AssertExits(pid);
        }
        clock.Stop();

        Assert.Equal(SettleStatus.Cancelled, settle.Result.Status);
        Assert.Equal("Not needed any more.", settle.Result.Error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Took {clock.Elapsed}.");
        Assert.Equal(0, keptSent);
    }

    [Fact]
    public async Task Heartbeat_reports_the_running_task_and_a_cancel_in_the_answer_stops_it()
    {
        await using var h = new RunnerHarness();
        h.Service.OnHeartbeat = beat => new HeartbeatAck
        {
            Leases = [.. beat.ActiveDeliveryIds.Select(id => new LeaseState { DeliveryId = id, LeaseExpiresAt = DateTimeOffset.MaxValue, CancelRequested = true })],
        };
        Assert.True((await h.DeliverAsync(h.Task("dlv_hb", directives: d => d["mode"] = "hang"))).Accepted);
        await h.Service.AgentProgressAsync("dlv_hb");

        h.Time.Advance(TimeSpan.FromSeconds(30));
        var settle = await h.Service.SettledAsync("dlv_hb");

        var beat = Assert.Single(h.Service.Heartbeats);
        Assert.Equal(["dlv_hb"], beat.ActiveDeliveryIds);
        Assert.Equal(2, beat.Slots);
        Assert.Equal(1, beat.SlotsBusy);
        Assert.Equal(SettleStatus.Cancelled, settle.Result.Status);
    }

    [Fact]
    public async Task A_run_that_goes_quiet_reports_every_minute_that_it_is_still_running_and_what_it_was_doing()
    {
        // The fake agent reports "Building the solution", then prints nothing more (a long build, the original issue).
        await using var h = new RunnerHarness();
        Assert.True((await h.DeliverAsync(h.Task("dlv_quiet", directives: d => d["mode"] = "hang"))).Accepted);
        await h.Service.AgentProgressAsync("dlv_quiet");

        h.Time.Advance(TimeSpan.FromSeconds(60));
        var first = await NextProgressAsync(h, p => p.Progress.Message.StartsWith("Still", StringComparison.Ordinal));
        h.Time.Advance(TimeSpan.FromSeconds(60));
        var second = await NextProgressAsync(h, p => p.Progress.Message.StartsWith("Still", StringComparison.Ordinal));

        Assert.Equal("Still running (1 min): Building the solution", first.Progress.Message);
        Assert.Equal("Still running (2 min): Building the solution", second.Progress.Message);
    }

    private static async Task<RunnerProgress> NextProgressAsync(RunnerHarness h, Func<RunnerProgress, bool> match)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var progress = await h.Service.Progress.Reader.ReadAsync(timeout.Token);
            if (match(progress)) return progress;
        }
    }

    [Fact]
    public async Task An_idle_runner_sends_a_heartbeat_on_its_default_interval()
    {
        // The service keeps an idle runner's agents online and its slots current from these.
        await using var h = new RunnerHarness();

        h.Time.Advance(TimeSpan.FromSeconds(60));
        var beat = await h.Service.NextHeartbeatAsync();

        Assert.Empty(beat.ActiveDeliveryIds);
        Assert.Equal(2, beat.Slots);
        Assert.Equal(0, beat.SlotsBusy);
    }

    [Fact]
    public async Task A_run_past_the_roles_time_limit_is_stopped_and_returns_to_the_queue()
    {
        await using var h = new RunnerHarness();
        var environment = new RunEnvironment { Checkout = CheckoutMode.Empty, TimeoutSeconds = 600 };
        Assert.True((await h.DeliverAsync(h.Task("dlv_slow", directives: d => d["mode"] = "hang", environment: environment))).Accepted);
        await h.Service.AgentProgressAsync("dlv_slow");

        h.Time.Advance(TimeSpan.FromSeconds(600));
        var settle = await h.Service.SettledAsync("dlv_slow");

        Assert.Equal(SettleStatus.Abandoned, settle.Result.Status);
        Assert.Contains("600 seconds", settle.Result.Error);
    }

    [Fact]
    public async Task A_run_that_needs_approval_starts_its_agent_only_once_the_owner_approves_and_its_time_limit_starts_then()
    {
        await using var h = new RunnerHarness();
        var answer = RunApprovalStatus.Pending;
        h.Service.OnRunApproval = _ => new RunApprovalState
        {
            ApprovalId = "apr_1", Status = answer, ApproverName = "Ada",
            Detail = answer == RunApprovalStatus.Approved ? "Ada approved running it on test-machine." : null,
        };
        var environment = new RunEnvironment { Checkout = CheckoutMode.Empty, TimeoutSeconds = 600 };
        var task = h.Task("dlv_ask", environment: environment) with { RequireRunApproval = true };
        Assert.True((await h.DeliverAsync(task)).Accepted);

        await NextProgressAsync(h, p => p.Progress.Message == "Waiting for Ada to approve the run");
        h.Time.Advance(TimeSpan.FromSeconds(600)); // waiting doesn't use up the role's time limit
        Assert.False(Directory.Exists(h.WorkTreeOf("dlv_ask"))); // nothing checked out, no agent started
        answer = RunApprovalStatus.Approved;
        h.Time.Advance(new RunnerOptions { WorkRoot = "" }.ApprovalPollInterval);

        var settle = await h.Service.SettledAsync("dlv_ask");
        Assert.Equal(SettleStatus.Succeeded, settle.Result.Status);
        Assert.Null(settle.Declined);
        Assert.Equal("dlv_ask", File.ReadAllText(Path.Combine(h.WorkTreeOf("dlv_ask"), "fake-agent.txt")));
    }

    [Fact]
    public async Task A_run_its_owner_declines_never_starts_and_is_handed_back_with_the_reason()
    {
        await using var h = new RunnerHarness();
        h.Service.OnRunApproval = _ => new RunApprovalState
        {
            ApprovalId = "apr_2", Status = RunApprovalStatus.Declined, Detail = "Ada declined to run it on test-machine; it waits for another machine.",
        };
        Assert.True((await h.DeliverAsync(h.Task("dlv_no") with { RequireRunApproval = true })).Accepted);

        var settle = await h.Service.SettledAsync("dlv_no");
        Assert.Equal(DeclineReason.NotApproved, settle.Declined);
        Assert.Equal(SettleStatus.Abandoned, settle.Result.Status);
        Assert.Equal("Ada declined to run it on test-machine; it waits for another machine.", settle.Result.Error);
        Assert.False(Directory.Exists(h.WorkTreeOf("dlv_no")));
    }

    [Fact]
    public async Task A_run_whose_reported_usage_passes_the_roles_most_cost_per_run_is_stopped_and_fails_counting_what_it_used()
    {
        // Claude Code repeats a message's usage on each of its content blocks: msg_a counts once. 4,000 + 3,000 output
        // tokens at $15 a million is $0.105, past the $0.10 cap only with msg_b; the agent would otherwise never finish.
        await using var h = new RunnerHarness();
        var task = h.Task("dlv_cap", directives: d => { d["calls"] = "msg_a:4000,msg_a:4000,msg_b:3000"; d["mode"] = "hang"; }) with
        {
            CostCap = new RunCostCap
            {
                MaxUsd = 0.10m,
                Prices = new Dictionary<string, TokenPrice> { ["claude-sonnet-4-5"] = new() { InputUsdPerMillion = 3, OutputUsdPerMillion = 15 } },
            },
        };
        Assert.True((await h.DeliverAsync(task)).Accepted);

        var settle = await h.Service.SettledAsync("dlv_cap");
        Assert.Equal(SettleStatus.Failed, settle.Result.Status);
        Assert.Equal("Stopped at an estimated $0.11, over the role's most cost per run of $0.10.", settle.Result.Error);
        Assert.Equal(7000, settle.Result.Usage!.TokensOut); // what it used goes toward the role's budget
    }

    [Fact]
    public async Task A_managed_drain_preserves_active_work_and_only_its_owner_can_resume_it()
    {
        await using var h = new RunnerHarness();
        Assert.True((await h.DeliverAsync(h.Task("dlv_active", directives: d => d["mode"] = "hang"))).Accepted);
        await h.Service.AgentProgressAsync("dlv_active");
        var drain = MessageBusJson.Serialize(new DrainRequest
        {
            ManagedDrainId = "aci-operation-one", Deadline = h.Time.GetUtcNow()
        });
        Assert.Equal(202, (await h.Routes.HandleAsync("POST", RunnerRoutes.Drain, drain, TestContext.Current.CancellationToken)).StatusCode);
        Assert.True(h.Engine.GetStatus().SupportsManagedDrain);
        Assert.Contains("dlv_active", h.Engine.GetStatus().ActiveDeliveryIds);
        Assert.Equal(DeclineReason.Draining, (await h.DeliverAsync(h.Task("dlv_held"))).Reason);
        h.Engine.Drain(new DrainRequest { ManagedDrainId = "aci-operation-two" });
        h.Engine.Drain(new DrainRequest { ManagedDrainId = "aci-operation-two", Resume = true });
        Assert.Equal("aci-operation-one", h.Engine.GetStatus().ManagedDrainId);
        var resume = MessageBusJson.Serialize(new DrainRequest { ManagedDrainId = "aci-operation-one", Resume = true });
        await h.Routes.HandleAsync("POST", RunnerRoutes.Drain, resume, TestContext.Current.CancellationToken);
        Assert.False(h.Engine.GetStatus().Draining);
        Assert.True((await h.DeliverAsync(h.Task("dlv_resumed"))).Accepted);
        await h.Service.SettledAsync("dlv_resumed");
        Assert.Contains("dlv_active", h.Engine.GetStatus().ActiveDeliveryIds);
    }

    [Fact]
    public async Task Resuming_a_managed_drain_preserves_a_manual_drain()
    {
        await using var h = new RunnerHarness();
        h.Engine.Drain(new DrainRequest { ManagedDrainId = "aci-idle-stop" });
        h.Engine.Drain(new DrainRequest { Reason = "owner paused work" });
        h.Engine.Drain(new DrainRequest { ManagedDrainId = "aci-idle-stop", Resume = true });
        Assert.Null(h.Engine.GetStatus().ManagedDrainId);
        Assert.Equal(DeclineReason.Draining, (await h.DeliverAsync(h.Task("dlv_still_paused"))).Reason);
    }

    [Fact]
    public async Task Draining_refuses_new_work_and_stops_what_is_left_at_the_deadline()
    {
        await using var h = new RunnerHarness();
        Assert.True((await h.DeliverAsync(h.Task("dlv_left", directives: d => d["mode"] = "hang"))).Accepted);
        await h.Service.AgentProgressAsync("dlv_left");

        var drain = MessageBusJson.Serialize(new DrainRequest { Reason = "update", Deadline = h.Time.GetUtcNow().AddMinutes(10) });
        Assert.Equal(202, (await h.Routes.HandleAsync("POST", RunnerRoutes.Drain, drain, TestContext.Current.CancellationToken)).StatusCode);
        var refused = await h.DeliverAsync(h.Task("dlv_new"));
        h.Time.Advance(TimeSpan.FromMinutes(10));
        var settle = await h.Service.SettledAsync("dlv_left");

        Assert.Equal(DeclineReason.Draining, refused.Reason);
        Assert.Equal(SettleStatus.Abandoned, settle.Result.Status);
    }

    [Theory]
    [InlineData("claim", 2)]          // says "the task succeeded" but exits 2
    [InlineData("settle-failed", 0)]  // exits 0 but its settle call says failed
    public async Task The_result_comes_from_the_exit_code_and_the_settle_call_not_from_what_the_agent_wrote(string mode, int exitCode)
    {
        await using var h = new RunnerHarness();
        Assert.True((await h.DeliverAsync(h.Task("dlv_" + mode, directives: d => d["mode"] = mode))).Accepted);

        var settle = await h.Service.SettledAsync("dlv_" + mode);

        Assert.Equal(SettleStatus.Failed, settle.Result.Status);
        Assert.Equal(exitCode, settle.Result.ExitCode);
    }

    [Fact]
    public async Task An_agent_that_exits_cleanly_without_its_settle_reaching_the_runner_fails_and_its_worktree_is_kept_for_a_day()
    {
        // As when the agent's settle call failed (the service's /mcp answered 404 in the first two-machine run, the original issue):
        // exit 0, a final message claiming success, no settle record. The worktree is the only copy of its work, so it is
        // held for 24 hours (the original issue), then housekeeping deletes it like any other.
        // the original issue: unless its settle was refused for an attachment and its answer kept: then the runner sends that one.
        var log = new StringWriter();
        await using var h = new RunnerHarness(log: log, keepRunFolders: false, extensions: e => e with
        {
            SendKeptAnswer = async (run, delivery, ct) =>
            {
                if (delivery.DeliveryId != "dlv_kept") return false;
                await File.WriteAllTextAsync(run.SettleFile, MessageBusJson.Serialize(new SettleInput
                {
                    ThreadId = delivery.Envelope.ThreadId, DeliveryId = delivery.DeliveryId,
                    Result = new SettleResult { Status = SettleStatus.Succeeded, Summary = "Kept answer.", Refs = [new Ref { Kind = "attachment.refused", Path = "/elsewhere" }] },
                }), ct);
                return true;
            },
        });
        var environment = new RunEnvironment { Checkout = CheckoutMode.Empty };
        Assert.True((await h.DeliverAsync(h.Task("dlv_lost", directives: d => d["settle"] = "no", environment: environment))).Accepted);
        Assert.True((await h.DeliverAsync(h.Task("dlv_kept", directives: d => d["settle"] = "no", environment: environment))).Accepted);

        var settle = (await h.Service.SettledAsync("dlv_lost")).Result;
        var kept = (await h.Service.SettledAsync("dlv_kept")).Result;
        await h.Engine.StopAsync(); // the run has finished, cleanup included

        Assert.Equal(SettleStatus.Succeeded, kept.Status);
        Assert.Equal("Kept answer.", kept.Summary);
        Assert.Equal("/elsewhere", Assert.Single(kept.Refs).Path);

        Assert.Equal(SettleStatus.Failed, settle.Status);
        Assert.Equal(0, settle.ExitCode);
        Assert.StartsWith("Agent result not received", settle.Error);
        Assert.Contains(h.WorkTreeOf("dlv_lost"), settle.Error);
        Assert.True(File.Exists(Path.Combine(h.WorkTreeOf("dlv_lost"), "fake-agent.txt")));
        Assert.False(Directory.Exists(h.WorkTreeOf("dlv_kept")));
        Assert.Contains($"kept in {Path.GetDirectoryName(h.WorkTreeOf("dlv_lost"))} until", log.ToString());

        Assert.Equal(new RunWorkspace.SweepResult(0, 0), RunWorkspace.Sweep(h.WorkRoot, h.Time.GetUtcNow() + TimeSpan.FromHours(23)));
        Assert.True(Directory.Exists(h.WorkTreeOf("dlv_lost")));
        Assert.Equal(new RunWorkspace.SweepResult(1, 0), RunWorkspace.Sweep(h.WorkRoot, h.Time.GetUtcNow() + RunWorkspace.HoldFor));
        Assert.False(Directory.Exists(h.WorkTreeOf("dlv_lost")));
    }

    [Fact]
    public async Task A_task_from_a_project_the_machine_does_not_accept_is_refused_and_nothing_runs()
    {
        var ownProjects = new RunnerPolicy
        {
            OwnerSubjectId = RunnerHarness.Owner,
            AcceptsWorkFrom = new AcceptsWorkFrom { Mode = AcceptsWorkFromMode.OwnProjects },
        };
        await using var h = new RunnerHarness(policy: ownProjects);

        var theirs = await h.DeliverAsync(h.Task("dlv_theirs", projectOwner: "microsoft:someone-else"));
        var mine = await h.DeliverAsync(h.Task("dlv_mine"));
        var settled = await h.Service.SettledAsync("dlv_mine");

        Assert.False(theirs.Accepted);
        Assert.Equal(DeclineReason.NotAccepted, theirs.Reason);
        Assert.False(Directory.Exists(Path.GetDirectoryName(h.WorkTreeOf("dlv_theirs"))));
        Assert.True(mine.Accepted);
        Assert.Equal(SettleStatus.Succeeded, settled.Result.Status);
        Assert.DoesNotContain(h.Service.Settles, s => s.DeliveryId == "dlv_theirs");
    }

    [Fact]
    public async Task A_variable_set_on_the_machine_overrides_that_part_of_the_work_policy_the_service_sends()
    {
        // the original issue: a container that says whose work it runs keeps that, whatever the dashboard says; the rest is the service's.
        await using var h = new RunnerHarness(environment: new Dictionary<string, string> { [RunnerEnvVars.AcceptsWorkFrom] = "workspace" });
        var sent = new RunnerWorkPolicy
        {
            OwnerSubjectId = RunnerHarness.Owner,
            AcceptsWorkFrom = new AcceptsWorkFrom { Mode = AcceptsWorkFromMode.OwnProjects },
            DailySpendCeilingUsd = 3m,
        };

        var answer = await h.Routes.HandleAsync("POST", RunnerRoutes.Policy, MessageBusJson.Serialize(sent), CancellationToken.None);

        Assert.Equal(202, answer.StatusCode);
        Assert.Equal(AcceptsWorkFromMode.Workspace, h.Engine.Policy.AcceptsWorkFrom.Mode);
        Assert.Equal(3m, h.Engine.Policy.DailySpendCeilingUsd);
        Assert.Equal(RunnerHarness.Owner, h.Engine.Policy.OwnerSubjectId);
    }

    [Fact]
    public async Task A_bundle_task_runs_in_a_clone_of_the_bundle()
    {
        await WindowsRunCapabilities.RequireGitShellAsync();
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        var source = new FileBundleSource();
        await using var h = new RunnerHarness(bundles: source);
        source.File = MakeBundle(git!, h.Scratch);
        var bundle = new AttachmentRef { Id = "att_1", Name = "work.bundle", Format = AttachmentFormat.GitBundle, SizeBytes = 1, Sha256 = "00", Branch = "main" };
        var environment = new RunEnvironment { Checkout = CheckoutMode.Bundle };

        Assert.True((await h.DeliverAsync(h.Task("dlv_bundle", environment: environment, attachments: [bundle]))).Accepted);
        var settle = await h.Service.SettledAsync("dlv_bundle");

        Assert.True(settle.Result.Status == SettleStatus.Succeeded, settle.Result.Error);
        Assert.Equal("from the bundle", File.ReadAllText(Path.Combine(h.WorkTreeOf("dlv_bundle"), "README.md")));
    }

    [Fact]
    public async Task Clone_mode_starts_each_run_from_the_machines_mirror_of_the_repository_and_keeps_the_mirror_up_to_date()
    {
        await WindowsRunCapabilities.RequireGitShellAsync();
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        await using var h = new RunnerHarness();
        var source = Directory.CreateDirectory(Path.Combine(h.Scratch, "source")).FullName;
        Git(git!, source, "init", "--quiet", "--initial-branch=main");
        var first = Commit(git!, source, "first version");
        var origin = Path.Combine(h.Scratch, "origin.git");
        Git(git!, h.Scratch, "clone", "--quiet", "--bare", source, origin);
        var task = h.Task("dlv_clone", environment: new RunEnvironment { Checkout = CheckoutMode.Clone });
        task = task with { Envelope = task.Envelope with { Refs = [new Ref { Kind = "url", Url = origin }] } };

        using var run1 = RunWorkspace.Create(h.WorkRoot, "dlv_1", 1);
        Assert.Null(await run1.CheckOutAsync(task, _ => git, null, TestContext.Current.CancellationToken));
        Assert.True(RepoMirrors.For(h.WorkRoot).Has(origin));

        // The remote's history is replaced: a fresh clone could no longer check out the first commit.
        Git(git!, source, "checkout", "--quiet", "--orphan", "rewrite");
        var second = Commit(git!, source, "second version");
        // The remote fetches it rather than being pushed to, so a run's no-push git settings around these tests don't matter (the original issue).
        Git(git!, origin, "fetch", "--quiet", source, "+rewrite:main");
        Git(git!, origin, "reflog", "expire", "--expire=now", "--all");
        Git(git!, origin, "gc", "--quiet", "--prune=now");

        // The next run copies the mirror (which still has it), fetches what is new from the real remote, and works on its own copy.
        using var run2 = RunWorkspace.Create(h.WorkRoot, "dlv_2", 1);
        var pinned = task with { Envelope = task.Envelope with { Refs = [new Ref { Kind = "url", Url = origin, Sha = first }] } };
        Assert.Null(await run2.CheckOutAsync(pinned, _ => git, null, TestContext.Current.CancellationToken));
        Assert.Equal("first version", File.ReadAllText(Path.Combine(run2.WorkTree, "README.md")));
        Assert.Equal(origin, GitOutput(git!, run2.WorkTree, "remote", "get-url", "origin"));
        Assert.Equal(second, GitOutput(git!, run2.WorkTree, "rev-parse", "refs/remotes/origin/main"));
        Assert.False(File.Exists(Path.Combine(run2.WorkTree, ".git", "objects", "info", "alternates")));
        Assert.Equal(second, GitOutput(git!, RepoMirrors.For(h.WorkRoot).PathFor(origin), "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task Housekeeping_removes_repository_mirrors_no_run_has_used_for_a_month_and_keeps_the_rest()
    {
        // the original issue: mirrors of repositories nobody works on any more don't pile up in the work folder.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-tidy-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var mirrors = RepoMirrors.For(work, time);
            string Mirror(string url, TimeSpan unused)
            {
                var path = Directory.CreateDirectory(mirrors.PathFor(url)).FullName;
                RepoMirrors.MarkUsed(path, time.GetUtcNow() - unused);
                return path;
            }
            var recent = Mirror("https://github.com/acme/app", TimeSpan.FromDays(2));
            var ageing = Mirror("https://github.com/acme/old", TimeSpan.FromDays(29.5));
            var stale = Mirror("https://github.com/acme/gone", TimeSpan.FromDays(45));

            await using var housekeeping = RunnerHousekeeping.Start(work, git: null, _ => null, TextWriter.Null, time);
            await housekeeping.Current;
            Assert.False(Directory.Exists(stale));
            Assert.True(Directory.Exists(ageing));

            // The next day's pass finds the second one unused for over a month too.
            time.Advance(RunnerHousekeeping.Interval);
            await housekeeping.Current;
            Assert.False(Directory.Exists(ageing));
            Assert.True(Directory.Exists(recent));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Housekeeping_deletes_what_finished_runs_left_and_their_logs_after_14_days_and_never_a_run_in_progress_a_held_run_or_anything_else()
    {
        // the original issue: a runner that crashed or restarted mid-run, or couldn't delete a file, leaves the run's folder (the decrypted
        // code bundle included). The first pass at start and the daily one delete it, keeping only its log.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-sweep-" + Guid.NewGuid().ToString("N"))).FullName;
        var outside = Directory.CreateDirectory(work + "-outside").FullName;
        static string RunFile(string folder, string name)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, name);
            return path;
        }
        try
        {
            using var running = RunWorkspace.Create(work, "dlv_running", 1);
            RunFile(running.Root, "task.bundle");
            var crashed = RunWorkspace.Create(work, "dlv_crashed", 1);
            RunFile(Directory.CreateDirectory(crashed.WorkTree).FullName, "README.md");
            RunFile(crashed.Root, "task.bundle");
            RunFile(crashed.Root, "gitconfig");
            var log = RunFile(crashed.Root, "agent.log");
            crashed.Dispose(); // its runner is gone
            var held = RunWorkspace.Create(work, "dlv_held", 1);
            held.Hold(time.GetUtcNow() + TimeSpan.FromHours(12));
            held.Dispose();
            // Another runner's run in progress in the same work folder: it holds its lock file.
            var other = Directory.CreateDirectory(Path.Combine(work, "dlv_other-1")).FullName;
            RunFile(other, "delivery.json");
            using var otherLock = new FileStream(Path.Combine(other, RunWorkspace.LockFileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            // From a runner before the lock file: deleted once untouched for a day.
            var legacy = Directory.CreateDirectory(Path.Combine(work, "dlv_legacy-1")).FullName;
            RunFile(legacy, "delivery.json");
            Directory.SetLastWriteTimeUtc(legacy, time.GetUtcNow().UtcDateTime.AddDays(-2));
            // Not runs: someone's folder, and a link named like a run out of the work folder.
            var notes = RunFile(Directory.CreateDirectory(Path.Combine(work, "notes")).FullName, "notes.txt");
            var target = RunFile(outside, "delivery.json");
            var link = Path.Combine(work, "dlv_link-1");
            if (OperatingSystem.IsWindows())
            {
                using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, outside]) { RedirectStandardOutput = true, UseShellExecute = false })!;
                mklink.WaitForExit();
            }
            else
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            var mirror = Directory.CreateDirectory(RepoMirrors.For(work, time).PathFor("https://github.com/acme/app")).FullName;
            RepoMirrors.MarkUsed(mirror, time.GetUtcNow());
            // the original issue: what a run of a role that keeps local logs leaves; the log goes once it is 14 days old.
            string LogOnly(string name, TimeSpan age)
            {
                var folder = Directory.CreateDirectory(Path.Combine(work, name)).FullName;
                File.SetLastWriteTimeUtc(RunFile(folder, "agent.log"), (time.GetUtcNow() - age).UtcDateTime);
                Directory.SetLastWriteTimeUtc(folder, (time.GetUtcNow() - age).UtcDateTime);
                return folder;
            }
            var oldLog = LogOnly("dlv_oldlog-1", TimeSpan.FromDays(15));
            var newerLog = LogOnly("dlv_newerlog-1", TimeSpan.FromDays(13.5));

            await using var housekeeping = RunnerHousekeeping.Start(work, git: null, _ => null, TextWriter.Null, time);
            await housekeeping.Current;

            Assert.False(Directory.Exists(oldLog));
            Assert.True(File.Exists(Path.Combine(newerLog, "agent.log")));
            Assert.Equal([log], Directory.GetFileSystemEntries(crashed.Root));
            Assert.False(Directory.Exists(legacy));
            Assert.True(File.Exists(Path.Combine(running.Root, "task.bundle")));
            Assert.True(File.Exists(Path.Combine(other, "delivery.json")));
            Assert.True(Directory.Exists(held.Root));
            Assert.True(File.Exists(notes));
            Assert.True(File.Exists(target));
            Assert.True(Directory.Exists(mirror));

            // The next day's pass: the hold is over, and the other log is past 14 days too.
            time.Advance(RunnerHousekeeping.Interval);
            await housekeeping.Current;
            Assert.False(Directory.Exists(held.Root));
            Assert.False(Directory.Exists(newerLog));
            Assert.True(File.Exists(Path.Combine(running.Root, "task.bundle")));

            // A runner told 0 days keeps logs however old they are.
            File.SetLastWriteTimeUtc(log, time.GetUtcNow().UtcDateTime.AddDays(-100));
            Directory.SetLastWriteTimeUtc(crashed.Root, time.GetUtcNow().UtcDateTime.AddDays(-100));
            await using (var keeping = RunnerHousekeeping.Start(work, git: null, v => v == RunnerHousekeeping.LogDaysVariable ? "0" : null, TextWriter.Null, time))
            {
                await keeping.Current;
            }
            Assert.True(File.Exists(log));
        }
        finally
        {
            RunWorkspace.TryDelete(work);
            RunWorkspace.TryDelete(outside);
        }
    }

    [Fact]
    public async Task A_message_to_a_closed_sessions_inbox_resumes_that_session_in_its_folder_and_only_its_owners_closed_session()
    {
        // the original issue: an orchestrator session (Claude Code, its log in ~/.claude/projects/<folder>) opened inbox inb_1 through
        // the machine's mbus tools, which recorded it.
        var owner = new RunnerPolicy { OwnerSubjectId = RunnerHarness.Owner, AcceptsWorkFrom = new AcceptsWorkFrom { Mode = AcceptsWorkFromMode.OwnProjects } };
        await using var h = new RunnerHarness(policy: owner, keepRunFolders: false);
        var folder = Directory.CreateDirectory(Path.Combine(h.Scratch, "my-repo")).FullName;
        File.WriteAllText(Path.Combine(folder, "README.md"), "the person's own work");
        var log = Directory.CreateDirectory(Path.Combine(h.Home, ".claude", "projects",
            System.Text.RegularExpressions.Regex.Replace(Path.GetFullPath(folder), "[^A-Za-z0-9]", "-"))).FullName;
        File.WriteAllText(Path.Combine(log, "sess-1.jsonl"), "{}\n");
        var session = new MessageBus.Mcp.SessionRecord
        {
            InboxId = "inb_1", WorkspaceId = "ws_test", AgentId = "claude-code", AgentKind = "claude-code", Folder = folder, SessionId = "sess-1",
            CreatedAt = h.Time.GetUtcNow(), ExpiresAt = h.Time.GetUtcNow().AddHours(24),
        };
        // The answer, from another person's agent and in no project: still the session owner's work, which this machine takes.
        TaskDelivery Reply(string id, string inboxOwner = RunnerHarness.Owner)
        {
            var task = h.Task(id, projectOwner: null, environment: new RunEnvironment { Logs = LogCapture.Local });
            return task with
            {
                Role = null, Instructions = null, InboxOwner = inboxOwner,
                Envelope = task.Envelope with
                {
                    Target = new InboxTarget { InboxId = "inb_1" }, Type = "task.implement.reply",
                    Sender = new SenderIdentity { Kind = SenderKind.Agent, SubjectId = "microsoft:someone-else" },
                },
            };
        }

        // While the session is still open (its mbus mcp, here this process, is running) the message waits for it to close.
        using (var self = Process.GetCurrentProcess())
            h.Sessions.Save(session with { ProcessId = self.Id, ProcessStartedAt = self.StartTime.ToUniversalTime() });
        Assert.Equal(DeclineReason.SessionOpen, (await h.DeliverAsync(Reply("dlv_open"))).Reason);
        // Never another person's session.
        Assert.Equal(DeclineReason.NotAccepted, (await h.DeliverAsync(Reply("dlv_theirs", inboxOwner: "microsoft:someone-else"))).Reason);
        // One the session already read (through wait or inbox) runs nothing.
        h.Sessions.Save(session with { Seen = ["msg_dlv_seen"] });
        Assert.True((await h.DeliverAsync(Reply("dlv_seen"))).Accepted);
        Assert.Equal("The session had already read this message.", (await h.Service.SettledAsync("dlv_seen")).Result.Summary);

        // Closed: the same session, in its own folder, with the orchestrator profile. It has nothing to settle itself.
        h.Sessions.Save(session);
        Assert.True((await h.DeliverAsync(Reply("dlv_reply"))).Accepted);
        var resumed = (await h.Service.SettledAsync("dlv_reply")).Result;
        Assert.Equal(SettleStatus.Succeeded, resumed.Status);
        var args = File.ReadAllLines(Path.Combine(folder, "fake-agent-args.txt"));
        Assert.Equal("sess-1", args[Array.IndexOf(args, "--resume") + 1]);
        Assert.Contains("Edit", args[Array.IndexOf(args, "--disallowedTools") + 1].Split(','));
        Assert.False(Directory.Exists(h.WorkTreeOf("dlv_reply")));
        // The role says to remove the worktree after the run: never the person's own folder.
        await h.Engine.StopAsync();
        Assert.Equal("the person's own work", File.ReadAllText(Path.Combine(folder, "README.md")));
    }

    [Fact]
    public async Task Closing_a_tools_connection_keeps_a_live_runs_inbox_open_for_replies()
    {
        await using var h = new RunnerHarness();
        var gate = Path.Combine(h.Scratch, "release-connection-test");
        Assert.True((await h.DeliverAsync(h.Task("dlv_owner", directives: d => d["wait"] = gate))).Accepted);
        using var self = Process.GetCurrentProcess();
        var record = new MessageBus.Mcp.SessionRecord
        {
            InboxId = "inb_connection", WorkspaceId = "ws_test", AgentKind = "claude-code",
            Folder = h.Scratch, ExpiresAt = h.Time.GetUtcNow().AddHours(24),
            RunDeliveryId = "dlv_owner", HostProcessId = self.Id, HostProcessStartedAt = self.StartTime.ToUniversalTime(),
        };
        h.Sessions.Save(record);
        using var http = new HttpClient();
        // EOF ends one real local MCP connection while the real runner's agent remains behind its gate.
        await MessageBus.Mcp.LocalServer.RunAsync(new MessageBus.Mcp.LocalServerOptions
        {
            Http = http, WorkspaceId = "ws_test", ConfigDirectory = Path.GetDirectoryName(h.Sessions.Directory)!,
            Input = new MemoryStream(), Output = new MemoryStream(), Session = record,
            Scope = new MessageBus.Mcp.RunToolScope { DeliveryId = "dlv_owner", ThreadId = "thr_dlv_owner", Root = h.Scratch },
        }, TestContext.Current.CancellationToken);
        var reply = h.Task("dlv_connection_reply") with { Role = null, Instructions = null, InboxOwner = RunnerHarness.Owner };
        reply = reply with { Envelope = reply.Envelope with { Target = new InboxTarget { InboxId = record.InboxId } } };
        Assert.Equal(DeclineReason.SessionOpen, (await h.DeliverAsync(reply)).Reason);
        Assert.Equal("dlv_owner", h.Sessions.Find(record.InboxId)!.RunDeliveryId);
        File.WriteAllText(gate, "");
        Assert.Equal(SettleStatus.Succeeded, (await h.Service.SettledAsync("dlv_owner")).Result.Status);
    }

    private static string Commit(string git, string repo, string text)
    {
        File.WriteAllText(Path.Combine(repo, "README.md"), text);
        Git(git, repo, "add", "README.md");
        Git(git, repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", text);
        return GitOutput(git, repo, "rev-parse", "HEAD");
    }

    private static string GitOutput(string git, string dir, params string[] args)
    {
        var info = new ProcessStartInfo(git) { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
        return output.Trim();
    }

    private static void AssertExits(int pid)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return; // already gone
        }
        using (process)
        {
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(5)), $"Process {pid} is still running.");
        }
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, false, true)]
    public async Task New_commits_are_returned_before_cleanup_or_the_worktree_is_kept(bool commit, bool canReturn, bool uploadThrows, bool fails = false)
    {
        await WindowsRunCapabilities.RequireGitShellAsync();
        // the original issue: otherwise the run's folder goes, succeeded or failed, and only its log stays.
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        var source = new FileBundleSource();
        var calls = 0;
        string? sentBundle = null;
        string? sentBase = null;
        var log = new StringWriter();
        await using var h = new RunnerHarness(bundles: source, log: log, keepRunFolders: false, extensions: e => e with
        {
            ReturnCommits = async (run, delivery, branch, baseCommit, ct) =>
            {
                calls++;
                Assert.Equal("main", branch);
                Assert.NotNull(baseCommit);
                sentBase = baseCommit;
                if (uploadThrows) throw new IOException("Upload failed");
                if (!canReturn) return false;
                sentBundle = Path.Combine(Path.GetDirectoryName(run.Root)!, "returned.bundle");
                await MessageBus.Attachments.Client.GitBundles.CreateAsync(run.WorkTree, sentBundle, branch, baseCommit, ct);
                return true;
            },
        });
        source.File = MakeBundle(git!, h.Scratch);
        var bundle = new AttachmentRef { Id = "att_code", Name = "work.bundle", Format = AttachmentFormat.GitBundle, SizeBytes = 1, Sha256 = "00", Branch = "main" };
        var task = h.Task("dlv_return", environment: new RunEnvironment { Checkout = CheckoutMode.Bundle, Logs = LogCapture.Local },
            attachments: [bundle], directives: d =>
            {
                if (commit) d["commit"] = "Task change";
                if (fails) d["mode"] = "settle-failed";
            });
        Assert.True((await h.DeliverAsync(task)).Accepted);
        Assert.Equal(fails ? SettleStatus.Failed : SettleStatus.Succeeded, (await h.Service.SettledAsync(task.DeliveryId)).Result.Status);
        await h.Engine.StopAsync(); // completion includes cleanup

        Assert.Equal(commit ? 1 : 0, calls);
        var kept = commit && (!canReturn || uploadThrows);
        var runFolder = Path.GetDirectoryName(h.WorkTreeOf(task.DeliveryId))!;
        if (!kept) Assert.Equal([Path.Combine(runFolder, "agent.log")], Directory.GetFileSystemEntries(runFolder));
        Assert.Equal(kept, Directory.Exists(h.WorkTreeOf(task.DeliveryId)));
        if (commit && canReturn && !uploadThrows)
        {
            Assert.True(File.Exists(sentBundle));
            var receiver = Directory.CreateDirectory(Path.Combine(h.Scratch, "receiver")).FullName;
            Git(git!, receiver, "clone", "--quiet", source.File!, ".");
            await MessageBus.Attachments.Client.GitBundles.FetchAsync(sentBundle!, receiver, "main", "returned", TestContext.Current.CancellationToken);
            Assert.NotNull(sentBase);
            Assert.Contains("returned the task branch", log.ToString());
        }
        else if (commit)
        {
            Assert.Contains(Path.GetDirectoryName(h.WorkTreeOf(task.DeliveryId))!, log.ToString());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cleanup_deletes_everything_the_run_left_but_its_log_when_the_role_keeps_logs(bool keepLog)
    {
        // the original issue: the decrypted code bundle, git settings and prompt went unnoticed beside the worktree before.
        var root = Path.Combine(Path.GetTempPath(), "mbus-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var run = RunWorkspace.Create(root, "dlv_scratch", 1);
            foreach (var folder in new[] { "work", "cache/dotnet-home", "cache/nuget", "review-build", "tmp" })
            {
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(run.Root, folder)).FullName, "file"), "cache");
            }
            foreach (var file in new[] { "task.bundle", "gitconfig", "git-ssh.sh", "prompt.md", "delivery.json", "settle.json", "agent.log" })
            {
                File.WriteAllText(Path.Combine(run.Root, file), file);
            }
            Assert.True(run.CleanUp(keepLog));
            if (keepLog) Assert.Equal([run.LogFile], Directory.GetFileSystemEntries(run.Root));
            else Assert.False(Directory.Exists(run.Root));
        }
        finally { RunWorkspace.TryDelete(root); }
    }

    [Fact]
    public void Cleanup_leaves_link_targets_and_an_earlier_retained_run_intact()
    {
        var root = Path.Combine(Path.GetTempPath(), "mbus-retained-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var run = RunWorkspace.Create(root, "dlv_kept", 1);
            Directory.CreateDirectory(run.WorkTree);
            var marker = Path.Combine(run.WorkTree, "commit");
            File.WriteAllText(marker, "kept");
            using var next = RunWorkspace.Create(root, "dlv_kept", 1);
            Assert.NotEqual(run.Root, next.Root);
            Assert.True(File.Exists(marker));
            // A scratch link may point at another run; removing it must never walk its target.
            if (!OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(Path.Combine(next.Root, "review-build"), run.WorkTree);
            File.WriteAllText(next.LogFile, "log");
            next.CleanUp(keepLog: true);
            Assert.True(File.Exists(marker));
            Assert.False(Directory.Exists(Path.Combine(next.Root, "review-build")));
        }
        finally { RunWorkspace.TryDelete(root); }
    }

    private static string MakeBundle(string git, string scratch)
    {
        var repo = Directory.CreateDirectory(Path.Combine(scratch, "repo")).FullName;
        Git(git, repo, "init", "--quiet", "--initial-branch=main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "from the bundle");
        Git(git, repo, "add", "README.md");
        Git(git, repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Start");
        var file = Path.Combine(scratch, "work.bundle");
        Git(git, repo, "bundle", "create", "--quiet", file, "--all");
        return file;
    }

    private static void Git(string git, string dir, params string[] args)
    {
        var info = new ProcessStartInfo(git) { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
    }

    private sealed class FileBundleSource : IBundleSource
    {
        public string? File { get; set; }

        public Task<string?> FetchAsync(TaskDelivery delivery, AttachmentRef bundle, string runDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(File);
    }
}
