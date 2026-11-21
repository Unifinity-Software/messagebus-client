using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using MessageBus.Mcp;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class MachineBundlesTests
{
    [Theory]
    [InlineData(DeliveryStatus.Settled)]
    [InlineData(DeliveryStatus.Leased)]
    public async Task History_fetches_passive_and_leased_replies_and_logs_a_rejected_lease_settlement(DeliveryStatus status)
    {
        await using var harness = new RunnerHarness();
        var task = harness.Task("dlv_task");
        var target = new InboxTarget { InboxId = "inb_history" };
        var bytes = "history bundle fixture"u8.ToArray();
        var history = new AttachmentRef
        {
            Id = "att_history", Name = "history.bundle", Format = AttachmentFormat.GitBundle,
            SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };
        var reply = new ReceivedMessage
        {
            Envelope = task.Envelope with
            {
                Id = "msg_history", Type = ProgressRequestKinds.GitHistoryMessageType, Target = target,
                Attachments = [history],
            },
            Delivery = new DeliveryInfo
            {
                DeliveryId = "dlv_history", MessageId = "msg_history", ThreadId = task.Envelope.ThreadId,
                Status = status, Attempt = 1,
                LeaseExpiresAt = status == DeliveryStatus.Leased ? harness.Time.GetUtcNow().AddMinutes(1) : null,
            },
            Links = [new DownloadLink { Id = history.Id, Url = FakeServer.Url + "/history", ExpiresAt = DateTimeOffset.MaxValue }],
        };

        using var server = new FakeServer();
        server.On(HttpMethod.Get, "/api/caller", _ => Json("""{"subjectId":"fixture","permissions":[]}"""));
        server.On(HttpMethod.Get, "/history", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var settleCalls = new List<SettleInput>();
        server.On(HttpMethod.Post, "/mcp", request =>
        {
            var rpc = JsonNode.Parse(request.BodyText)!;
            if (rpc["id"] is null) return new HttpResponseMessage(HttpStatusCode.Accepted);
            if ((string?)rpc["method"] is not ("initialize" or "tools/call"))
                return Json(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = rpc["id"]!.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" },
                }.ToJsonString());
            var parameters = rpc["params"]!;
            var result = (string?)rpc["method"] == "initialize"
                ? new JsonObject
                {
                    ["protocolVersion"] = parameters["protocolVersion"]!.DeepClone(),
                    ["serverInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                }
                : RespondToTool(parameters);
            return Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = rpc["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
        });

        using var api = new HttpClient(server) { BaseAddress = new Uri(FakeServer.Url) };
        using var log = new StringWriter();
        using var bundles = new MachineBundles(api, server.Links, "ws_test", null, harness.Scratch, log, harness.Time);
        var original = history with { Id = "att_task" };
        var ct = TestContext.Current.CancellationToken;
        var path = await bundles.RequestHistoryAsync(task, original, harness.Scratch, (request, _) =>
        {
            Assert.Equal(ProgressRequestKinds.GitHistory, request.Kind);
            Assert.Equal(original.Id, request.AttachmentId);
            Assert.Equal(target, request.ReplyTo);
            return Task.CompletedTask;
        }, TimeSpan.FromMinutes(1), ct);

        Assert.True(path is not null, log.ToString());
        Assert.Equal(Path.Combine(harness.Scratch, "history.bundle"), path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, ct));
        server.Single(HttpMethod.Get, "/history");
        if (status == DeliveryStatus.Leased)
        {
            var settled = Assert.Single(settleCalls);
            Assert.Equal(reply.Delivery.DeliveryId, settled.DeliveryId);
            Assert.Equal(reply.Delivery.ThreadId, settled.ThreadId);
            Assert.Equal(SettleStatus.Succeeded, settled.Result.Status);
            Assert.Contains("mbus: couldn't settle dlv_history: lease settlement rejected", log.ToString());
        }
        else
        {
            Assert.Empty(settleCalls);
            Assert.DoesNotContain("couldn't settle", log.ToString());
        }

        JsonObject RespondToTool(JsonNode parameters)
        {
            var arguments = parameters["arguments"]!;
            switch ((string?)parameters["name"])
            {
                case McpToolNames.OpenInbox:
                    return ToolResult(new OpenInboxResult
                    {
                        InboxId = target.InboxId, ReplyTo = target, ExpiresAt = DateTimeOffset.MaxValue,
                    });
                case McpToolNames.Inbox:
                    Assert.Equal(target.InboxId, (string?)arguments["inboxId"]);
                    return ToolResult(new InboxResult { Messages = [reply] });
                case McpToolNames.Settle:
                    settleCalls.Add(MessageBusJson.Deserialize<SettleInput>(arguments.ToJsonString())!);
                    return JsonNode.Parse("""{"isError":true,"structuredContent":{"code":"conflict","message":"lease settlement rejected"},"content":[]}""")!.AsObject();
                default:
                    throw new InvalidOperationException($"Unexpected tool {parameters["name"]}");
            }
        }
    }

    private static JsonObject ToolResult<T>(T value) => new()
    {
        ["structuredContent"] = JsonNode.Parse(MessageBusJson.Serialize(value)),
        ["content"] = new JsonArray(),
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
