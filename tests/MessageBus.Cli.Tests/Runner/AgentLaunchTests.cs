using System.Text.Json.Nodes;
using MessageBus.Cli.Mcp;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

/// <summary>
/// What a run loads besides the task (the original issue): every model call re-reads the system prompt, every tool schema and
/// the first message, so the runner gives the agent CLIs only the mbus server, only the tools the profile can use,
/// and a short prompt.
/// </summary>
public sealed class AgentLaunchTests : IDisposable
{
    private readonly string _run = Directory.CreateTempSubdirectory("mbus-launch-").FullName;

    public void Dispose() => Directory.Delete(_run, recursive: true);

    private AgentLaunchContext Context(PermissionProfile profile) =>
        new("prompt", Path.Combine(_run, "prompt.md"), profile, Path.Combine(_run, "work"),
            new Dictionary<string, string> { ["MBUS_DELIVERY_FILE"] = Path.Combine(_run, "delivery.json"), ["MBUS_AGENT"] = "agt_1" });

    private static string After(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void Claude_gets_only_the_mbus_server_with_the_runs_variables_and_only_the_built_in_tools_its_profile_can_use()
    {
        var mbus = new McpLaunchCommand("/opt/mbus/mbus", ["mcp"]);
        var reviewer = new PermissionProfile { Name = PermissionProfiles.Reviewer, CanEditFiles = false, CanRunCommands = false, Network = NetworkAccess.None };

        var args = new ClaudeCodeAdapter(mbus).BuildLaunch(Context(reviewer)).Arguments;

        // None of the person's own MCP servers, plugins' servers or claude.ai connectors: only the file's.
        Assert.Contains("--strict-mcp-config", args);
        var config = JsonNode.Parse(File.ReadAllText(After(args, "--mcp-config")))!["mcpServers"]!.AsObject();
        var server = Assert.Single(config);
        Assert.Equal(ClaudeCodeAdapter.McpServerName, server.Key);
        Assert.Equal("/opt/mbus/mbus", (string?)server.Value!["command"]);
        Assert.Equal("agt_1", (string?)server.Value["env"]!["MBUS_AGENT"]);
        Assert.Equal(Path.Combine(_run, "delivery.json"), (string?)server.Value["env"]!["MBUS_DELIVERY_FILE"]);

        // A read-only run can't edit, run commands or reach the web, and no run gets sub-agents or skills.
        Assert.Equal(["Read", "Glob", "Grep"], After(args, "--tools").Split(','));
        var worker = new PermissionProfile { Name = PermissionProfiles.Worker, AllowedTools = ["Bash(dotnet test:*)"], Network = NetworkAccess.Open };
        var tools = After(new ClaudeCodeAdapter(mbus).BuildLaunch(Context(worker)).Arguments, "--tools").Split(',');
        Assert.Equal(["Read", "Glob", "Grep", "Edit", "Write", "Bash", "WebFetch", "WebSearch"], tools);
    }

    [Fact]
    public void Claude_keeps_named_tools_only_from_the_owners_own_settings_in_a_run_file_only_they_can_read()
    {
        // the original issue: user scope and projects' local scope in .claude.json count; the repository's .mcp.json never does.
        var worker = PermissionProfiles.Defaults(PermissionProfiles.Worker);
        var work = Directory.CreateDirectory(Path.Combine(_run, "work")).FullName;
        File.WriteAllText(Path.Combine(work, ".mcp.json"), """{ "mcpServers": { "playwright": { "command": "repository" }, "docs": { "command": "repository" } } }""");
        var file = Path.Combine(_run, ".claude.json");
        var playwright = new JsonObject { ["command"] = "npx", ["args"] = new JsonArray("@playwright/mcp@latest"), ["env"] = new JsonObject { ["TOKEN"] = "s3cret" } };
        var docs = new JsonObject { ["type"] = "http", ["url"] = "https://docs.example/mcp" };
        static JsonObject Servers(params (string Name, JsonNode Server)[] servers) => new(servers.Select(s => KeyValuePair.Create(s.Name, (JsonNode?)s.Server.DeepClone())));
        void Settings(JsonObject? user, JsonObject projects) => File.WriteAllText(file, new JsonObject { ["mcpServers"] = user, ["projects"] = projects }.ToJsonString());
        var adapter = new ClaudeCodeAdapter(new McpLaunchCommand("/opt/mbus/mbus", ["mcp"]), file);
        var both = Context(worker) with { KeepAgentTools = ["playwright", "docs"] };

        Settings(Servers(("playwright", playwright)), new JsonObject { ["/elsewhere/app"] = new JsonObject { ["mcpServers"] = Servers(("docs", docs)) } });
        var plain = adapter.BuildLaunch(Context(worker)).Arguments;
        var launch = adapter.BuildLaunch(both);

        Assert.Null(launch.CannotStart);
        Assert.Equal(plain.Select(a => a == After(plain, "--allowedTools") ? a.Replace("mcp__mbus", "mcp__mbus,mcp__playwright,mcp__docs", StringComparison.Ordinal) : a), launch.Arguments);
        var path = After(launch.Arguments, "--mcp-config");
        var servers = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!.AsObject();
        Assert.Equal(["mbus", "playwright", "docs"], servers.Select(s => s.Key));
        Assert.True(JsonNode.DeepEquals(playwright, servers["playwright"]));
        Assert.True(JsonNode.DeepEquals(docs, servers["docs"]));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        Assert.Equal("Keeps these Claude Code tools: playwright, docs.", launch.Note);
        Assert.EndsWith("This run keeps these tools: playwright, docs. Use them instead of starting programs yourself.\n", launch.StandardInput);

        // The run's own folder's project wins over user scope; a project that turned a server off doesn't count.
        Settings(Servers(("playwright", playwright)), new JsonObject
        {
            [work.Replace('\\', '/')] = new JsonObject { ["mcpServers"] = Servers(("playwright", new JsonObject { ["command"] = "local" })) },
            ["/elsewhere/app"] = new JsonObject { ["mcpServers"] = Servers(("docs", docs)), ["disabledMcpServers"] = new JsonArray("docs") },
        });
        var local = adapter.BuildLaunch(Context(worker) with { KeepAgentTools = ["playwright"] });
        Assert.Equal("local", (string?)JsonNode.Parse(File.ReadAllText(After(local.Arguments, "--mcp-config")))!["mcpServers"]!["playwright"]!["command"]);
        Assert.Equal("This role's runs keep playwright, docs, but Claude Code on this computer doesn't have docs set up. Add it to Claude Code's settings here (claude mcp add --scope user), or run this role on another computer.",
            adapter.BuildLaunch(both).CannotStart);

        // Two projects that set it up differently, an entry with nothing to start, and a file that isn't JSON fail plainly,
        // never quoting the settings.
        var other = new JsonObject { ["type"] = "http", ["url"] = "https://other.example/mcp" };
        Settings(Servers(("playwright", playwright)), new JsonObject
        {
            ["/a"] = new JsonObject { ["mcpServers"] = Servers(("docs", docs)) },
            ["/b"] = new JsonObject { ["mcpServers"] = Servers(("docs", other)) },
        });
        Assert.Equal("This role's runs keep docs, which Claude Code on this computer has set up differently in several projects. Add it once for all projects (claude mcp add --scope user), or run this role on another computer.",
            adapter.BuildLaunch(both).CannotStart);
        Settings(Servers(("playwright", new JsonObject { ["args"] = new JsonArray("x"), ["env"] = new JsonObject { ["TOKEN"] = "s3cret" } })), new JsonObject());
        Assert.Equal("This role's runs keep playwright, but its entry in Claude Code's settings on this computer isn't complete: it needs a command to start or an address. Set it up again with claude mcp add, or run this role on another computer.",
            adapter.BuildLaunch(both).CannotStart);
        File.WriteAllText(file, """{ "mcpServers": { "playwright": { "env": { "TOKEN": "s3cret" """);
        Assert.Equal("This role's runs keep playwright, docs, but Claude Code's settings file on this computer (.claude.json) couldn't be read. Fix the file, or run this role on another computer.",
            adapter.BuildLaunch(both).CannotStart);
    }

    [Fact]
    public void A_built_in_reviewer_run_launches_without_edit_tools_and_only_gits_read_only_commands()
    {
        // the original issue: the profile a built-in reviewer role gets.
        var reviewer = PermissionProfiles.Defaults(PermissionProfiles.Reviewer);

        var args = new ClaudeCodeAdapter(new McpLaunchCommand("/opt/mbus/mbus", ["mcp"])).BuildLaunch(Context(reviewer)).Arguments;

        Assert.Equal(["Read", "Glob", "Grep", "Bash"], After(args, "--tools").Split(','));
        var allowed = After(args, "--allowedTools").Split(',');
        Assert.Equal(PermissionProfiles.ReviewerCommands, allowed.Where(t => t.StartsWith("Bash", StringComparison.Ordinal)));
        var denied = After(args, "--disallowedTools").Split(',');
        Assert.Contains("Edit", denied);
        Assert.Contains("Write", denied);
        Assert.Equal("default", After(args, "--permission-mode"));
        // Codex can't be held to a command list; its own sandbox keeps the run read-only.
        var codex = new CodexAdapter("gpt-5", mbusServerConfigured: true, codexHome: _run).BuildLaunch(Context(reviewer)).Arguments;
        Assert.Equal("read-only", After(codex, "--sandbox"));
    }

    [Fact]
    public void Codex_on_Windows_uses_its_sandbox_that_needs_no_admin_rights_and_the_network_whenever_the_role_has_some()
    {
        // the original issue: never Codex's elevated sandbox (its setup asks for admin rights); the run's proxy limits the network.
        var hostList = new PermissionProfile { Name = PermissionProfiles.Worker, CanEditFiles = true, Network = NetworkAccess.Allowlist };
        var offline = hostList with { Network = NetworkAccess.None };
        var readOnly = hostList with { CanEditFiles = false };
        List<string> Launch(PermissionProfile profile, bool windows, string? sandbox = null, bool resumed = false) =>
            [.. new CodexAdapter("gpt-5", mbusServerConfigured: true, codexHome: _run, getEnvironmentVariable: name => name == CodexAdapter.SandboxOverrideVariable ? sandbox : null)
                .BuildLaunch(resumed ? Context(profile) with { ResumeSessionId = "01a1d693-49de-7192-acc6-dc991d600218" } : Context(profile), windows).Arguments];
        static bool Has(List<string> args, string setting) => args.Where((a, i) => a == "-c" && i + 1 < args.Count && args[i + 1] == setting).Any();

        // Always said, both ways: a config.toml that turns prefer_mxc on can't undo the override.
        foreach (var (sandbox, mxc) in new[] { ((string?)null, "true"), ("unelevated", "false") })
        {
            foreach (var launch in new[] { Launch(hostList, true, sandbox), Launch(hostList, true, sandbox, resumed: true), Launch(readOnly, true, sandbox) })
            {
                Assert.True(Has(launch, $"features.prefer_mxc={mxc}"));
                Assert.True(Has(launch, "windows.sandbox=\"unelevated\""));
            }
        }
        Assert.True(Has(Launch(hostList, windows: true), "sandbox_workspace_write.network_access=true"));
        Assert.False(Has(Launch(offline, windows: true), "sandbox_workspace_write.network_access=true"));

        var mac = Launch(hostList, windows: false);
        Assert.DoesNotContain(mac, a => a.StartsWith("features.prefer_mxc", StringComparison.Ordinal) || a.StartsWith("windows.", StringComparison.Ordinal));
        Assert.False(Has(mac, "sandbox_workspace_write.network_access=true")); // elsewhere a host list without hosts keeps Codex offline
    }

    [Fact]
    public void A_reviewer_that_may_run_tests_gets_the_repositorys_test_commands_and_no_other_command()
    {
        // the original issue: a .NET solution, a front end with a test script, a docs site without one, and a team workflow file
        // that names a test command the files don't show and a command that isn't a test.
        var work = Directory.CreateDirectory(Path.Combine(_run, "work")).FullName;
        File.WriteAllText(Path.Combine(work, "TodoApp.slnx"), "<Solution />");
        Directory.CreateDirectory(Path.Combine(work, "client"));
        File.WriteAllText(Path.Combine(work, "client", "package.json"), """{ "scripts": { "test": "vitest run", "build": "vite build" } }""");
        Directory.CreateDirectory(Path.Combine(work, "docs"));
        File.WriteAllText(Path.Combine(work, "docs", "package.json"), """{ "scripts": { "build": "astro build" } }""");
        Directory.CreateDirectory(Path.Combine(work, ".messagebus"));
        File.WriteAllText(Path.Combine(work, ".messagebus", "workflow.md"), """
            ### 2. Build
            - **What:**
              ```
              cargo test --all && rm -rf /
              ```
            """);
        var reviewer = PermissionProfiles.Defaults(PermissionProfiles.Reviewer) with { CanRunTests = true };

        var profile = TestCommands.Apply(reviewer, work);
        var args = new ClaudeCodeAdapter(new McpLaunchCommand("/opt/mbus/mbus", ["mcp"])).BuildLaunch(Context(profile)).Arguments;

        var commands = After(args, "--allowedTools").Split(',').Where(t => t.StartsWith("Bash", StringComparison.Ordinal)).Except(PermissionProfiles.ReviewerCommands);
        Assert.Equal(["Bash(dotnet test:*)", "Bash(npm test:*)", "Bash(cargo test:*)"], commands);
        Assert.DoesNotContain("Bash", After(args, "--disallowedTools").Split(','));
        Assert.Contains("Edit", After(args, "--disallowedTools").Split(','));
        // Off, or with commands allowed anyway, the profile is left as it is.
        var off = reviewer with { CanRunTests = false };
        Assert.Same(off, TestCommands.Apply(off, work));
    }

    [Fact]
    public void Copilot_gets_the_mbus_server_with_the_runs_variables_and_a_reviewer_only_gits_read_only_commands()
    {
        // the original issue: the person's own MCP servers stay out of the run; the run's mbus server replaces theirs.
        var home = Directory.CreateDirectory(Path.Combine(_run, ".copilot")).FullName;
        File.WriteAllText(Path.Combine(home, "mcp-config.json"), """{ "mcpServers": { "mbus": { "command": "mbus" }, "playwright": { "command": "npx" } } }""");
        var adapter = new CopilotAdapter(new McpLaunchCommand("/opt/mbus/mbus", ["mcp"]), home);

        var reviewer = adapter.BuildLaunch(Context(PermissionProfiles.Defaults(PermissionProfiles.Reviewer))).Arguments;

        var config = JsonNode.Parse(File.ReadAllText(After(reviewer, "--additional-mcp-config")[1..]))!["mcpServers"]!["mbus"]!;
        Assert.Equal("/opt/mbus/mbus", (string?)config["command"]);
        Assert.Equal("agt_1", (string?)config["env"]!["MBUS_AGENT"]);
        Assert.Contains("--disable-mcp-server=playwright", reviewer);
        Assert.DoesNotContain("--disable-mcp-server=mbus", reviewer);
        // A reviewer can't run commands: no blanket permission, only git's read-only commands, no edits, no push.
        Assert.DoesNotContain("--allow-all-tools", reviewer);
        Assert.Contains("--allow-tool=shell(git log:*)", reviewer);
        Assert.DoesNotContain("--allow-tool=write", reviewer);
        Assert.Contains("--deny-tool=write", reviewer);
        Assert.Contains("--deny-tool=shell(git push)", reviewer);
        // A worker that may push gets everything but merging and approving.
        var pusher = adapter.BuildLaunch(Context(new PermissionProfile { Name = PermissionProfiles.Worker, CanPush = true, Network = NetworkAccess.Open })).Arguments;
        Assert.Contains("--allow-all-tools", pusher);
        Assert.Contains("--allow-all-urls", pusher);
        Assert.DoesNotContain("--deny-tool=shell(git push)", pusher);
        Assert.Contains("--deny-tool=shell(gh pr merge)", pusher);
    }

    [Theory]
    [InlineData("2.3.0", true)]
    [InlineData("1.19.5", false)]
    [InlineData(null, true)]
    public void OpenCode_runs_its_own_server_when_it_has_a_shared_one_and_gets_the_profile_and_mbus_through_its_settings(string? version, bool standalone)
    {
        var adapter = new OpenCodeAdapter(version, mbus: new McpLaunchCommand("/opt/mbus/mbus", ["mcp"]));

        var launch = adapter.BuildLaunch(Context(PermissionProfiles.Defaults(PermissionProfiles.Reviewer)));

        // OpenCode 1 refuses --standalone; OpenCode 2 without it runs the tools in its shared background server.
        Assert.Equal(standalone, launch.Arguments.Contains("--standalone"));
        Assert.Equal("json", After(launch.Arguments, "--format"));
        var settings = JsonNode.Parse(launch.Environment[OpenCodeAdapter.ConfigContentVariable])!;
        Assert.Equal(["/opt/mbus/mbus", "mcp"], settings["mcp"]!["mbus"]!["command"]!.AsArray().Select(a => (string?)a));
        Assert.Equal("agt_1", (string?)settings["mcp"]!["mbus"]!["environment"]!["MBUS_AGENT"]);
        var permission = settings["permission"]!;
        Assert.Equal("deny", (string?)permission["edit"]);
        Assert.Equal("deny", (string?)permission["bash"]!["*"]);
        Assert.Equal("allow", (string?)permission["bash"]!["git log *"]);
        var worker = JsonNode.Parse(adapter.BuildLaunch(Context(new PermissionProfile { Name = PermissionProfiles.Worker })).Environment[OpenCodeAdapter.ConfigContentVariable])!["permission"]!;
        Assert.Equal(("allow", "allow", "deny"), ((string?)worker["edit"], (string?)worker["bash"]!["*"], (string?)worker["bash"]!["git push *"]));
    }

    [Fact]
    public void Codex_turns_off_the_other_mcp_servers_in_its_settings_but_not_mbus_or_servers_it_doesnt_have()
    {
        var home = Directory.CreateDirectory(Path.Combine(_run, ".codex")).FullName;
        File.WriteAllText(Path.Combine(home, "config.toml"), """
            model = "gpt-5"

            [mcp_servers.mbus]
            command = "mbus"

            [mcp_servers."fal-ai"]
            url = "https://mcp.example/mcp"

            [mcp_servers."fal-ai".http_headers]
            x = "y"

            [mcp_servers.node_repl]
            command = "node_repl"

            [mcp_servers.node_repl.env]
            A = "b"

            [mcp_servers."two words"]
            command = "x"
            """);

        var args = new CodexAdapter("gpt-5", mbusServerConfigured: true, codexHome: home).BuildLaunch(Context(new PermissionProfile { Name = PermissionProfiles.Worker })).Arguments;

        // A -c for a server Codex has no command or URL for stops Codex, so only the servers that are there.
        var disabled = args.Where(a => a.EndsWith(".enabled=false", StringComparison.Ordinal)).ToList();
        Assert.Equal(["mcp_servers.fal-ai.enabled=false", "mcp_servers.node_repl.enabled=false"], disabled);
        Assert.Contains("features.plugins=false", args);
        Assert.Contains("features.apps=false", args);
    }

    [Fact]
    public void A_resumed_codex_session_keeps_its_read_only_sandbox_as_a_setting_because_exec_resume_has_no_sandbox_flag()
    {
        // the original issue, checked against codex-cli 0.169's `codex exec resume --help`: [OPTIONS] [SESSION_ID] [PROMPT], -c but no --sandbox.
        var launch = new CodexAdapter("gpt-5", codexHome: Path.Combine(_run, ".codex")).BuildLaunch(Context(new PermissionProfile { Name = PermissionProfiles.Orchestrator, CanEditFiles = false, CanRunCommands = false })
            with { ResumeSessionId = "01a1d693-49de-7192-acc6-dc991d600218" });

        Assert.Equal(["exec", "resume", "01a1d693-49de-7192-acc6-dc991d600218"], launch.Arguments.Take(3));
        Assert.DoesNotContain("--sandbox", launch.Arguments);
        Assert.Contains("sandbox_mode=\"read-only\"", launch.Arguments);
        Assert.Equal("-", launch.Arguments[^1]); // the prompt on stdin
        // Under the macOS sandbox, a Codex that sandboxes its own commands isn't wrapped where nesting isn't allowed.
        Assert.True(MessageBus.Cli.Runner.Sandbox.MacSandbox.StartsItsOwnSandbox(new ProcessLaunch { FileName = "codex", Arguments = launch.Arguments, WorkingDirectory = _run }));
    }

    [Fact]
    public void Codex_also_turns_off_a_trusted_repositorys_own_mcp_servers_but_never_mbus()
    {
        // The run works in a subfolder of a repository whose root and subfolder both have Codex settings (the original issue).
        var repo = Directory.CreateDirectory(Path.Combine(_run, "work")).FullName;
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, ".codex"));
        File.WriteAllText(Path.Combine(repo, ".codex", "config.toml"), """
            [mcp_servers.repo-docs]
            command = "node"
            args = ["docs-server.js"]

            [mcp_servers.mbus]
            command = "mbus"

            [mcp_servers.shared]
            command = "shared"
            """);
        var sub = Directory.CreateDirectory(Path.Combine(repo, "api", ".codex")).Parent!.FullName;
        File.WriteAllText(Path.Combine(sub, ".codex", "config.toml"), "[mcp_servers.api-db]\nurl = \"http://localhost:7000/mcp\"\n");
        var home = Directory.CreateDirectory(Path.Combine(_run, ".codex")).FullName;
        var userConfig = $"""
            [mcp_servers.shared]
            command = "shared"

            [projects.'{repo}']
            trust_level = "trusted"
            """;
        File.WriteAllText(Path.Combine(home, "config.toml"), userConfig);
        var context = Context(new PermissionProfile { Name = PermissionProfiles.Worker }) with { WorkingDirectory = sub };

        List<string> Disabled() => [.. new CodexAdapter("gpt-5", mbusServerConfigured: true, codexHome: home).BuildLaunch(context).Arguments
            .Where(a => a.EndsWith(".enabled=false", StringComparison.Ordinal))];

        Assert.Equal(["mcp_servers.shared.enabled=false", "mcp_servers.repo-docs.enabled=false", "mcp_servers.api-db.enabled=false"], Disabled());

        // Untrusted, Codex doesn't read the repository's settings, and a -c for a server it doesn't have would stop it.
        File.WriteAllText(Path.Combine(home, "config.toml"), userConfig.Replace("\"trusted\"", "\"untrusted\"", StringComparison.Ordinal));
        Assert.Equal(["mcp_servers.shared.enabled=false"], Disabled());
    }

    [Fact]
    public void A_retained_Codex_tool_cannot_request_the_runners_model_key()
    {
        var home = Directory.CreateDirectory(Path.Combine(_run, ".codex")).FullName;
        File.WriteAllText(Path.Combine(home, "config.toml"), """
            [mcp_servers.tool]
            command = "tool"
            env_vars = ["CODEX_API_KEY"]
            """);
        var context = Context(new PermissionProfile { Name = PermissionProfiles.Worker }) with { KeepAgentTools = ["tool"] };
        var adapter = new CodexAdapter(null, codexHome: home);

        Assert.Null(adapter.BuildLaunch(context).CannotStart);
        var refused = adapter.BuildLaunch(context with { UsesCodexEnvironmentKey = true });
        Assert.Contains("may forward CODEX_API_KEY", refused.CannotStart);
        Assert.Empty(refused.Arguments);
    }

    [Fact]
    public async Task On_windows_an_npm_installed_codex_starts_without_cmd_so_its_quoted_mbus_settings_reach_it_intact()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), ".cmd shims are Windows only.");
        // the original issue: npm's codex.cmd as npm 10/11 writes it, with node found on PATH (none beside the shim). The fake agent stands
        // in for node.exe and writes down the arguments it was given.
        var npm = Directory.CreateDirectory(Path.Combine(_run, "npm")).FullName;
        var script = Path.Combine(npm, "node_modules", "@openai", "codex", "bin", "codex.js");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "");
        var shim = Path.Combine(npm, "codex.cmd");
        File.WriteAllText(shim, string.Join("\r\n",
            "@ECHO off", "GOTO start", ":find_dp0", "SET dp0=%~dp0", "EXIT /b", ":start", "SETLOCAL", "CALL :find_dp0", "",
            """IF EXIST "%dp0%\node.exe" (""", """  SET "_prog=%dp0%\node.exe" """.TrimEnd(), ") ELSE (", """  SET "_prog=node" """.TrimEnd(),
            "  SET PATHEXT=%PATHEXT:;.JS;=;%", ")", "",
            """endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\node_modules\@openai\codex\bin\codex.js" %*""", ""));
        var node = Directory.CreateDirectory(Path.Combine(_run, "nodejs")).FullName;
        File.Copy(RunnerHarness.FakeAgentPath, Path.Combine(node, "node.exe"));
        foreach (var file in (string[])["fake-agent.dll", "fake-agent.runtimeconfig.json", "fake-agent.deps.json", "MessageBus.Testing.dll"])
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(node, file));
        }
        var work = Directory.CreateDirectory(Path.Combine(_run, "work")).FullName;
        var launch = new CodexAdapter("gpt-5", mbusServerConfigured: true, codexHome: Path.Combine(_run, ".codex"))
            .BuildLaunch(Context(new PermissionProfile { Name = PermissionProfiles.Worker }));
        string[] args = [.. launch.Arguments, "100%PATH%"];

        using (var agent = AgentProcess.Start(new ProcessLaunch
        {
            FileName = shim,
            Arguments = args,
            WorkingDirectory = work,
            Environment = new Dictionary<string, string?> { ["PATH"] = node + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") },
            StandardInput = launch.StandardInput,
        }))
        {
            Assert.Equal(0, await agent.WaitForExitAsync());
        }

        Assert.Contains("mcp_servers.mbus.env.MBUS_AGENT=\"agt_1\"", args);
        Assert.Equal([script, .. args], File.ReadAllLines(Path.Combine(work, "fake-agent-args.txt")));

        // A batch file that isn't a known shim still goes through cmd.exe, which can't be given " or % safely.
        var other = Path.Combine(_run, "other.cmd");
        File.WriteAllText(other, "@echo off\r\necho %*\r\n");
        Assert.Throws<ArgumentException>(() => AgentProcess.Start(new ProcessLaunch { FileName = other, Arguments = args, WorkingDirectory = work }));
    }

    [Fact]
    public void The_task_prompt_has_the_role_instructions_once_and_little_else()
    {
        const string instructions = "# Developer\n\nYou implement one task in the worktree you were given.";
        var now = DateTimeOffset.UnixEpoch;
        var delivery = new TaskDelivery
        {
            DeliveryId = "dlv_1", Attempt = 1, LeaseExpiresAt = now, AgentId = "agt_1", Role = "developer", HeartbeatSeconds = 30,
            Instructions = new InstructionVersion { Role = "developer", Version = 3, Content = instructions, ContentSha256 = "00", Source = InstructionSource.BuiltIn, CreatedAt = now },
            RunEnvironment = new RunEnvironment(),
            Envelope = new MessageEnvelope
            {
                Id = "msg_1", Type = "task.implement", Source = "agent://ws/agt_0", Time = now, WorkspaceId = "ws", Target = new RoleTarget { Role = "developer" },
                ThreadId = "thr_1", Title = "API and API tests: due dates", Sender = new SenderIdentity { Kind = SenderKind.Agent, DisplayName = "Claude Code on PC" },
                Body = new BodyRef { SizeBytes = 6, Sha256 = "00" },
                Refs = [new Ref { Kind = "pr", Repo = "acme/api", Number = 42 }],
            },
        };

        var prompt = TaskPrompt.Build(delivery, "Studio-Mac", restricted: false);

        Assert.Single(prompt.Split(instructions)[1..]);
        Assert.Contains("thr_1", prompt);
        // Everything but the role's own instructions: the inbox call returns the rest of the message.
        Assert.True(prompt.Length - instructions.Length < 1000, $"The prompt's own text is {prompt.Length - instructions.Length} characters.");
    }

    [Fact]
    public void Antigravity_launch_maps_developer_and_readonly_profiles_to_settings_and_flags()
    {
        var adapter = new AntigravityAdapter();
        var devCtx = Context(new PermissionProfile
        {
            Name = PermissionProfiles.Worker,
            CanEditFiles = true,
            CanRunCommands = true,
            CanPush = false,
            Network = NetworkAccess.Allowlist,
            AllowedHosts = ["api.github.com"],
        }) with
        {
            Model = "gemini-4-neon-high",
            ResumeSessionId = "conv-123",
            RunnerIsolationEnabled = true,
            RunnerSandboxed = false,
            KeepAgentTools = ["context7"],
        };

        var devLaunch = adapter.BuildLaunch(devCtx);
        Assert.Null(devLaunch.StandardInput);
        Assert.Contains("--output-format", devLaunch.Arguments);
        Assert.Contains("stream-json", devLaunch.Arguments);
        Assert.Contains("--disable-slash-commands", devLaunch.Arguments);
        Assert.Contains("--dangerously-skip-permissions", devLaunch.Arguments);
        Assert.Contains("--sandbox", devLaunch.Arguments);
        Assert.Contains("--model", devLaunch.Arguments);
        Assert.Contains("gemini-4-neon-high", devLaunch.Arguments);
        Assert.Contains("--conversation", devLaunch.Arguments);
        Assert.Contains("conv-123", devLaunch.Arguments);
        Assert.Contains("-p", devLaunch.Arguments);

        var settingsIdx = devLaunch.Arguments.ToList().IndexOf("--settings");
        Assert.True(settingsIdx >= 0);
        var settingsPath = devLaunch.Arguments[settingsIdx + 1];
        var devSettings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!;
        var devAllow = devSettings["permissions"]!["allow"]!.AsArray().Select(n => (string)n!).ToList();
        var devDeny = devSettings["permissions"]!["deny"]!.AsArray().Select(n => (string)n!).ToList();
        Assert.Contains("mcp(mbus/*)", devAllow);
        Assert.Contains("mcp(context7/*)", devAllow);
        Assert.Contains("command(*)", devAllow);
        Assert.Contains("read_url(api.github.com)", devAllow);
        Assert.Contains("command(gh pr merge)", devDeny);
        Assert.Contains("command(git push)", devDeny);

        // Read-only reviewer profile: no --dangerously-skip-permissions, write_file and network denied, allowed Bash commands mapped.
        var reviewCtx = Context(new PermissionProfile
        {
            Name = PermissionProfiles.Reviewer,
            CanEditFiles = false,
            CanRunCommands = false,
            CanPush = false,
            Network = NetworkAccess.None,
            AllowedTools = ["Bash(git log:*)", "Bash(git diff:*)"],
        }) with { RunnerSandboxed = true };

        var reviewLaunch = adapter.BuildLaunch(reviewCtx);
        Assert.DoesNotContain("--dangerously-skip-permissions", reviewLaunch.Arguments);
        Assert.DoesNotContain("--sandbox", reviewLaunch.Arguments);
        var reviewSettings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!;
        var reviewAllow = reviewSettings["permissions"]!["allow"]!.AsArray().Select(n => (string)n!).ToList();
        var reviewDeny = reviewSettings["permissions"]!["deny"]!.AsArray().Select(n => (string)n!).ToList();
        Assert.Contains("command(git log)", reviewAllow);
        Assert.Contains("command(git diff)", reviewAllow);
        Assert.Contains("write_file(*)", reviewDeny);
        Assert.Contains("read_url(*)", reviewDeny);
        Assert.DoesNotContain("command(*)", reviewDeny);
    }
}
