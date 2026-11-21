using System.Text.Json;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Contracts.Tests;

public sealed class OptionalApprovalProtocolTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Impact_is_optional_in_both_schemas_and_attempt_is_exposed_only_remotely(bool local)
    {
        var approval = McpToolCatalog.Schema(typeof(RequestApprovalInput), local);
        Assert.Equal(1000, approval.GetProperty("properties").GetProperty("affected").GetProperty("maxLength").GetInt32());
        Assert.DoesNotContain("affected", approval.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        var settle = McpToolCatalog.Schema(typeof(SettleInput), local);
        Assert.Equal(!local, settle.GetProperty("properties").TryGetProperty("attempt", out _));
    }

    [Fact]
    public void Old_payloads_stay_readable_and_omitted_optional_fields_do_not_change_the_wire_shape()
    {
        Assert.Null(MessageBusJson.Deserialize<RequestApprovalInput>("""{"title":"Archive rows"}""")!.Affected);
        Assert.Null(MessageBusJson.Deserialize<ApprovalResult>("""{"approvalId":"apr_old","status":"pending","title":"Archive","expiresAt":"2026-01-01T00:00:00Z"}""")!.Affected);
        var old = MessageBusJson.Deserialize<RunnerSettle>("""{"deliveryId":"dlv_old","result":{"status":"succeeded"}}""")!;
        Assert.Null(old.Attempt);
        Assert.DoesNotContain("attempt", MessageBusJson.Serialize(old));
        Assert.DoesNotContain("affected", MessageBusJson.Serialize(new RequestApprovalInput { Title = "Archive" }));
        var remote = new SettleInput { ThreadId = "thr_test", Attempt = 2, Result = new SettleResult { Status = SettleStatus.Succeeded } };
        using var json = JsonDocument.Parse(MessageBusJson.Serialize(remote));
        Assert.Equal(2, json.RootElement.GetProperty("attempt").GetInt32());
    }
}
