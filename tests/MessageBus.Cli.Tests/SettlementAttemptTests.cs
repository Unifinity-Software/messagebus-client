using System.Net;
using System.Text.Json;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Cli.Tests.Runner;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using MessageBus.Mcp;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class SettlementAttemptTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Agent_run_reports_its_delivered_attempt_when_the_run_is_declined_before_start()
    {
        await using var runner = new RunnerHarness();
        runner.Service.OnRunApproval = _ => new RunApprovalState { ApprovalId = "apr_test", Status = RunApprovalStatus.Declined };
        runner.Service.OnRequest = path => path == "/api/server"
            ? FakeServer.Json(new MessageBus.Contracts.Http.ServerCapabilitiesResponse { Product = "messagebus.ai", Capabilities = ["settleAttemptV1"] })
            : null;
        var delivery = runner.Task("dlv_attempt") with { Attempt = 3, RequireRunApproval = true };
        Assert.True((await runner.DeliverAsync(delivery)).Accepted);
        var settle = await runner.Service.SettledAsync(delivery.DeliveryId);
        Assert.Equal(3, settle.Attempt);
        Assert.Equal(DeclineReason.NotApproved, settle.Declined);
    }

    [Theory]
    [InlineData(true, true, 3)]
    [InlineData(false, true, null)]
    [InlineData(true, false, null)]
    public async Task Local_settle_uses_only_the_trusted_delivery_attempt_and_gates_it(bool supported, bool trusted, int? expected)
    {
        using var h = new ApprovalImpactTests.Harness();
        h.Support(supported);
        await using var runner = new RunnerHarness();
        var delivery = runner.Task("dlv_test") with { Attempt = 3 };
        var file = Path.Combine(h.Machine.Home, "delivery.json");
        await File.WriteAllTextAsync(file, MessageBusJson.Serialize(delivery with { Attempt = 999 }), Ct);
        await using var tools = new LocalTools(h.Options with
        {
            Delivery = trusted ? delivery : null, DeliveryFile = file,
            Scope = RunToolScope.For(delivery, h.Machine.Home),
        });
        var result = await ApprovalImpactTests.CallAsync(tools, McpToolNames.Settle,
            """{"threadId":"thr_dlv_test","deliveryId":"dlv_test","attempt":777,"result":{"status":"succeeded"}}""");
        Assert.False(result.IsError == true, result.StructuredContent.ToString());
        var forwarded = Assert.Single(h.Calls);
        Assert.Equal(expected, (int?)forwarded["attempt"]);
        Assert.Equal(expected is not null, forwarded.ContainsKey("attempt"));
    }

    [Fact]
    public async Task A_kept_answer_cannot_replace_the_trusted_attempt()
    {
        using var h = new ApprovalImpactTests.Harness();
        h.Support();
        await using var runner = new RunnerHarness();
        var delivery = runner.Task("dlv_test") with { Attempt = 4 };
        var settleFile = Path.Combine(h.Machine.Home, "settle.json");
        var kept = new SettleInput
        {
            ThreadId = delivery.Envelope.ThreadId, DeliveryId = delivery.DeliveryId, Attempt = 1,
            Result = new SettleResult { Status = SettleStatus.Succeeded },
            SealedReply = new SealedBody
            {
                Body = delivery.Envelope.Body,
                Encryption = new EncryptionInfo
                {
                    Algorithm = "A256GCM", KeyWrapAlgorithm = "RSA-OAEP-256", KeyId = "ek_test",
                    WrappedKey = Convert.ToBase64String(new byte[32]), Nonce = Convert.ToBase64String(new byte[12]),
                },
            },
        };
        await File.WriteAllTextAsync(KeptAnswer.KeptFile(settleFile), MessageBusJson.Serialize(kept), Ct);
        await using var tools = new LocalTools(h.Options with
        {
            Delivery = delivery, Scope = RunToolScope.For(delivery, h.Machine.Home), SettleFile = settleFile,
        });
        Assert.True(await tools.SendKeptAnswerAsync(Ct));
        var forwarded = Assert.Single(h.Calls);
        Assert.Equal(4, (int?)forwarded["attempt"]);
        Assert.True((bool?)forwarded["replyOnly"]);
        var recorded = MessageBusJson.Deserialize<SettleInput>(await File.ReadAllTextAsync(settleFile, Ct))!;
        Assert.Equal(delivery.DeliveryId, recorded.DeliveryId);
        Assert.Equal(4, recorded.Attempt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Runner_refreshes_capabilities_for_every_settle_and_never_sends_attempt_to_an_old_server(bool initiallySupported)
    {
        using var h = new ApprovalImpactTests.Harness();
        h.Support(initiallySupported);
        h.Machine.Server.On(HttpMethod.Post, ServiceRoutes.Settle, _ => new(HttpStatusCode.OK));
        var client = new HttpRunnerServiceClient(h.Http);
        var settle = new RunnerSettle { DeliveryId = "dlv_test", Attempt = 3, Result = new SettleResult { Status = SettleStatus.Succeeded } };
        await client.SettleAsync(settle, Ct);
        h.Support(!initiallySupported);
        await client.SettleAsync(settle, Ct);
        var posts = h.Machine.Server.Requests.Where(r => r.Method == HttpMethod.Post).ToArray();
        Assert.Equal(2, posts.Length);
        for (var i = 0; i < 2; i++)
        {
            using var json = JsonDocument.Parse(posts[i].BodyText);
            var supported = i == 0 ? initiallySupported : !initiallySupported;
            Assert.Equal(supported, json.RootElement.TryGetProperty("attempt", out var attempt));
            if (supported) Assert.Equal(3, attempt.GetInt32());
        }
        Assert.Equal(2, h.Machine.Server.Requests.Count(r => r.Uri.AbsolutePath == "/api/server"));
    }

    [Fact]
    public async Task Stale_attempt_conflict_is_propagated_without_an_untagged_retry()
    {
        using var h = new ApprovalImpactTests.Harness();
        h.Support();
        var currentAttempt = 3;
        var accepted = 0;
        h.Machine.Server.On(HttpMethod.Post, ServiceRoutes.Settle, request =>
        {
            var input = MessageBusJson.Deserialize<RunnerSettle>(request.BodyText)!;
            if (input.Attempt != currentAttempt) return new(HttpStatusCode.Conflict);
            accepted++;
            return new(HttpStatusCode.OK);
        });
        var client = new HttpRunnerServiceClient(h.Http);
        var old = new RunnerSettle { DeliveryId = "dlv_reused", Attempt = 2, Result = new SettleResult { Status = SettleStatus.Succeeded } };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SettleAsync(old, Ct));
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal(0, accepted);
        Assert.Single(h.Machine.Server.Requests, r => r.Method == HttpMethod.Post);
        await client.SettleAsync(old with { Attempt = currentAttempt }, Ct);
        Assert.Equal(1, accepted);
    }
}
