using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class ApprovalImpactTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public async Task Omitted_or_empty_impact_works_with_an_old_server_without_discovery(string? affected)
    {
        using var h = new Harness();
        await using var tools = new LocalTools(h.Options);
        var result = await CallAsync(tools, new RequestApprovalInput { Title = "Archive rows", Affected = affected });
        Assert.False(result.IsError == true, result.StructuredContent.ToString());
        Assert.DoesNotContain(h.Machine.Server.Requests, r => r.Uri.AbsolutePath == "/api/server");
        Assert.False(Assert.Single(h.Calls).ContainsKey("affected"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"product\":\"messagebus.ai\"}")]
    [InlineData("{\"product\":\"other\",\"capabilities\":[\"approvalAffectedV1\"]}")]
    [InlineData("{\"product\":\"messagebus.ai\",\"capabilities\":[\"futureFeature\"]}")]
    [InlineData("malformed")]
    [InlineData("failure")]
    public async Task Unknown_failed_or_wrong_product_discovery_refuses_before_any_post(string info)
    {
        using var h = new Harness();
        h.Machine.Server.On(HttpMethod.Get, "/api/server", _ => info == "failure" ? new(HttpStatusCode.ServiceUnavailable) : Json(info));
        await using var tools = new LocalTools(h.Options);
        var result = await CallAsync(tools, new RequestApprovalInput { Title = "Archive rows", Affected = "Archived rows" });
        Assert.True(result.IsError);
        Assert.Equal("affected", Error(result).Field);
        Assert.DoesNotContain(h.Machine.Server.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Normalized_non_BMP_boundary_round_trips_then_a_downgrade_refuses_without_cached_support()
    {
        using var h = new Harness();
        h.Support();
        await using var tools = new LocalTools(h.Options);
        var affected = string.Concat(Enumerable.Repeat("\U0001F680", 998)) + "\nX";
        var result = await CallAsync(tools, new RequestApprovalInput { Title = "Archive rows", Affected = " \r\n" + affected.Replace("\n", "\r\n") + "\r " });
        Assert.False(result.IsError == true, result.StructuredContent.ToString());
        Assert.Equal(affected, McpToolCatalog.ReadResult<ApprovalResult>(result).Affected);
        Assert.Equal(affected, (string?)Assert.Single(h.Calls)["affected"]);
        var wait = await CallAsync(tools, McpToolNames.WaitApproval, """{"approvalId":"apr_test","timeoutSeconds":1}""");
        Assert.False(wait.IsError == true, wait.StructuredContent.ToString());
        h.Support(false);
        var refused = await CallAsync(tools, new RequestApprovalInput { Title = "Archive rows", Affected = "Rows" });
        Assert.True(refused.IsError);
        Assert.Equal(2, h.Machine.Server.Requests.Count(r => r.Uri.AbsolutePath == "/api/server"));
        Assert.Equal(2, h.Calls.Count); // request and wait only
        Assert.All(h.Machine.Server.Requests.Where(r => r.Uri.AbsolutePath == "/api/server"), r =>
        {
            Assert.Contains("no-cache", r.Header("Cache-Control"));
            Assert.Contains("no-store", r.Header("Cache-Control"));
            Assert.Equal("MbusKey test", r.Header("Authorization"));
        });
    }

    [Fact]
    public async Task Another_target_cannot_inherit_an_earlier_targets_positive_discovery()
    {
        using var first = new Harness();
        first.Support();
        await using var firstTools = new LocalTools(first.Options);
        Assert.False((await CallAsync(firstTools, new RequestApprovalInput { Title = "Archive", Affected = "Rows" })).IsError == true);
        using var second = new Harness();
        using var otherHttp = new HttpClient(second.Machine.Server, false) { BaseAddress = new Uri("https://other.example.invalid/") };
        await using var secondTools = new LocalTools(second.Options with { Http = otherHttp });
        Assert.True((await CallAsync(secondTools, new RequestApprovalInput { Title = "Archive", Affected = "Rows" })).IsError);
        Assert.Empty(second.Calls);
        Assert.Equal("other.example.invalid", Assert.Single(second.Machine.Server.Requests).Uri.Host);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("control")]
    [InlineData("secret")]
    [InlineData("surrogate")]
    public async Task Invalid_or_secret_impact_never_leaves_the_machine(string kind)
    {
        using var h = new Harness();
        h.Support();
        await using var tools = new LocalTools(h.Options);
        var text = kind switch
        {
            "length" => string.Concat(Enumerable.Repeat("\U0001F680", 1001)),
            "control" => "Rows\u0000more",
            "secret" => "-----BEGIN PRIVATE KEY-----\nexample\n-----END PRIVATE KEY-----",
            _ => "",
        };
        var result = kind == "surrogate"
            ? await CallAsync(tools, McpToolNames.RequestApproval, """{"title":"Archive","affected":"\uD800"}""")
            : await CallAsync(tools, new RequestApprovalInput { Title = "Archive rows", Affected = text });
        Assert.True(result.IsError);
        Assert.Equal(McpErrorCodes.InvalidInput, Error(result).Code);
        Assert.Empty(h.Machine.Server.Requests);
    }

    [Theory]
    [InlineData("echo")]
    [InlineData("digest")]
    [InlineData("flag-off")]
    public async Task A_rollout_race_or_bad_echo_is_never_retried_by_dropping_impact(string fault)
    {
        using var h = new Harness();
        h.Support();
        h.Fault = fault;
        await using var tools = new LocalTools(h.Options);
        var result = await CallAsync(tools, new RequestApprovalInput { Title = "Archive", Affected = "Rows" });
        Assert.True(result.IsError);
        Assert.Equal("Rows", (string?)Assert.Single(h.Calls)["affected"]);
        if (fault != "flag-off") Assert.Contains("not authorization", Error(result).Message);
    }

    [Theory]
    [InlineData("echo")]
    [InlineData("changed-digest")]
    public async Task Wait_cannot_authorize_a_different_request(string fault)
    {
        using var h = new Harness();
        h.Support();
        await using var tools = new LocalTools(h.Options);
        Assert.False((await CallAsync(tools, new RequestApprovalInput { Title = "Archive", Affected = "Rows" })).IsError == true);
        h.Fault = fault;
        var wait = await CallAsync(tools, McpToolNames.WaitApproval, """{"approvalId":"apr_test","timeoutSeconds":1}""");
        Assert.True(wait.IsError);
        Assert.Contains("not authorization", Error(wait).Message);
    }

    internal static Task<CallToolResult> CallAsync(LocalTools tools, string tool, string json)
    {
        using var document = JsonDocument.Parse(json);
        return tools.CallAsync("codex", tool, document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()), Ct);
    }

    private static Task<CallToolResult> CallAsync(LocalTools tools, RequestApprovalInput input) =>
        CallAsync(tools, McpToolNames.RequestApproval, MessageBusJson.Serialize(input));

    private static McpError Error(CallToolResult result) => result.StructuredContent!.Value.Deserialize<McpError>(MessageBusJson.Options)!;
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal sealed class Harness : IDisposable
    {
        public TestMachine Machine { get; } = new();
        public HttpClient Http { get; }
        public List<JsonObject> Calls { get; } = [];
        public string? Fault { get; set; }
        private string? _affected;
        public LocalServerOptions Options => new()
        {
            Http = Http, Links = Http, WorkspaceId = "ws_test", ConfigDirectory = Machine.Home,
            Input = Stream.Null, Output = Stream.Null, Time = Machine.Time,
        };

        public Harness()
        {
            Http = new HttpClient(Machine.Server, false) { BaseAddress = new Uri(FakeServer.Url + "/") };
            Http.DefaultRequestHeaders.Authorization = new("MbusKey", "test");
            Machine.Server.On(HttpMethod.Post, "/mcp", request =>
            {
                var rpc = JsonNode.Parse(request.BodyText)!;
                if (rpc["id"] is null) return new(HttpStatusCode.Accepted);
                if ((string?)rpc["method"] is not ("initialize" or "tools/call"))
                    return Json(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = rpc["id"]!.DeepClone(),
                        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" },
                    }.ToJsonString());
                var parameters = rpc["params"]!;
                JsonNode result;
                if ((string?)rpc["method"] == "initialize")
                {
                    result = new JsonObject
                    {
                        ["protocolVersion"] = parameters["protocolVersion"]!.DeepClone(),
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
                    };
                }
                else
                {
                    var tool = (string?)parameters["name"];
                    var args = parameters["arguments"]!.DeepClone().AsObject();
                    Calls.Add(args);
                    if (tool == McpToolNames.RequestApproval) _affected = (string?)args["affected"];
                    var outcome = tool == McpToolNames.Settle ? """{"status":"working"}""" : MessageBusJson.Serialize(new ApprovalResult
                    {
                        ApprovalId = "apr_test", Title = "Archive", Status = ApprovalState.Approved,
                        ExpiresAt = DateTimeOffset.UnixEpoch.AddDays(1),
                        Affected = Fault == "echo" ? "Different rows" : _affected,
                        RequestDigest = Fault == "digest" ? null : Fault == "changed-digest" ? "changed" : "digest",
                    });
                    if (Fault == "flag-off") outcome = """{"code":"unavailable","message":"Feature disabled"}""";
                    result = new JsonObject
                    {
                        ["isError"] = Fault == "flag-off",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = outcome }),
                    };
                }
                return Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = rpc["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
            });
        }

        public void Support(bool enabled = true) => Machine.Server.On(HttpMethod.Get, "/api/server", _ =>
            Json(enabled ? """{"product":"messagebus.ai","capabilities":["approvalAffectedV1","settleAttemptV1"]}""" : """{"product":"messagebus.ai"}"""));

        public void Dispose() { Http.Dispose(); Machine.Dispose(); }
    }
}
