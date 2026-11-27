using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Tests.Shared;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class JobWorkflowCommandTests
{
    private const string Root = "/api/w/ws_test/";
    private const string Workflows = """
        [{"id":"wf_feature","name":"Feature","description":"Build a feature","state":"published","publishedVersion":3},
         {"id":"wf_draft","name":"Draft","state":"draft"}]
        """;

    [Fact]
    public async Task Start_resolves_a_published_workflow_sends_overrides_and_returns_the_link_without_following()
    {
        using var machine = Machine();
        machine.Server.On(HttpMethod.Get, Root + "workflows", _ => Json(Workflows))
            .On(HttpMethod.Post, Root + "jobs", _ => Json("""{"id":"job_test","state":"running"}"""));

        var run = await machine.RunAsync("job", "start", "gh:acme/app#42", "--workflow", "fEaTuRe", "--instructions", "Keep it small",
            "--limit", "12.50", "--runs-on", "owner-runners");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(FakeServer.Url + "/jobs/job_test", run.Output.Trim());
        var request = machine.Server.Single(HttpMethod.Post, Root + "jobs");
        var body = JsonNode.Parse(request.BodyText)!;
        Assert.Equal("wf_feature", (string?)body["workflowId"]);
        Assert.Equal("gh:acme/app#42", (string?)body["workItem"]);
        Assert.Equal("Keep it small", (string?)body["instructions"]);
        Assert.Equal(12.5m, body["limitUsd"]!.GetValue<decimal>());
        Assert.Equal("owner-runners", (string?)body["runsOn"]);
        Assert.Equal("MbusKey mbk_test", request.Header("Authorization"));
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.StartsWith(Root + "jobs/", StringComparison.Ordinal));

        foreach (var name in new[] { "Draft", "Missing" })
        {
            var refused = await machine.RunAsync("job", "start", "gh:acme/app#42", "--workflow", name);
            Assert.NotEqual(0, refused.ExitCode);
            Assert.Contains("Feature, Draft", refused.Error);
        }
        Assert.Single(machine.Server.Requests, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == Root + "jobs");
    }

    [Fact]
    public async Task Start_sends_the_locally_read_issue_within_service_limits()
    {
        using var machine = Machine();
        machine.WorkItemCommand = (start, _) =>
        {
            Assert.Equal("gh", start.FileName);
            Assert.Equal(["issue", "view", "42", "--repo", "acme/app", "--json", "title,body"], start.ArgumentList);
            Assert.Equal("1", start.Environment["GH_PROMPT_DISABLED"]);
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            return Task.FromResult((0, new JsonObject
            {
                ["title"] = "  " + new string('t', 201) + "  ", ["body"] = new string('b', 30_001),
            }.ToJsonString()));
        };
        machine.Server.On(HttpMethod.Get, Root + "workflows", _ => Json(Workflows))
            .On(HttpMethod.Post, Root + "jobs", _ => Json("""{"id":"job_test","state":"running"}"""));

        var run = await machine.RunAsync("job", "start", "gh:acme/app#42", "--workflow", "Feature");

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Error);
        var body = JsonNode.Parse(machine.Server.Single(HttpMethod.Post, Root + "jobs").BodyText)!;
        Assert.Equal(new string('t', 200), (string?)body["workItemTitle"]);
        Assert.Equal(new string('b', 30_000), (string?)body["workItemBody"]);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("missing")]
    [InlineData("timeout")]
    [InlineData("malformed")]
    [InlineData("wrong-shape")]
    [InlineData("ado")]
    public async Task Start_without_local_issue_context_still_starts_quietly(string reason)
    {
        using var machine = Machine();
        var calls = 0;
        machine.WorkItemCommand = async (_, ct) =>
        {
            calls++;
            if (reason == "missing") throw new System.ComponentModel.Win32Exception();
            if (reason == "timeout")
            {
                var pending = new TaskCompletionSource<(int, string)>();
                using var registration = ct.Register(() => pending.TrySetCanceled(ct));
                machine.Time.Advance(TimeSpan.FromSeconds(5));
                return await pending.Task;
            }
            return reason switch
            {
                "malformed" => (0, "not JSON"),
                "wrong-shape" => (0, """{"title":12,"body":null}"""),
                _ => (1, "not signed in"),
            };
        };
        machine.Server.On(HttpMethod.Get, Root + "workflows", _ => Json(Workflows))
            .On(HttpMethod.Post, Root + "jobs", _ => Json("""{"id":"job_test","state":"running"}"""));

        var run = await machine.RunAsync("job", "start", reason == "ado" ? "ado:org/project#42" : "gh:acme/app#42", "--workflow", "Feature");

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Error);
        Assert.Equal(reason == "ado" ? 0 : 1, calls);
        var body = JsonNode.Parse(machine.Server.Single(HttpMethod.Post, Root + "jobs").BodyText)!.AsObject();
        Assert.False(body.ContainsKey("workItemTitle"));
        Assert.False(body.ContainsKey("workItemBody"));
    }

    [Fact]
    public async Task Show_prints_step_names_rounds_results_agents_duration_and_waiting_details()
    {
        using var machine = Machine();
        machine.Server.On(HttpMethod.Get, Root + "jobs/job_test", _ => Json("""
            {"id":"job_test","title":"Add due dates","workItem":{"key":"gh:acme/app#42"},"state":"running",
             "workflow":{"id":"wf_feature","name":"Feature","version":3},"spentUsd":2.5,"limitUsd":12,
             "path":{"steps":[{"id":"review","name":"Code review"},{"id":"merge","name":"Wait for merge"}]},
             "steps":[{"stepId":"review","round":2,"state":"passed","outcome":"changes-needed","agent":"agt_review","machineId":"mch_test",
               "startedAt":"2026-11-13T08:59:00Z","endedAt":"2026-11-13T08:59:30Z"},
               {"stepId":"merge","round":1,"state":"waiting","waitingOn":{"kind":"checks","pullRequest":57,"done":2,"total":4}}]}
            """));

        var run = await machine.RunAsync("job", "show", "job_test");

        Assert.Equal(0, run.ExitCode);
        foreach (var text in new[] { "Add due dates", "gh:acme/app#42", "Feature, version 3", "2.5 / 12", "Code review", "round 2", "passed",
                     "changes needed", "agt_review on mch_test", "30s", "Wait for merge", "PR #57: 2/4 done" })
            Assert.Contains(text, run.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_workflow_command_refuses_when_capability_is_absent_or_workspace_route_returns_404(bool advertised)
    {
        using var machine = Machine(advertised);
        string[][] commands =
        [
            ["job", "start", "gh:acme/app#42", "--workflow", "Feature"], ["job", "list"], ["job", "show", "job_test"],
            ["job", "stop", "job_test"], ["job", "retry", "job_test", "--step", "code"], ["job", "open", "job_test"],
            ["workflow", "list"], ["workflow", "show", "Feature"], ["workflow", "check", machine.WriteHomeFile("path.json", "{}")],
        ];
        foreach (var args in commands)
        {
            var run = await machine.RunAsync(args);
            Assert.NotEqual(0, run.ExitCode);
            Assert.Equal("Workflows are not switched on for this workspace.", run.Error.Trim());
            Assert.Empty(run.Output);
        }
        if (!advertised) Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.StartsWith(Root, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hands_on_job_and_project_alias_share_the_handler_and_warn_once_only_on_stderr()
    {
        using var machine = Machine(false);
        machine.Server.On(HttpMethod.Post, "/mcp", request =>
        {
            var message = JsonNode.Parse(request.BodyText)!;
            if (message["id"] is not { } id) return new(HttpStatusCode.Accepted);
            if (message["method"]!.ToString() is not ("initialize" or "tools/call"))
                return Json(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" },
                }.ToJsonString());
            var result = message["method"]!.ToString() == "initialize"
                ? JsonNode.Parse("""{"capabilities":{"tools":{}},"serverInfo":{"name":"test","version":"1"}}""")!
                : new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "{\"project\":null}" }) };
            if (message["method"]!.ToString() == "initialize") result["protocolVersion"] = message["params"]!["protocolVersion"]!.DeepClone();
            else Assert.Equal(McpToolNames.FindProject, (string?)message["params"]!["name"]);
            return Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString());
        });

        var job = await machine.RunAsync("job", "find", "file:plan.md", "--json");
        var first = await machine.RunAsync("project", "find", "file:plan.md", "--json");
        var second = await machine.RunAsync("project", "find", "file:plan.md", "--json");

        Assert.True(job.ExitCode == 0, job.Error);
        Assert.True(first.ExitCode == 0, first.Error);
        Assert.True(second.ExitCode == 0, second.Error);
        Assert.Empty(job.Error);
        Assert.Contains("`mbus project` is now `mbus job`", first.Error);
        Assert.Empty(second.Error);
        Assert.Equal(job.Output, first.Output);
        Assert.Equal(job.Output, second.Output);
        Assert.Null(JsonNode.Parse(first.Output)!["project"]);
    }

    [Fact]
    public async Task List_filters_and_step_controls_use_the_agreed_routes_and_json_output()
    {
        using var machine = Machine();
        machine.Server.On(HttpMethod.Get, Root + "workflows", _ => Json(Workflows))
            .On(HttpMethod.Get, "/api/caller", _ => Json("""{"subjectId":"test:starter"}"""))
            .On(HttpMethod.Get, Root + "jobs", _ => Json("[]"))
            .On(HttpMethod.Post, Root + "jobs/job_test/stop", _ => new(HttpStatusCode.NoContent))
            .On(HttpMethod.Post, Root + "jobs/job_test/steps/code/retry", _ => new(HttpStatusCode.NoContent));

        var list = await machine.RunAsync("job", "list", "--state", "running", "--workflow", "Feature", "--mine", "--json");
        Assert.Equal(0, list.ExitCode);
        Assert.Empty(JsonNode.Parse(list.Output)!.AsArray());
        var query = machine.Server.Single(HttpMethod.Get, Root + "jobs").Uri.Query;
        Assert.Contains("state=running", query);
        Assert.Contains("workflowId=wf_feature", query);
        Assert.Contains("startedBy=test%3Astarter", query);
        foreach (var args in new[] { new[] { "job", "stop", "job_test", "--json" }, ["job", "retry", "job_test", "--step", "code", "--json"] })
        {
            var run = await machine.RunAsync(args);
            Assert.Equal(0, run.ExitCode);
            Assert.Empty(JsonNode.Parse(run.Output)!.AsObject());
        }
        machine.Server.Single(HttpMethod.Post, Root + "jobs/job_test/stop");
        machine.Server.Single(HttpMethod.Post, Root + "jobs/job_test/steps/code/retry");
    }

    [Fact]
    public async Task Workflow_show_resolves_the_name_and_prints_settings_and_the_published_steps_and_arrows()
    {
        using var machine = Machine();
        machine.Server.On(HttpMethod.Get, Root + "workflows", _ => Json(Workflows))
            .On(HttpMethod.Get, Root + "workflows/wf_feature", _ => Json("""
                {"id":"wf_feature","name":"Feature","description":"Build a feature","state":"published","publishedVersion":3,
                 "settings":{"startsWhen":{"kind":"github-label","label":"mbus:feature","repositories":["acme/app"]},
                   "runsOn":"owner-runners","spendingLimitUsd":25,"loopLimit":3,"whoCanStart":"admins","asks":"role-owners","keepExhibitsLocked":true},
                 "published":{"schema":1,"steps":[{"id":"code","name":"Build","type":"agent"},{"id":"end","name":"Done","type":"end"}],
                   "arrows":[{"from":"code","on":"passed","to":"end"}]}}
                """));

        var run = await machine.RunAsync("workflow", "show", "FEATURE");

        Assert.True(run.ExitCode == 0, run.Error);
        foreach (var text in new[] { "published version 3", "mbus:feature", "acme/app", "starter's runners", "25 USD", "Loop limit: 3",
                     "Only admins", "role owners", "Exhibits stay locked", "code: Build (agent)", "code --passed--> end" })
            Assert.Contains(text, run.Output);
        var list = await machine.RunAsync("workflow", "list", "--json");
        Assert.Equal(0, list.ExitCode);
        Assert.Equal(2, JsonNode.Parse(list.Output)!.AsArray().Count);
    }

    [Fact]
    public async Task Workflow_check_posts_the_path_and_sorts_errors_before_warnings_with_missing_severity_as_error()
    {
        using var machine = Machine();
        var file = machine.WriteHomeFile("path.json", "{\"schema\":1,\"steps\":[],\"arrows\":[]}");
        machine.Server.On(HttpMethod.Post, Root + "workflows/check", _ => Json("""
            {"ok":false,"problems":[{"kind":"unknown-role","message":"Review the role.","stepId":"code","severity":"warning"},
             {"kind":"no-end","message":"Add an end step."}]}
            """));
        var run = await machine.RunAsync("workflow", "check", file);
        Assert.NotEqual(0, run.ExitCode);
        Assert.StartsWith("error: path: Add an end step.", run.Output);
        Assert.Contains("warning: code: Review the role.", run.Output);
        Assert.Equal(1, JsonNode.Parse(machine.Server.Single(HttpMethod.Post, Root + "workflows/check").BodyText)!["schema"]!.GetValue<int>());

        machine.Server.On(HttpMethod.Post, Root + "workflows/check", _ => Json("""{"ok":true,"problems":[{"severity":"warning","message":"Review."}]}"""));
        Assert.Equal(0, (await machine.RunAsync("workflow", "check", file, "--json")).ExitCode);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("draft", true)]
    [InlineData("3", true)]
    public async Task Workflow_export_writes_the_server_yaml_exactly(string? version, bool toFile)
    {
        using var machine = Machine();
        const string yaml = "messagebusWorkflow: 1\r\nname: Fonctionnalité";
        const string route = Root + "workflows/wf_feature/export";
        machine.Server.On(HttpMethod.Get, Root + "workflows", _ => Json(Workflows))
            .On(HttpMethod.Get, route, _ => new(HttpStatusCode.OK)
            {
                Content = new StringContent(yaml, Encoding.UTF8, "application/yaml"),
            });
        var file = Path.Combine(machine.Home, "feature.workflow.yaml");
        var args = new List<string> { "workflow", "export", "fEaTuRe" };
        if (version is not null) args.AddRange(["--version", version]);
        if (toFile) args.AddRange(["--output", file]);

        var run = await machine.RunAsync(args.ToArray());

        Assert.True(run.ExitCode == 0, run.Error);
        Assert.Equal(version is null ? "" : $"?version={version}", machine.Server.Single(HttpMethod.Get, route).Uri.Query);
        if (toFile)
        {
            Assert.Equal(file, run.Output.Trim());
            Assert.Equal(Encoding.UTF8.GetBytes(yaml), await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.Equal(yaml, run.Output);
        }
    }

    [Fact]
    public async Task Workflow_import_sends_markdown_to_the_resolved_workflow_and_reports_a_saved_draft_with_problems()
    {
        using var machine = Machine();
        const string content = "# Fonctionnalité\r\n\r\n## Required steps\r\n";
        const string response = """
            {"workflow":{"id":"wf_draft","name":"Draft"},"problems":[
             {"severity":"warning","stepId":"build","message":"Review the role."},{"message":"Add an end step."}]}
            """;
        var file = machine.WriteHomeFile("workflow.md", content);
        machine.Server.On(HttpMethod.Get, Root + "workflows", _ => Json(Workflows))
            .On(HttpMethod.Post, Root + "workflows/import", _ => Json(response));

        var run = await machine.RunAsync("workflow", "import", file, "--into", "dRaFt");

        Assert.True(run.ExitCode == 0, run.Error);
        Assert.StartsWith($"Saved draft Draft: {FakeServer.Url}/workflows/wf_draft/edit{Environment.NewLine}", run.Output);
        Assert.Contains($"error: path: Add an end step.{Environment.NewLine}warning: build: Review the role.", run.Output);
        var body = JsonNode.Parse(machine.Server.Single(HttpMethod.Post, Root + "workflows/import").BodyText)!.AsObject();
        Assert.Equal(content, (string?)body["content"]);
        Assert.Equal("wf_draft", (string?)body["workflowId"]);
        Assert.False(body.ContainsKey("name"));
    }

    [Fact]
    public async Task Workflow_import_reports_the_servers_file_error()
    {
        using var machine = Machine();
        var file = machine.WriteHomeFile("workflow.yaml", "messagebusWorkflow: 2\n");
        machine.Server.On(HttpMethod.Post, Root + "workflows/import", _ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"This file is from a newer version of messagebus.ai."}""",
                Encoding.UTF8, "application/json"),
        });

        var run = await machine.RunAsync("workflow", "import", file);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal("This file is from a newer version of messagebus.ai.", run.Error.Trim());
        Assert.Empty(run.Output);
    }

    [Theory]
    [InlineData(32768, true)]
    [InlineData(32769, false)]
    public async Task Workflow_import_enforces_the_byte_limit_and_returns_the_response_in_json_mode(int length, bool fits)
    {
        using var machine = Machine();
        const string response = """{"workflow":{"id":"wf_new","name":"New workflow"},"problems":[]}""";
        var content = new string('é', length);
        var file = machine.WriteHomeFile("workflow.yaml", content);
        machine.Server.On(HttpMethod.Post, Root + "workflows/import", _ => Json(response));

        var run = await machine.RunAsync("workflow", "import", file, "--name", "New workflow", "--json");

        if (fits)
        {
            Assert.True(run.ExitCode == 0, run.Error);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(response), JsonNode.Parse(run.Output)));
            var body = JsonNode.Parse(machine.Server.Single(HttpMethod.Post, Root + "workflows/import").BodyText)!.AsObject();
            Assert.Equal(content, (string?)body["content"]);
            Assert.Equal("New workflow", (string?)body["name"]);
            Assert.False(body.ContainsKey("workflowId"));
        }
        else
        {
            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("64 KiB", run.Error);
            Assert.DoesNotContain(machine.Server.Requests, r => r.Method == HttpMethod.Post);
        }
    }

    [Fact]
    public async Task Workflow_export_reports_the_servers_not_found_message()
    {
        using var machine = Machine();
        machine.Server.On(HttpMethod.Get, Root + "workflows", _ => Json(Workflows))
            .On(HttpMethod.Get, Root + "workflows/wf_feature/export", _ => new(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":"Workflow or version not found."}""", Encoding.UTF8, "application/json"),
            });

        var run = await machine.RunAsync("workflow", "export", "Feature", "--version", "draft");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal("Workflow or version not found.", run.Error.Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workflow_file_commands_distinguish_an_absent_capability_from_missing_file_routes(bool advertised)
    {
        using var machine = Machine(advertised);
        machine.Server.On(HttpMethod.Get, Root + "workflows", _ => Json(Workflows))
            .On(HttpMethod.Get, Root + "workflows/wf_feature/export", _ => new(HttpStatusCode.NotFound))
            .On(HttpMethod.Post, Root + "workflows/import", _ => new(HttpStatusCode.NotFound));
        var file = machine.WriteHomeFile("workflow.yaml", "messagebusWorkflow: 1\n");

        foreach (var args in new[] { new[] { "workflow", "export", "Feature" }, ["workflow", "import", file] })
        {
            var run = await machine.RunAsync(args);
            Assert.NotEqual(0, run.ExitCode);
            Assert.Empty(run.Output);
            Assert.Equal(advertised ? "This server can't export or import workflow files yet."
                : "Workflows are not switched on for this workspace.", run.Error.Trim());
        }
        if (advertised)
        {
            machine.Server.Single(HttpMethod.Get, Root + "workflows/wf_feature/export");
            machine.Server.Single(HttpMethod.Post, Root + "workflows/import");
        }
        else
        {
            Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.StartsWith(Root, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("unsupported")]
    [InlineData("tampered")]
    [InlineData("unsafe-name")]
    public async Task Open_decrypts_workspace_exhibits_and_never_saves_partial_or_unsupported_content(string format)
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MachineKeyReason);
        using var machine = Machine();
        using var device = KeyPair.GenerateP256();
        using var workspace = KeyPair.GenerateP256();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(store.Load() with { MachineId = "mch_test", KeyStore = "file" });
        store.SaveKeyPem(device.ExportPem());
        var deviceInfo = Info(device, "ek_device", KeyScope.Machine, "mch_test");
        var workspaceInfo = Info(workspace, "ek_workspace", KeyScope.Workspace, null);
        var grant = GrantCrypto.Create("ws_test", workspaceInfo, workspace, deviceInfo, deviceInfo, device, "test:starter", machine.Time.GetUtcNow());
        await using var cipher = new MemoryStream();
        var sealedResult = await MessageCrypto.SealAsync(new MemoryStream(Encoding.UTF8.GetBytes("a local document")), cipher, workspaceInfo, TestContext.Current.CancellationToken);
        var payload = Encoding.UTF8.GetBytes((format == "unsupported" ? "other/1\n" : "mbus-sealed-exhibit/1\n") +
            MessageBusJson.Serialize(sealedResult.Encryption) + "\n").Concat(cipher.ToArray()).ToArray();
        if (format == "tampered") payload[^1] ^= 1;
        machine.Server.On(HttpMethod.Get, "/api/caller", _ => Json("""{"subjectId":"test:starter","permissions":[]}"""))
            .On(HttpMethod.Get, Root + "keys", _ => FakeServer.Json<IReadOnlyList<PublicKeyInfo>>([deviceInfo, workspaceInfo]))
            .On(HttpMethod.Get, Root + "keys/grants", _ => FakeServer.Json<IReadOnlyList<KeyGrant>>([grant]))
            .On(HttpMethod.Get, Root + "jobs/job_test/exhibits", _ => Json(new JsonArray(
                new JsonObject { ["id"] = "ex_locked", ["name"] = format == "unsafe-name" ? "../outside.txt" : "notes.txt", ["locked"] = true },
                new JsonObject { ["id"] = "ex_open", ["name"] = "diagram.svg", ["locked"] = false }).ToJsonString()))
            .On(HttpMethod.Get, Root + "jobs/job_test/exhibits/ex_locked", _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var folder = Path.Combine(machine.Home, "exhibits");

        var run = await machine.RunAsync("job", "open", "job_test", "--out", folder, "--json");

        if (format == "valid")
        {
            Assert.True(run.ExitCode == 0, run.Error);
            Assert.Equal("a local document", await File.ReadAllTextAsync(Path.Combine(folder, "ex_locked-notes.txt"), TestContext.Current.CancellationToken));
            var results = JsonNode.Parse(run.Output)!.AsArray();
            Assert.Equal(FakeServer.Url + "/jobs/job_test", (string?)results[1]!["url"]);
            Assert.Single(Directory.GetFiles(folder));
            var repeated = await machine.RunAsync("job", "open", "job_test", "--out", folder);
            Assert.NotEqual(0, repeated.ExitCode);
            Assert.Equal("a local document", await File.ReadAllTextAsync(Path.Combine(folder, "ex_locked-notes.txt"), TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(folder));
        }
        else
        {
            Assert.NotEqual(0, run.ExitCode);
            Assert.Empty(Directory.GetFiles(folder));
            if (format == "unsupported") Assert.Contains("Unsupported locked exhibit format", run.Error);
            if (format == "unsafe-name") Assert.Contains("unsafe exhibit file name", run.Error);
        }
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.EndsWith("/ex_open", StringComparison.Ordinal));
    }

    private static PublicKeyInfo Info(KeyPair key, string id, KeyScope scope, string? owner) => new()
    {
        KeyId = id, Scope = scope, Owner = owner, Version = 1, Algorithm = key.Algorithm, PublicKey = key.PublicKey.ToBase64(),
        Fingerprint = key.PublicKey.Fingerprint, CreatedAt = DateTimeOffset.Parse("2026-11-13T09:00:00Z"),
    };

    private static TestMachine Machine(bool workflows = true)
    {
        var machine = new TestMachine();
        new ConfigStore(machine.MbusFolder, "file").Save(new CliConfig { Server = FakeServer.Url, WorkspaceId = "ws_test", KeyStore = "file" });
        machine.Env["MBUS_API_KEY"] = "mbk_test";
        machine.Server.On(HttpMethod.Get, "/api/server", _ => FakeServer.Json(FakeServer.ConfiguredServerInfo with { Capabilities = workflows ? ["workflows"] : [] }));
        return machine;
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
}
