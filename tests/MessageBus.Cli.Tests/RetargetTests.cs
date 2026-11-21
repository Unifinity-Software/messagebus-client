using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Scripts;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class RetargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("tool-error")]
    [InlineData("method-not-found")]
    [InlineData("unknown-tool")]
    public async Task Advertised_but_unimplemented_handlers_refuse_before_any_content_or_key_requests(string failure)
    {
        using var machine = new TestMachine();
        using var http = Server(machine, failure);
        await using var tools = Tools(machine, http);
        var call = await Call(tools, Input());
        var error = Assert.Throws<McpToolException>(() => McpToolCatalog.ReadResult<RetargetResult>(call));
        Assert.Equal(McpErrorCodes.NotFound, error.Error.Code);
        Assert.Single(Calls(machine));
        Assert.All(machine.Server.Requests, r => Assert.Equal("/mcp", r.Uri.AbsolutePath));
        Assert.True((bool?)Calls(machine)[0]["arguments"]!["dryRun"]);
    }

    [Fact]
    public async Task Explicit_preview_needing_reseal_never_reads_keys_content_or_uploads()
    {
        using var machine = new TestMachine();
        using var http = Server(machine, needsReseal: true);
        await using var tools = Tools(machine, http);
        var result = McpToolCatalog.ReadResult<RetargetResult>(await Call(tools, Input() with { DryRun = true }));
        Assert.True(result.NeedsReseal);
        Assert.True(result.DryRun);
        Assert.Single(Calls(machine));
        Assert.All(machine.Server.Requests, r => Assert.Equal("/mcp", r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task A_committed_operation_returns_its_historical_result_without_resealing_or_mutating_again()
    {
        using var machine = new TestMachine();
        using var http = Server(machine, needsReseal: true, alreadyApplied: true);
        await using var tools = Tools(machine, http);

        var result = McpToolCatalog.ReadResult<RetargetResult>(await Call(tools, Input()));

        Assert.True(result.AlreadyApplied);
        Assert.False(result.DryRun);
        Assert.Equal("msg_new", result.MessageId);
        Assert.Single(Calls(machine));
        Assert.All(machine.Server.Requests, r => Assert.Equal("/mcp", r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task Mutation_preserves_operation_task_pin_raw_target_and_server_status()
    {
        using var machine = new TestMachine();
        using var http = Server(machine);
        await using var tools = Tools(machine, http);
        var args = new Dictionary<string, JsonElement>
        {
            ["threadId"] = JsonSerializer.SerializeToElement("thr_task"),
            ["to"] = JsonSerializer.SerializeToElement("capabilities:os:linux,tool:dotnet"),
            ["expectedTaskMessageId"] = JsonSerializer.SerializeToElement("msg_old"),
            ["operationId"] = JsonSerializer.SerializeToElement("operation-test"),
        };
        var result = McpToolCatalog.ReadResult<RetargetResult>(await tools.CallAsync("mbus-cli", McpToolNames.Retarget, args, Ct));
        Assert.Equal(ThreadStatus.Working, result.Status);
        var calls = Calls(machine);
        Assert.Equal(2, calls.Count);
        foreach (var call in calls)
        {
            Assert.Equal("operation-test", (string?)call["arguments"]!["operationId"]);
            Assert.Equal("msg_old", (string?)call["arguments"]!["expectedTaskMessageId"]);
            Assert.Equal("capabilities", (string?)call["arguments"]!["to"]!["kind"]);
        }
        Assert.True((bool?)calls[0]["arguments"]!["dryRun"]);
        Assert.NotEqual(true, (bool?)calls[1]["arguments"]!["dryRun"]);
    }

    [Theory]
    [InlineData(null, "msg_old", "operationId")]
    [InlineData("operation-test", null, "expectedTaskMessageId")]
    public async Task Mutations_require_retry_and_staleness_pins_before_network(string? operation, string? expected, string field)
    {
        using var machine = new TestMachine();
        using var http = Server(machine);
        await using var tools = Tools(machine, http);
        var call = await Call(tools, Input() with { OperationId = operation, ExpectedTaskMessageId = expected });
        var error = Assert.Throws<McpToolException>(() => McpToolCatalog.ReadResult<RetargetResult>(call));
        Assert.Equal(field, error.Error.Field);
        Assert.Empty(machine.Server.Requests);
    }

    [Fact]
    public async Task Stale_preview_refuses_before_resealing()
    {
        using var machine = new TestMachine();
        using var http = Server(machine, needsReseal: true, previous: "msg_changed");
        await using var tools = Tools(machine, http);
        var call = await Call(tools, Input());
        var error = Assert.Throws<McpToolException>(() => McpToolCatalog.ReadResult<RetargetResult>(call));
        Assert.Equal(McpErrorCodes.Conflict, error.Error.Code);
        Assert.Single(Calls(machine));
        Assert.All(machine.Server.Requests, r => Assert.Equal("/mcp", r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task Missing_old_key_is_actionable_and_never_uploads_a_replacement()
    {
        using var machine = new TestMachine();
        using var http = Server(machine, needsReseal: true);
        using var pair = KeyPair.GenerateP256();
        var key = new PublicKeyInfo
        {
            KeyId = "ek_destination", Scope = KeyScope.Role, Owner = "reviewer", Version = 1,
            Algorithm = pair.PublicKey.Algorithm, PublicKey = pair.PublicKey.ToBase64(), Fingerprint = pair.PublicKey.Fingerprint,
            CreatedAt = machine.Time.GetUtcNow(),
        };
        var sealedBody = BodyCrypto.Seal(new MessageBody { Summary = "Do the work" }, key with { KeyId = "ek_missing" }).ToSealedBody();
        var envelope = new MessageEnvelope
        {
            Id = "msg_old", ThreadId = "thr_task", WorkspaceId = "ws_test", Type = "task.implement", Source = "person://ws_test/person_test",
            Target = new RoleTarget { Role = "developer" }, Sender = new SenderIdentity { Kind = SenderKind.Person },
            Time = machine.Time.GetUtcNow(), Body = sealedBody.Body, Encryption = sealedBody.Encryption,
        };
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/threads/thr_task/messages", _ =>
            new(HttpStatusCode.OK) { Content = new StringContent("[" + MessageBusJson.Serialize(envelope) + "]", Encoding.UTF8, "application/json") });
        machine.Server.On(HttpMethod.Get, "/api/caller", _ => new(HttpStatusCode.OK) { Content = new StringContent("{\"permissions\":[]}", Encoding.UTF8, "application/json") });
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/keys", _ => FakeServer.Json<IReadOnlyList<PublicKeyInfo>>([key]));
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/keys/rotation", _ => FakeServer.Json<IReadOnlyList<KeyRotationState>>([new() { Scope = key.Scope, Owner = key.Owner, Key = key }]));
        await using var tools = Tools(machine, http);

        var call = await Call(tools, Input());

        var error = Assert.Throws<McpToolException>(() => McpToolCatalog.ReadResult<RetargetResult>(call));
        Assert.Equal(McpErrorCodes.Forbidden, error.Error.Code);
        Assert.Contains("mbus retarget", error.Message);
        Assert.Contains("original role/workspace keys", error.Message);
        Assert.Single(Calls(machine));
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.Contains("attachments") || r.Method == HttpMethod.Put);
    }

    [Theory]
    [InlineData("role:reviewer", "role")]
    [InlineData("agent:agt_reviewer", "agent")]
    [InlineData("capabilities:os:linux,tool:dotnet", "capabilities")]
    public async Task Cli_uses_shared_tool_parser_and_prints_the_result(string target, string kind)
    {
        using var machine = new TestMachine();
        using var http = Server(machine);
        new ConfigStore(machine.MbusFolder, "file").Save(new CliConfig { Server = FakeServer.Url, WorkspaceId = "ws_test" });
        machine.Env["MBUS_API_KEY"] = "mbk_test";
        var run = await machine.RunAsync("retarget", "thr_task", "--to", target, "--expected-task-message-id", "msg_old", "--operation-id", "operation-test", "--json");
        Assert.Equal(ScriptExitCodes.Ok, run.ExitCode);
        Assert.Equal("working", (string?)JsonNode.Parse(run.Output)!["status"]);
        Assert.All(Calls(machine), c => Assert.Equal(kind, (string?)c["arguments"]!["to"]!["kind"]));
    }

    [Fact]
    public async Task Cli_preserves_structured_refusal_and_exit_code()
    {
        using var machine = new TestMachine();
        using var http = Server(machine, "conflict");
        new ConfigStore(machine.MbusFolder, "file").Save(new CliConfig { Server = FakeServer.Url, WorkspaceId = "ws_test" });
        machine.Env["MBUS_API_KEY"] = "mbk_test";
        var run = await machine.RunAsync("retarget", "thr_task", "--to", "role:reviewer", "--dry-run", "--json");
        Assert.Equal(ScriptExitCodes.Conflict, run.ExitCode);
        Assert.Equal("Already picked up.", (string?)JsonNode.Parse(run.Output)!["error"]!["message"]);
    }

    private static RetargetInput Input() => new()
    {
        ThreadId = "thr_task", To = new RoleTarget { Role = "reviewer" },
        OperationId = "operation-test", ExpectedTaskMessageId = "msg_old",
    };

    private static Task<CallToolResult> Call(LocalTools tools, RetargetInput input) => tools.CallAsync("mbus-cli", McpToolNames.Retarget,
        JsonSerializer.SerializeToElement(input, MessageBusJson.Options).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()), Ct);

    private static LocalTools Tools(TestMachine machine, HttpClient http) => new(new LocalServerOptions
    {
        Http = http, Links = http, WorkspaceId = "ws_test", ConfigDirectory = machine.Home,
        Input = Stream.Null, Output = Stream.Null, Time = machine.Time,
    });

    private static List<JsonNode> Calls(TestMachine machine) => machine.Server.Requests.Where(r => r.Uri.AbsolutePath == "/mcp")
        .Select(r => JsonNode.Parse(r.BodyText)!).Where(n => (string?)n["method"] == "tools/call").Select(n => n["params"]!).ToList();

    private static HttpClient Server(TestMachine machine, string? failure = null, bool needsReseal = false, string previous = "msg_old", bool alreadyApplied = false)
    {
        machine.Server.On(HttpMethod.Post, "/mcp", request =>
        {
            var rpc = JsonNode.Parse(request.BodyText)!;
            if (rpc["id"] is not { } id) return new(HttpStatusCode.Accepted);
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
            JsonNode result;
            switch ((string?)rpc["method"])
            {
                case "initialize":
                    result = new JsonObject
                    {
                        ["protocolVersion"] = rpc["params"]!["protocolVersion"]!.DeepClone(),
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
                    };
                    break;
                case "tools/list":
                    result = new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "retarget", ["inputSchema"] = new JsonObject { ["type"] = "object" } }) };
                    break;
                case "tools/call":
                    if (failure is "method-not-found" or "unknown-tool")
                    {
                        response["error"] = new JsonObject { ["code"] = failure == "method-not-found" ? -32601 : -32602, ["message"] = "Unknown tool retarget" };
                        return Json(response);
                    }
                    var args = rpc["params"]!["arguments"]!;
                    var preview = (bool?)args["dryRun"] == true;
                    var value = failure is "tool-error" or "conflict"
                        ? new JsonObject { ["code"] = failure == "conflict" ? "conflict" : "not-found", ["message"] = failure == "conflict" ? "Already picked up." : "Unknown tool retarget" }
                        : new JsonObject
                        {
                            ["threadId"] = "thr_task", ["messageId"] = preview && !alreadyApplied ? "msg_old" : "msg_new", ["previousMessageId"] = previous,
                            ["from"] = new JsonObject { ["kind"] = "role", ["role"] = "developer" }, ["to"] = args["to"]!.DeepClone(),
                            ["status"] = preview ? "queued" : "working", ["dryRun"] = preview && !alreadyApplied, ["needsReseal"] = needsReseal,
                            ["alreadyApplied"] = alreadyApplied,
                        };
                    result = new JsonObject { ["isError"] = failure is not null, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = value.ToJsonString() }) };
                    break;
                default: throw new InvalidOperationException("Unexpected RPC method.");
            }
            response["result"] = result;
            return Json(response);
        });
        return new HttpClient(machine.Server, disposeHandler: false) { BaseAddress = new Uri(FakeServer.Url) };
    }

    private static HttpResponseMessage Json(JsonNode value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
}
