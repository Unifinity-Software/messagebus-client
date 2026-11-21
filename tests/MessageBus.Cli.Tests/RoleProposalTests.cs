using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Security;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Roles;
using MessageBus.Mcp;
using MessageBus.Tests.Shared;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class RoleProposalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string ProposalPath = "/api/w/ws_test/role-proposals";

    [Theory]
    [InlineData("create", "{\"timeoutSeconds\":7200}")]
    [InlineData("update", "{\"timeoutSeconds\":7200}")]
    [InlineData("create", "{\"permissionProfile\":{\"canPush\":false,\"allowedHosts\":[]},\"models\":{\"allowed\":[\"example-model\"]}}")]
    [InlineData("update", "{\"permissionProfile\":{\"canPush\":false,\"allowedHosts\":[]},\"models\":{\"allowed\":[\"example-model\"]}}")]
    public async Task Partial_run_settings_reach_the_server_without_materializing_baseline_or_inherited_fields(string operation, string run)
    {
        using var machine = new TestMachine();
        Capability(machine, 1);
        var record = Record(machine);
        new SessionRecords(machine.MbusFolder).Save(record);
        machine.Server.On(HttpMethod.Post, ProposalPath, request =>
        {
            using var body = JsonDocument.Parse(request.BodyText);
            using var expected = JsonDocument.Parse(run);
            var outgoing = body.RootElement.GetProperty("definition").GetProperty("runEnvironment");
            Assert.True(JsonElement.DeepEquals(expected.RootElement, outgoing), request.BodyText);
            // These are the values visible to server normalization: it can still select the safe
            // create baseline or inherit the captured update settings for each absent nested field.
            var settings = MessageBusJson.Deserialize<SubmitRoleProposalRequest>(request.BodyText)!.Definition.RunEnvironment!;
            Assert.Null(settings.Checkout);
            Assert.Null(settings.KeepAgentTools);
            Assert.Null(settings.PermissionProfile?.CanEditFiles);
            Assert.Null(settings.PermissionProfile?.CanRunCommands);
            Assert.Null(settings.PermissionProfile?.AllowedTools);
            Assert.Null(settings.Models?.Defaults);
            return FakeServer.Json(Submitted(machine));
        });
        using var http = Client(machine);
        await using var tools = new LocalTools(Options(machine, http) with { Session = record });
        var args = JsonNode.Parse(MessageBusJson.Serialize(Input()))!.AsObject();
        args["operation"] = operation;
        args["definition"]!["runEnvironment"] = JsonNode.Parse(run);
        using var document = JsonDocument.Parse(args.ToJsonString());
        var result = await tools.CallAsync("codex", McpToolNames.ProposeRole,
            document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()), Ct);
        Assert.NotEqual(true, result.IsError);
        Assert.Single(machine.Server.Requests, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == ProposalPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CLI_retry_after_accepted_but_lost_response_reuses_original_record_and_reports_inbox_on_error(bool responseTimeout)
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MachineKeyReason);
        using var machine = new TestMachine();
        Enroll(machine);
        Capability(machine, 1);
        var inboxCount = OpenInboxes(machine);
        string? accepted = null;
        var posts = 0;
        machine.Server.On(HttpMethod.Post, ProposalPath, request =>
        {
            var sent = MessageBusJson.Deserialize<SubmitRoleProposalRequest>(request.BodyText)!;
            var saved = new SessionRecords(machine.MbusFolder).Find(sent.InboxId)!;
            Assert.True(saved.RoleProposalProvenance);
            Assert.Single(saved.RoleProposalRetryKeys);
            if (++posts == 1)
            {
                accepted = request.BodyText; // Server committed before losing the response.
                if (responseTimeout) throw new TaskCanceledException("Response timed out after acceptance");
                throw new HttpRequestException("Response lost after acceptance");
            }
            Assert.Equal(accepted, request.BodyText);
            return FakeServer.Json(Submitted(machine));
        });
        var file = machine.WriteHomeFile("proposal.json", MessageBusJson.Serialize(Input()));
        var command = new[] { "roles", "propose", "--input", file, "--agent", "codex", "--json" };
        var first = await machine.RunAsync(command);
        Assert.NotEqual(0, first.ExitCode);
        var failure = JsonNode.Parse(first.Output)!;
        Assert.Equal(McpErrorCodes.Unavailable, (string?)failure["error"]!["code"]);
        Assert.Equal("inb_1", (string?)failure["coordinatorInboxId"]);
        var second = await machine.RunAsync(command);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal("rpr_test", (string?)JsonNode.Parse(second.Output)!["proposalId"]);
        Assert.Equal("inb_1", (string?)JsonNode.Parse(second.Output)!["coordinatorInboxId"]);
        Assert.Equal(1, inboxCount());
        Assert.Equal(2, posts);
    }

    [Fact]
    public async Task Submitted_provenance_survives_host_exit_expiry_and_unrelated_inbox_pruning_for_CLI_reads_and_waits()
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MachineKeyReason);
        using var machine = new TestMachine();
        Enroll(machine);
        Capability(machine, 1);
        OpenInboxes(machine);
        machine.Server.On(HttpMethod.Post, ProposalPath, _ => FakeServer.Json(Submitted(machine)));
        var file = machine.WriteHomeFile("proposal.json", MessageBusJson.Serialize(Input()));
        Assert.Equal(0, (await machine.RunAsync("roles", "propose", "--input", file, "--json")).ExitCode);
        var sessions = new SessionRecords(machine.MbusFolder);
        var original = sessions.Find("inb_1")!;
        // Simulate the original CLI host exiting; advance fake server/client time past expiry.
        sessions.Save(original with { ProcessId = null, ProcessStartedAt = null, HostProcessId = null, HostProcessStartedAt = null });
        var unretained = Record(machine) with { InboxId = "inb_unretained", ExpiresAt = original.ExpiresAt };
        sessions.Save(unretained);
        machine.Time.Advance(TimeSpan.FromDays(2));
        using var http = Client(machine);
        await using (var unrelated = new LocalTools(Options(machine, http)))
            Assert.NotEqual(true, (await unrelated.CallAsync("codex", McpToolNames.OpenInbox, null, Ct)).IsError);
        Assert.Null(sessions.Find(unretained.InboxId)); // Opening the unrelated inbox really pruned expired records.
        var retained = sessions.Find(original.InboxId)!;
        Assert.Equal(original.CoordinatorRecordId, retained.CoordinatorRecordId);
        Assert.True(retained.RoleProposalProvenance);
        var detail = Detail(original) with { Status = RoleProposalStatus.Applying };
        machine.Server.On(HttpMethod.Get, ProposalPath + "/rpr_test", _ => FakeServer.Json(detail));
        machine.Server.On(HttpMethod.Get, ProposalPath + "/rpr_test/wait", _ => FakeServer.Json(detail with { Status = RoleProposalStatus.Rejected }));
        var read = await machine.RunAsync("roles", "get-proposal", "rpr_test", "--inbox", original.InboxId, "--json");
        Assert.Equal(0, read.ExitCode);
        Assert.Equal("applying", (string?)JsonNode.Parse(read.Output)!["status"]);
        var wait = await machine.RunAsync("roles", "wait-proposal", "rpr_test", "--inbox", original.InboxId, "--timeout", "0", "--json");
        Assert.Equal(0, wait.ExitCode);
        Assert.Equal("rejected", (string?)JsonNode.Parse(wait.Output)!["status"]);
    }

    [Theory]
    [InlineData("{\"name\":\"reviewer\",\"displayName\":\"Reviewer\",\"instructions\":\"Review\",\"access\":{\"unknownGrant\":true}}")]
    [InlineData("{\"name\":\"reviewer\",\"name\":\"other\",\"displayName\":\"Reviewer\",\"instructions\":\"Review\"}")]
    [InlineData("{\"name\":\"reviewer\",\"displayName\":\"Reviewer\",\"instructions\":\"Review\",\"runEnvironment\":{\"permissionProfile\":{\"network\":\"unknown\"}}}")]
    public async Task Invalid_definitions_are_rejected_without_dropping_unknown_or_duplicate_properties(string definition)
    {
        using var machine = new TestMachine();
        using var http = Client(machine);
        await using var tools = new LocalTools(Options(machine, http));
        using var document = JsonDocument.Parse("{\"operation\":\"create\",\"rationale\":\"Review\",\"idempotencyKey\":\"retry_test\",\"definition\":" + definition + "}");
        var args = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        Assert.Equal(McpErrorCodes.InvalidInput, Error(await tools.CallAsync("codex", McpToolNames.ProposeRole, args, Ct)).Code);
        Assert.Empty(machine.Server.Requests);
    }

    [Fact]
    public async Task New_interactive_hosts_own_distinct_original_records_and_cannot_adopt_one_through_coordination_arguments()
    {
        using var machine = new TestMachine();
        Capability(machine, 1);
        var inboxCount = 0;
        FakeMcp(machine, (name, _) => name switch
        {
            McpToolNames.OpenInbox => new JsonObject
            {
                ["inboxId"] = "inb_" + ++inboxCount, ["agentId"] = "agt_test",
                ["replyTo"] = new JsonObject { ["kind"] = "inbox", ["inboxId"] = "inb_" + inboxCount },
                ["expiresAt"] = machine.Time.GetUtcNow().AddHours(24),
            },
            McpToolNames.EnsureProject => JsonNode.Parse("""
                {"project":{"projectId":"prj_test","workspaceId":"ws_test","key":"gh:acme/api#42","title":"Example",
                "startedBy":{"kind":"agent"},"status":"active","createdAt":"2026-11-13T09:00:00Z"},
                "coordinationAcquired":true,"coordinatorInboxId":"inb_1"}
                """)!,
            _ => throw new InvalidOperationException(name),
        });
        machine.Server.On(HttpMethod.Post, ProposalPath, _ => FakeServer.Json(new RoleProposalSubmitted
        {
            ProposalId = "rpr_test", Status = RoleProposalStatus.Pending, ExpiresAt = machine.Time.GetUtcNow().AddHours(24), ReviewUrl = "https://example.com/review",
        }, HttpStatusCode.Created));
        using var http = Client(machine);
        for (var i = 0; i < 2; i++)
        {
            await using var tools = new LocalTools(Options(machine, http) with { WorkingDirectory = machine.Home });
            var proposed = await tools.CallAsync("codex", McpToolNames.ProposeRole, Args(Input()), Ct);
            Assert.True(proposed.IsError != true, proposed.StructuredContent?.GetRawText());
        }
        var sessions = new SessionRecords(machine.MbusFolder);
        var first = sessions.Find("inb_1")!;
        var second = sessions.Find("inb_2")!;
        Assert.Equal(ProposalAuthority.Interactive, first.OriginAuthority);
        Assert.Equal("mch_test", first.MachineId);
        Assert.NotEqual(first.CoordinatorRecordId, second.CoordinatorRecordId);
        await using var unrelated = new LocalTools(Options(machine, http));
        var claims = JsonDocument.Parse("""{"key":"gh:acme/api#42","title":"Example","claimedWorkItems":["gh:acme/api#42"],"coordinatorInboxId":"inb_1"}""")
            .RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        Assert.NotEqual(true, (await unrelated.CallAsync("codex", McpToolNames.EnsureProject, claims, Ct)).IsError);
        var before = machine.Server.Requests.Count;
        Assert.Equal(McpErrorCodes.Forbidden, Error(await unrelated.CallAsync("codex", McpToolNames.ProposeRole, Args(Input()), Ct)).Code);
        Assert.Equal(before, machine.Server.Requests.Count);
    }

    [Theory]
    [InlineData(ProposalAuthority.Worker, false)]
    [InlineData(ProposalAuthority.Worker, true)]
    [InlineData(ProposalAuthority.Unknown, true)]
    public async Task Worker_and_unknown_hosts_refuse_discovery_and_forced_calls_before_network(ProposalAuthority authority, bool wildcard)
    {
        using var machine = new TestMachine();
        using var http = Client(machine);
        var scope = new RunToolScope { DeliveryId = "dlv_test", ThreadId = "thr_test", Root = machine.Home,
            Tools = wildcard ? null : McpToolNames.All.ToHashSet(), ProposalAuthority = authority };
        await using var tools = new LocalTools(Options(machine, http) with { ProposalAuthority = authority, Scope = scope });
        Assert.DoesNotContain(await tools.ListToolsAsync(null, Ct), t => McpToolNames.IsRoleProposal(t.Name));
        foreach (var name in new[] { McpToolNames.ProposeRole, McpToolNames.GetRoleProposal, McpToolNames.WaitRoleProposal })
        {
            Assert.Equal(McpErrorCodes.Forbidden, scope.Check(name, null, new HashSet<string>())?.Code);
            var result = await tools.CallAsync("codex", name, null, Ct);
            Assert.Equal(McpErrorCodes.Forbidden, Error(result).Code);
        }
        Assert.Empty(machine.Server.Requests);
    }

    [Theory]
    [InlineData("MBUS_DELIVERY_FILE", "missing.json")]
    [InlineData("MBUS_RUN_TOOLS", "")]
    [InlineData("MBUS_SETTLE_FILE", "settle.json")]
    public async Task Missing_runner_context_cannot_become_an_interactive_CLI_or_MCP(string marker, string value)
    {
        using var machine = new TestMachine();
        machine.Env[marker] = value;
        var cli = await machine.RunAsync("roles", "get-proposal", "rpr_test", "--json");
        Assert.Equal(4, cli.ExitCode);
        Assert.Contains("tools pipe", cli.Error);
        var mcp = await machine.RunAsync("mcp");
        Assert.Equal(1, mcp.ExitCode);
        Assert.Contains("tools pipe", mcp.Error);
        Assert.Empty(machine.Server.Requests);
        using var http = Client(machine);
        Assert.Equal(ProposalAuthority.Unknown, LocalServer.Options(http, "ws_test", "mch_test", machine.MbusFolder,
            Stream.Null, Stream.Null, TextWriter.Null, "1", machine.CreateHost().GetEnvironmentVariable).ProposalAuthority);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task Missing_or_unknown_capability_hides_tools_and_refuses_calls_without_role_CRUD(int? version)
    {
        using var machine = new TestMachine();
        Capability(machine, version);
        using var http = Client(machine);
        await using var tools = new LocalTools(Options(machine, http));
        Assert.DoesNotContain(await tools.ListToolsAsync(null, Ct), t => McpToolNames.IsRoleProposal(t.Name));
        var result = await tools.CallAsync("codex", McpToolNames.GetRoleProposal, Args(new GetRoleProposalInput { ProposalId = "rpr_test" }), Ct);
        Assert.Equal(McpErrorCodes.Unsupported, Error(result).Code);
        Assert.All(machine.Server.Requests, r => Assert.Equal("/api/server", r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task Own_expired_inbox_still_reads_waits_and_submits_with_original_binding_and_preserves_errors()
    {
        using var machine = new TestMachine();
        Capability(machine, 1);
        var record = Record(machine) with { ExpiresAt = machine.Time.GetUtcNow().AddHours(-1) };
        new SessionRecords(machine.MbusFolder).Save(record);
        var detail = Detail(record);
        machine.Server.On(HttpMethod.Get, ProposalPath + "/rpr_test", _ => FakeServer.Json(detail));
        machine.Server.On(HttpMethod.Get, ProposalPath + "/rpr_test/wait", r =>
        {
            Assert.Contains("timeoutSeconds=0", r.Uri.Query);
            Assert.Contains("coordinatorRecordId=coord_original", r.Uri.Query);
            return FakeServer.Json(detail); // Pending at the deadline is still authoritative, not rejection.
        });
        machine.Server.On(HttpMethod.Post, ProposalPath, r =>
        {
            var body = MessageBusJson.Deserialize<SubmitRoleProposalRequest>(r.BodyText)!;
            Assert.Equal(record.InboxId, body.InboxId);
            Assert.Equal(record.CoordinatorRecordId, body.CoordinatorRecordId);
            Assert.Equal(record.AgentId, body.AgentId);
            Assert.Equal("retry_test", body.IdempotencyKey);
            Assert.Null(body.Definition.RunEnvironment); // Do not materialize permissive client defaults.
            return FakeServer.Json(new McpError { Code = "idempotency-conflict", Message = "Changed request", ProposalId = "rpr_test",
                Status = RoleProposalStatus.Pending, CurrentRevision = "review_new" }, HttpStatusCode.Conflict);
        });
        using var http = Client(machine);
        await using var tools = new LocalTools(Options(machine, http) with { Session = record });
        Assert.Contains(await tools.ListToolsAsync(null, Ct), t => t.Name == McpToolNames.ProposeRole);
        var read = await tools.CallAsync("codex", McpToolNames.GetRoleProposal, Args(new GetRoleProposalInput { ProposalId = "rpr_test" }), Ct);
        Assert.NotEqual(true, read.IsError);
        var wait = await tools.CallAsync("codex", McpToolNames.WaitRoleProposal, Args(new WaitRoleProposalInput { ProposalId = "rpr_test", TimeoutSeconds = 0 }), Ct);
        Assert.NotEqual(true, wait.IsError);
        Assert.Equal(RoleProposalStatus.Pending, MessageBusJson.Deserialize<RoleProposalDetail>(wait.StructuredContent!.Value.GetRawText())!.Status);
        var submit = await tools.CallAsync("codex", McpToolNames.ProposeRole, Args(Input()), Ct);
        var error = Error(submit);
        Assert.Equal("idempotency-conflict", error.Code);
        Assert.Equal("rpr_test", error.ProposalId);
        Assert.Equal("review_new", error.CurrentRevision);
        var unchanged = new SessionRecords(machine.MbusFolder).Find(record.InboxId)!;
        Assert.Equal(record.CoordinatorRecordId, unchanged.CoordinatorRecordId);
        Assert.Equal(record.ExpiresAt, unchanged.ExpiresAt);
        Assert.Equal(record.OriginAuthority, unchanged.OriginAuthority);
    }

    [Fact]
    public async Task A_response_for_another_coordinator_is_not_exposed_and_endpoint_rollback_is_unsupported()
    {
        using var machine = new TestMachine();
        Capability(machine, 1);
        var record = Record(machine);
        new SessionRecords(machine.MbusFolder).Save(record);
        machine.Server.On(HttpMethod.Get, ProposalPath + "/rpr_test", _ => FakeServer.Json(Detail(record) with { CoordinatorRecordId = "coord_other" }));
        using var http = Client(machine);
        await using var tools = new LocalTools(Options(machine, http) with { Session = record });
        var result = await tools.CallAsync("codex", McpToolNames.GetRoleProposal, Args(new GetRoleProposalInput { ProposalId = "rpr_test" }), Ct);
        Assert.Equal(McpErrorCodes.NotFound, Error(result).Code);
        machine.Server.On(HttpMethod.Get, ProposalPath + "/rpr_test", _ =>
        {
            Capability(machine, null);
            return new(HttpStatusCode.NotFound);
        });
        result = await tools.CallAsync("codex", McpToolNames.GetRoleProposal, Args(new GetRoleProposalInput { ProposalId = "rpr_test" }), Ct);
        Assert.Equal(McpErrorCodes.Unsupported, Error(result).Code);
        Assert.Equal(2, machine.Server.Requests.Count(r => r.Uri.AbsolutePath == "/api/server"));
    }

    [Theory]
    [InlineData(ProposalAuthority.Unknown)]
    [InlineData(ProposalAuthority.Worker)]
    public async Task Legacy_and_worker_inboxes_never_gain_interactive_origin_when_reused(ProposalAuthority origin)
    {
        using var machine = new TestMachine();
        Capability(machine, 1);
        var record = Record(machine) with { OriginAuthority = origin };
        var sessions = new SessionRecords(machine.MbusFolder);
        sessions.Save(record);
        Assert.Throws<InvalidOperationException>(() => sessions.Save(record with { OriginAuthority = ProposalAuthority.Interactive }));
        using var http = Client(machine);
        await using var tools = new LocalTools(Options(machine, http) with { Session = record });
        var result = await tools.CallAsync("codex", McpToolNames.ProposeRole, Args(Input()), Ct);
        Assert.Equal(McpErrorCodes.Forbidden, Error(result).Code);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task CLI_uses_the_same_typed_proposer_and_bounded_wait_contract()
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MachineKeyReason);
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        using var key = MachineKey.Create();
        store.Save(new CliConfig { Server = FakeServer.Url, WorkspaceId = "ws_test", MachineId = "mch_test", KeyStore = "file" });
        store.SaveKeyPem(key.ExportPrivateKeyPem());
        Capability(machine, 1);
        var record = Record(machine);
        new SessionRecords(machine.MbusFolder).Save(record);
        machine.Server.On(HttpMethod.Post, ProposalPath, _ => FakeServer.Json(new RoleProposalSubmitted
        {
            ProposalId = "rpr_test", Status = RoleProposalStatus.Pending, ExpiresAt = record.ExpiresAt, ReviewUrl = "https://example.com/review",
        }, HttpStatusCode.Created));
        machine.Server.On(HttpMethod.Get, ProposalPath + "/rpr_test/wait", _ => FakeServer.Json(Detail(record)));
        var file = machine.WriteHomeFile("proposal.json", MessageBusJson.Serialize(Input()));
        var submit = await machine.RunAsync("roles", "propose", "--input", file, "--inbox", record.InboxId, "--json");
        Assert.Equal(0, submit.ExitCode);
        Assert.Equal(record.InboxId, (string?)JsonNode.Parse(submit.Output)!["coordinatorInboxId"]);
        var wait = await machine.RunAsync("roles", "wait-proposal", "rpr_test", "--inbox", record.InboxId, "--timeout", "0", "--json");
        Assert.Equal(0, wait.ExitCode);
        Assert.Equal("pending", (string?)JsonNode.Parse(wait.Output)!["status"]);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.Contains("/roles/", StringComparison.Ordinal));
    }

    private static HttpClient Client(TestMachine machine) => new(machine.Server, disposeHandler: false) { BaseAddress = new Uri(FakeServer.Url + "/") };
    private static RoleProposalSubmitted Submitted(TestMachine machine) => new()
    {
        ProposalId = "rpr_test", Status = RoleProposalStatus.Pending, ExpiresAt = machine.Time.GetUtcNow().AddDays(1), ReviewUrl = "https://example.com/review",
    };
    private static void Enroll(TestMachine machine)
    {
        var store = new ConfigStore(machine.MbusFolder, "file");
        using var key = MachineKey.Create();
        store.Save(new CliConfig { Server = FakeServer.Url, WorkspaceId = "ws_test", MachineId = "mch_test", KeyStore = "file" });
        store.SaveKeyPem(key.ExportPrivateKeyPem());
    }
    private static Func<int> OpenInboxes(TestMachine machine)
    {
        var count = 0;
        FakeMcp(machine, (name, _) => name == McpToolNames.OpenInbox ? new JsonObject
        {
            ["inboxId"] = "inb_" + ++count, ["agentId"] = "agt_test",
            ["replyTo"] = new JsonObject { ["kind"] = "inbox", ["inboxId"] = "inb_" + count },
            ["expiresAt"] = machine.Time.GetUtcNow().AddHours(24),
        } : throw new InvalidOperationException(name));
        return () => count;
    }
    private static void FakeMcp(TestMachine machine, Func<string, JsonNode?, JsonNode> outcome) => machine.Server.On(HttpMethod.Post, "/mcp", request =>
    {
        var message = JsonNode.Parse(request.BodyText)!;
        if (message["id"] is not { } id) return new(HttpStatusCode.Accepted);
        var envelope = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        var method = (string?)message["method"];
        if (method is not "initialize" and not "tools/call")
        {
            envelope["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
            return new(HttpStatusCode.OK) { Content = new StringContent(envelope.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
        }
        envelope["result"] = method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = message["params"]!["protocolVersion"]!.DeepClone(),
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "example", ["version"] = "1" },
            },
            "tools/call" => new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = outcome((string)message["params"]!["name"]!, message["params"]!["arguments"]).ToJsonString() }),
            },
            _ => throw new InvalidOperationException(method),
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(envelope.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    });
    private static LocalServerOptions Options(TestMachine machine, HttpClient http) => new()
    {
        Http = http, Links = http, WorkspaceId = "ws_test", MachineId = "mch_test", ConfigDirectory = machine.MbusFolder,
        Input = Stream.Null, Output = Stream.Null, Time = machine.Time, ProposalAuthority = ProposalAuthority.Interactive,
        CodexHome = Path.Combine(machine.Home, ".codex"), ClaudeHome = Path.Combine(machine.Home, ".claude"),
    };
    private static SessionRecord Record(TestMachine machine) => new()
    {
        InboxId = "inb_original", WorkspaceId = "ws_test", MachineId = "mch_test", AgentId = "agt_test", AgentKind = "codex",
        Folder = machine.Home, OriginAuthority = ProposalAuthority.Interactive, CoordinatorRecordId = "coord_original",
        CreatedAt = machine.Time.GetUtcNow(), ExpiresAt = machine.Time.GetUtcNow().AddHours(24),
    };
    private static RoleProposalDetail Detail(SessionRecord record) => new()
    {
        ProposalId = "rpr_test", Status = RoleProposalStatus.Pending, Operation = RoleProposalOperation.Create,
        CoordinatorRecordId = record.CoordinatorRecordId!, InboxId = record.InboxId, AgentId = record.AgentId!, MachineId = record.MachineId!,
        ExpiresAt = record.ExpiresAt, ReviewUrl = "https://example.com/review",
    };
    private static ProposeRoleInput Input() => new()
    {
        Operation = RoleProposalOperation.Create, Definition = new RoleDefinitionV1 { Name = "reviewer", DisplayName = "Reviewer", Instructions = "Review changes." },
        Rationale = "Review incoming changes", IdempotencyKey = "retry_test",
    };
    private static void Capability(TestMachine machine, int? version) => machine.Server.On(HttpMethod.Get, "/api/server", _ => new(HttpStatusCode.OK)
    {
        Content = new StringContent(version is null ? "{\"product\":\"messagebus.ai\"}" : """{"capabilities":{"roleProposals":{"version":VERSION,"baselineVersion":"role-proposal-readonly-v1","normalizerVersion":"role-definition-v1"}}}""".Replace("VERSION", version.ToString())),
    });
    private static IDictionary<string, JsonElement> Args<T>(T value) => JsonSerializer.SerializeToElement(value, MessageBusJson.Options.GetTypeInfo(typeof(T)))
        .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    private static McpError Error(ModelContextProtocol.Protocol.CallToolResult result)
    {
        Assert.True(result.IsError);
        return MessageBusJson.Deserialize<McpError>(result.StructuredContent!.Value.GetRawText())!;
    }
}
