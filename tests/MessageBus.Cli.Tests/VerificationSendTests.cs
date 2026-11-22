using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Scripts;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class VerificationSendTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_flags_route_required_verification_without_legacy_fallback(bool supported)
    {
        using var machine = new TestMachine();
        new ConfigStore(machine.MbusFolder, "file").Save(new CliConfig { Server = FakeServer.Url, WorkspaceId = "ws_test" });
        machine.Env["MBUS_API_KEY"] = "mbk_test";
        ConfigureService(machine.Server, supported);

        var run = await machine.RunAsync("send", "--type", "task.implement", "--to", "role:developer",
            "--summary", "Add the feature.", "--environment", "frontend", "--required-check", "test", "--required-check", "build", "--json");

        var calls = ToolCalls(machine.Server).ToArray();
        Assert.Equal(supported, run.ExitCode == ScriptExitCodes.Ok);
        if (!supported)
        {
            Assert.Empty(calls);
            return;
        }
        var call = Assert.Single(calls);
        Assert.Equal(McpToolNames.SendVerified, (string?)call["name"]);
        var args = call["arguments"]!;
        Assert.NotNull(args["sealedBody"]);
        Assert.Null(args["body"]);
        Assert.Equal("frontend", (string?)args["verification"]!["environment"]);
        Assert.Equal(["test", "build"], args["verification"]!["requiredChecks"]!.AsArray().Select(check => (string?)check));
        Assert.Equal(supported, run.ExitCode == ScriptExitCodes.Ok);
    }

    [Fact]
    public async Task A_mixed_batch_uses_one_verified_call()
    {
        using var machine = new TestMachine();
        ConfigureService(machine.Server, supported: true);
        using var http = new HttpClient(machine.Server) { BaseAddress = new Uri(FakeServer.Url) };
        await using var tools = new LocalTools(new LocalServerOptions
        {
            Http = http, Links = machine.Server.Links, WorkspaceId = "ws_test", ConfigDirectory = machine.MbusFolder,
            Input = Stream.Null, Output = Stream.Null,
        });

        var result = await tools.CallAsync(null, McpToolNames.SendMany, new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.Deserialize<JsonElement>("""
                [
                  {"to":"role:developer","type":"task.implement","body":{"summary":"First task."}},
                  {"to":"role:developer","type":"task.review","body":{"summary":"Review the code."},
                   "refs":[{"kind":"github.commit","repo":"example-org/example-web","sha":"1234567890abcdef1234567890abcdef12345678"}],
                   "verification":{"environment":"frontend","requiredChecks":["build"]}}
                ]
                """),
        }, TestContext.Current.CancellationToken);

        Assert.False(result.IsError == true);
        var call = Assert.Single(ToolCalls(machine.Server));
        Assert.Equal(McpToolNames.SendManyVerified, (string?)call["name"]);
        var messages = call["arguments"]!["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Null(messages[0]!["verification"]);
        Assert.Equal("build", (string?)messages[1]!["verification"]!["requiredChecks"]![0]);
        Assert.Equal("1234567890abcdef1234567890abcdef12345678", (string?)messages[1]!["refs"]![0]!["sha"]);
    }

    [Theory]
    [InlineData("legacy", false)]
    [InlineData("pin-only", false)]
    [InlineData("final", false)]
    [InlineData("404", false)]
    [InlineData("disabled", false)]
    [InlineData("malformed", false)]
    [InlineData("wrong-version", false)]
    [InlineData("missing-capability", false)]
    [InlineData("missing-tools", false)]
    [InlineData("legacy", true)]
    [InlineData("pin-only", true)]
    [InlineData("final", true)]
    [InlineData("404", true)]
    [InlineData("disabled", true)]
    [InlineData("malformed", true)]
    [InlineData("wrong-version", true)]
    [InlineData("missing-capability", true)]
    [InlineData("missing-tools", true)]
    public async Task Required_dispatch_negotiates_registry_and_handler_before_creating_any_children(string state, bool batch)
    {
        using var machine = new TestMachine();
        ConfigureService(machine.Server, supported: true, state);
        using var http = new HttpClient(machine.Server) { BaseAddress = new Uri(FakeServer.Url) };
        await using var tools = new LocalTools(new LocalServerOptions
        {
            Http = http, Links = machine.Server.Links, WorkspaceId = "ws_test", ConfigDirectory = machine.MbusFolder,
            Input = Stream.Null, Output = Stream.Null,
        });
        const string required = """{"to":"role:developer","type":"task.implement","body":{"summary":"Build it."},"verification":{"environment":"frontend","requiredChecks":["build"]}}""";
        var argument = batch
            ? "{\"messages\":[{\"to\":\"role:developer\",\"type\":\"task.implement\",\"body\":{\"summary\":\"Ordinary child.\"}}," + required + "]}"
            : required;
        var result = await tools.CallAsync(null, batch ? McpToolNames.SendMany : McpToolNames.Send,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argument), TestContext.Current.CancellationToken);

        var calls = ToolCalls(machine.Server).ToArray();
        Assert.Equal(state != "final", result.IsError == true);
        if (state == "final")
            Assert.Equal(batch ? McpToolNames.SendManyVerified : McpToolNames.SendVerified, (string?)Assert.Single(calls)["name"]);
        else
        {
            Assert.Empty(calls);
            Assert.DoesNotContain(machine.Server.Requests, request => request.Uri.AbsolutePath.EndsWith("/keys", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("pin-only")]
    [InlineData("final")]
    public async Task Ordinary_sends_keep_the_legacy_route_without_registry_negotiation(string state)
    {
        using var machine = new TestMachine();
        ConfigureService(machine.Server, supported: true, state);
        using var http = new HttpClient(machine.Server) { BaseAddress = new Uri(FakeServer.Url) };
        await using var tools = new LocalTools(new LocalServerOptions
        {
            Http = http, Links = machine.Server.Links, WorkspaceId = "ws_test", ConfigDirectory = machine.MbusFolder,
            Input = Stream.Null, Output = Stream.Null,
        });
        var result = await tools.CallAsync(null, McpToolNames.Send,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"to":"role:developer","type":"task.implement","body":{"summary":"Ordinary task."}}"""),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError == true);
        Assert.Equal(McpToolNames.Send, (string?)Assert.Single(ToolCalls(machine.Server))["name"]);
        Assert.DoesNotContain(machine.Server.Requests, request => request.Uri.AbsolutePath.EndsWith("/environments", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_negotiated_handler_refusal_never_falls_back_to_ordinary_sends(bool batch)
    {
        using var machine = new TestMachine();
        ConfigureService(machine.Server, supported: true, state: "handler-refusal");
        using var http = new HttpClient(machine.Server) { BaseAddress = new Uri(FakeServer.Url) };
        await using var tools = new LocalTools(new LocalServerOptions
        {
            Http = http, Links = machine.Server.Links, WorkspaceId = "ws_test", ConfigDirectory = machine.MbusFolder,
            Input = Stream.Null, Output = Stream.Null,
        });
        const string required = """{"to":"role:developer","type":"task.implement","body":{"summary":"Build it."},"verification":{"environment":"frontend","requiredChecks":["build"]}}""";
        var argument = batch
            ? "{\"messages\":[{\"to\":\"role:developer\",\"type\":\"task.implement\",\"body\":{\"summary\":\"Ordinary child.\"}}," + required + "]}"
            : required;
        var result = await tools.CallAsync(null, batch ? McpToolNames.SendMany : McpToolNames.Send,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argument), TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal(batch ? McpToolNames.SendManyVerified : McpToolNames.SendVerified,
            (string?)Assert.Single(ToolCalls(machine.Server))["name"]);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(true, null)]
    [InlineData(false, null)]
    public async Task Directory_adds_repository_discovery_only_when_requested_and_available(bool discover, bool? supported)
    {
        const string path = "/api/w/ws_test/environments";
        using var machine = new TestMachine();
        ConfigureService(machine.Server, supported: false, state: "404");
        if (supported is { } ready)
        {
            machine.Server.On(HttpMethod.Get, path, _ => Json(JsonNode.Parse($$"""
                {
                  "registryVersion": 1,
                  "requiredTaskRequestsSupported": {{(ready ? "true" : "false")}},
                  "recipes": [{"id":"node-npm-ci","revision":1,"nodeMajor":24,"checks":["test","typecheck","build"]}],
                  "environments": [{"id":"frontend","version":"v1","recipeId":"node-npm-ci","recipeRevision":1,"approvedSharedMachineCount":0}]
                }
                """)!));
        }
        using var http = new HttpClient(machine.Server) { BaseAddress = new Uri(FakeServer.Url) };
        http.DefaultRequestHeaders.Add("Authorization", "MbusKey mbk_test");
        await using var tools = new LocalTools(new LocalServerOptions
        {
            Http = http, Links = machine.Server.Links, WorkspaceId = "ws_test", ConfigDirectory = machine.MbusFolder,
            Input = Stream.Null, Output = Stream.Null,
        });
        var ct = TestContext.Current.CancellationToken;
        var arguments = new Dictionary<string, JsonElement> { ["role"] = JsonSerializer.SerializeToElement("developer") };
        if (discover) arguments["verificationRepositoryId"] = JsonSerializer.SerializeToElement("github:12345");

        var result = await tools.CallAsync(null, McpToolNames.Directory, arguments, ct);

        var sent = Assert.Single(ToolCalls(machine.Server));
        Assert.Equal("directory", (string?)sent["name"]);
        Assert.Equal("developer", (string?)sent["arguments"]!["role"]);
        Assert.Null(sent["arguments"]!["verificationRepositoryId"]);
        var requests = machine.Server.Requests.Where(request => request.Uri.AbsolutePath == path);
        if (discover)
        {
            var request = Assert.Single(requests);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("?repositoryId=github:12345", Uri.UnescapeDataString(request.Uri.Query));
            Assert.Equal("MbusKey mbk_test", request.Header("Authorization"));
        }
        else
        {
            Assert.Empty(requests);
        }
        var directory = McpToolCatalog.ReadResult<DirectoryResult>(result);
        var role = Assert.Single(directory.Roles);
        Assert.Equal("developer", role.Name);
        var resources = Assert.IsType<MachineResourceView>(Assert.Single(role.Agents).Resources);
        Assert.Equal(MachineResourceState.Elevated, resources.State);
        Assert.Equal(4, resources.SlotsTotal);
        Assert.Equal(2, resources.SlotsBusy);
        Assert.Equal(7L, resources.AgeSeconds);
        Assert.Equal(["memory-pressure"], resources.Reasons);
        var sample = Assert.IsType<RunnerResourceSample>(resources.Sample);
        Assert.Equal(12L, sample.Sequence);
        Assert.Equal(ResourceSampleStatus.Partial, sample.Status);
        Assert.Equal(23.5, sample.Cpu?.UtilizationPercent);
        Assert.Equal(8589934592L, sample.Memory?.TotalBytes);
        Assert.Null(sample.Memory?.AvailableBytes);
        if (supported is null)
        {
            Assert.Null(directory.Verification);
            Assert.False(result.StructuredContent!.Value.TryGetProperty("verification", out _));
        }
        else
        {
            var verification = Assert.IsType<VerificationDirectory>(directory.Verification);
            Assert.Equal(1, verification.RegistryVersion);
            Assert.Equal(supported.Value, verification.RequiredTaskRequestsSupported);
            var recipe = Assert.Single(verification.Recipes);
            Assert.Equal("node-npm-ci", recipe.Id);
            Assert.Equal(24, recipe.NodeMajor);
            Assert.Equal(["test", "typecheck", "build"], recipe.Checks);
            var environment = Assert.Single(verification.Environments);
            Assert.Equal("frontend", environment.Id);
            Assert.Equal("v1", environment.Version.GetString());
            Assert.Equal(recipe.Id, environment.RecipeId);
            Assert.Equal(recipe.Revision, environment.RecipeRevision);
            Assert.Equal(0, environment.ApprovedSharedMachineCount);
        }
    }

    [Fact]
    public async Task Retry_preserves_source_pin_identity_request_and_exact_repository_revision()
    {
        using var machine = new TestMachine();
        var body = MessageBusJson.Serialize(new MessageBody { Summary = "Retry the same approved input." });
        var request = new VerificationRequest { Environment = "frontend", RequiredChecks = ["test", "build"] };
        var root = new MessageEnvelope
        {
            Id = "msg_source", Type = "task.implement", Source = "person://ws_test/person_test",
            Time = machine.Time.GetUtcNow(), WorkspaceId = "ws_test", ProjectId = "prj_source",
            Target = new RoleTarget { Role = "developer" }, ThreadId = "thr_source",
            Sender = new SenderIdentity { Kind = SenderKind.Person, SubjectId = "person_test" },
            Verification = request,
            Refs = [new Ref { Kind = "github.commit", Repo = "example-org/example-web", Sha = new string('a', 40) }],
            Body = new BodyRef
            {
                ContentType = MessageLimits.BodyContentType, SizeBytes = Encoding.UTF8.GetByteCount(body), Sha256 = "",
                Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(body)),
            },
        };
        ConfigureService(machine.Server, supported: true, retryRoot: root);
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/threads/thr_source/messages",
            _ => Json(JsonNode.Parse("[" + MessageBusJson.Serialize(root) + "]")!));
        using var http = new HttpClient(machine.Server) { BaseAddress = new Uri(FakeServer.Url) };
        await using var tools = new LocalTools(new LocalServerOptions
        {
            Http = http, Links = machine.Server.Links, WorkspaceId = "ws_test", ConfigDirectory = machine.MbusFolder,
            Input = Stream.Null, Output = Stream.Null,
        });
        var result = await tools.CallAsync(null, McpToolNames.Retry,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"threadId":"thr_source"}"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError == true);
        var sent = Assert.Single(ToolCalls(machine.Server), call => (string?)call["name"] == McpToolNames.SendVerified)["arguments"]!;
        Assert.Equal("thr_source", (string?)sent["sourceThreadId"]);
        Assert.Equal("prj_source", (string?)sent["projectId"]);
        Assert.Equal("frontend", (string?)sent["verification"]!["environment"]);
        Assert.Equal(["test", "build"], sent["verification"]!["requiredChecks"]!.AsArray().Select(check => (string?)check));
        Assert.Equal("example-org/example-web", (string?)sent["refs"]![0]!["repo"]);
        Assert.Equal(new string('a', 40), (string?)sent["refs"]![0]!["sha"]);
        Assert.DoesNotContain(ToolCalls(machine.Server), call => (string?)call["name"] == McpToolNames.Send);
    }

    private static IEnumerable<JsonNode> ToolCalls(FakeServer server) =>
        server.Requests.Where(request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == ApiRoutes.Mcp)
            .Select(request => JsonNode.Parse(request.BodyText)!)
            .Where(message => (string?)message["method"] == "tools/call")
            .Select(message => message["params"]!);

    private static void ConfigureService(FakeServer server, bool supported, string state = "final", MessageEnvelope? retryRoot = null)
    {
        if (state != "legacy" && state != "404")
        {
            server.On(HttpMethod.Get, "/api/w/ws_test/environments", _ =>
            {
                if (state == "malformed") return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{broken", Encoding.UTF8, "application/json"),
                };
                var registry = JsonNode.Parse("""{"registryVersion":1,"requiredTaskRequestsSupported":true,"recipes":[],"environments":[]}""")!;
                registry["requiredTaskRequestsSupported"] = supported && state is not ("pin-only" or "disabled");
                if (state == "wrong-version") registry["registryVersion"] = 2;
                if (state == "missing-capability") registry.AsObject().Remove("requiredTaskRequestsSupported");
                return Json(registry);
            });
        }
        using var pair = KeyPair.GenerateP256();
        PublicKeyInfo Key(KeyScope scope, string? owner) => new()
        {
            Scope = scope, Owner = owner, KeyId = "ek_" + scope, Version = 1, Algorithm = pair.PublicKey.Algorithm,
            PublicKey = pair.PublicKey.ToBase64(), Fingerprint = pair.PublicKey.Fingerprint, CreatedAt = DateTimeOffset.UnixEpoch,
        };
        var role = Key(KeyScope.Role, "developer");
        var workspace = Key(KeyScope.Workspace, null);
        server.On(HttpMethod.Get, "/api/caller", _ => Json(JsonNode.Parse("""{"subjectId":"person_test","permissions":[]}""")!));
        server.On(HttpMethod.Get, "/api/w/ws_test/keys", _ => FakeServer.Json<IReadOnlyList<PublicKeyInfo>>([role, workspace]));
        server.On(HttpMethod.Get, "/api/w/ws_test/keys/rotation", request =>
        {
            var key = request.Uri.Query.Contains("scope=role", StringComparison.Ordinal) ? role : workspace;
            return FakeServer.Json<IReadOnlyList<KeyRotationState>>([new() { Scope = key.Scope, Owner = key.Owner, Key = key }]);
        });
        server.On(HttpMethod.Post, ApiRoutes.Mcp, request =>
        {
            var message = JsonNode.Parse(request.BodyText)!;
            if (message["id"] is not { } id) return new(HttpStatusCode.Accepted);
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
            if ((string?)message["method"] == "initialize")
            {
                var initialized = JsonNode.Parse("""{"capabilities":{"tools":{}},"serverInfo":{"name":"test","version":"1"}}""")!;
                initialized["protocolVersion"] = message["params"]!["protocolVersion"]!.DeepClone();
                response["result"] = initialized;
            }
            else if ((string?)message["method"] == "tools/list")
            {
                var names = state is "legacy" or "missing-tools"
                    ? new[] { McpToolNames.Send, McpToolNames.SendMany }
                    : new[] { McpToolNames.Send, McpToolNames.SendMany, McpToolNames.SendVerified, McpToolNames.SendManyVerified };
                response["result"] = new JsonObject
                {
                    ["tools"] = new JsonArray(names.Select(name => (JsonNode)new JsonObject
                    {
                        ["name"] = name, ["inputSchema"] = JsonNode.Parse("""{"type":"object"}"""),
                    }).ToArray()),
                };
            }
            else if ((string?)message["method"] != "tools/call")
            {
                response["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
            }
            else if ((string?)message["params"]!["name"] == McpToolNames.Status && retryRoot is { } root)
            {
                var status = new StatusResult
                {
                    Threads = [new ThreadSummary
                    {
                        ThreadId = root.ThreadId, RootMessageId = root.Id, WorkspaceId = root.WorkspaceId,
                        Type = root.Type, Target = root.Target, Sender = root.Sender, Status = ThreadStatus.Settled,
                        CreatedAt = root.Time, UpdatedAt = root.Time,
                    }],
                };
                response["result"] = new JsonObject { ["structuredContent"] = JsonNode.Parse(MessageBusJson.Serialize(status)), ["content"] = new JsonArray() };
            }
            else if ((string?)message["params"]!["name"] == McpToolNames.Directory)
            {
                response["result"] = JsonNode.Parse("""
                    {
                      "structuredContent": {
                        "roles": [{
                          "name": "developer",
                          "displayName": "Developer",
                          "delivery": {},
                          "agents": [{
                            "agentId": "agt_fixture",
                            "machineId": "mch_fixture",
                            "ownerSubjectId": "microsoft:fixture",
                            "kind": "codex",
                            "displayName": "Fixture agent",
                            "presence": "online",
                            "resources": {
                              "state": "elevated", "ageSeconds": 7, "slotsTotal": 4, "slotsBusy": 2,
                              "reasons": ["memory-pressure"],
                              "sample": {
                                "schemaVersion": 1, "sequence": 12, "sampledAt": "2026-01-01T00:00:00Z",
                                "status": "partial", "cpu": { "utilizationPercent": 23.5 },
                                "memory": { "totalBytes": 8589934592 }
                              }
                            }
                          }]
                        }]
                      },
                      "content": []
                    }
                    """);
            }
            else if (state == "handler-refusal")
            {
                response["result"] = JsonNode.Parse("""{"isError":true,"structuredContent":{"code":"forbidden","message":"The approved environment is no longer available.","retryable":false},"content":[]}""");
            }
            else if (supported && (string?)message["params"]!["name"] is McpToolNames.SendMany or McpToolNames.SendManyVerified)
            {
                response["result"] = JsonNode.Parse("""{"structuredContent":{"results":[{"messageId":"msg_one","threadId":"thr_one","projectId":"prj_test","status":"queued"},{"messageId":"msg_two","threadId":"thr_two","projectId":"prj_test","status":"queued"}]},"content":[]}""");
            }
            else if (supported)
            {
                response["result"] = JsonNode.Parse("""
                    {"structuredContent":{"messageId":"msg_test","threadId":"thr_test","projectId":"prj_test","status":"queued"},"content":[]}
                    """);
            }
            else
            {
                response["error"] = new JsonObject { ["code"] = -32602, ["message"] = "Unknown tool" };
            }
            return Json(response);
        });
    }

    private static HttpResponseMessage Json(JsonNode value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
}
