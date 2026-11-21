using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using Xunit;

namespace MessageBus.Contracts.Tests;

public sealed class RetargetContractTests
{
    [Fact]
    public void Future_remote_tools_are_hidden_until_the_handler_opts_in_and_existing_tools_remain()
    {
        Assert.DoesNotContain(McpToolCatalog.Tools(local: false), t => t.Name == McpToolNames.Retarget);
        Assert.Contains(McpToolCatalog.Tools(local: true), t => t.Name == McpToolNames.Retarget);
        Assert.Contains(McpToolCatalog.Tools(local: false, only: null, supported: new HashSet<string> { McpToolNames.Retarget }), t => t.Name == McpToolNames.Retarget);
        Assert.Contains(McpToolCatalog.Tools(local: false), t => t.Name == McpToolNames.Move);
        Assert.DoesNotContain(McpToolCatalog.Tools(local: false, only: new HashSet<string> { McpToolNames.Move }, supported: new HashSet<string> { McpToolNames.Retarget }), t => t.Name == McpToolNames.Retarget);
    }

    [Fact]
    public void Local_schema_hides_sealed_inputs_and_keeps_preview_and_retry_controls()
    {
        var schema = McpToolCatalog.Schema(typeof(RetargetInput), local: true).GetProperty("properties");
        Assert.False(schema.TryGetProperty("sealedBody", out _));
        Assert.False(schema.TryGetProperty("attachments", out _));
        Assert.True(schema.TryGetProperty("dryRun", out _));
        Assert.True(schema.TryGetProperty("operationId", out _));
        Assert.True(schema.TryGetProperty("expectedTaskMessageId", out _));
        Assert.Equal(500, schema.GetProperty("reason").GetProperty("maxLength").GetInt32());
        Assert.True(McpToolCatalog.Schema(typeof(RetargetInput), local: false).GetProperty("properties").TryGetProperty("sealedBody", out _));
    }

    [Fact]
    public void Existing_message_events_remain_readable_without_retarget_metadata()
    {
        var old = MessageBusJson.Deserialize<ThreadEvent>("""
            {"kind":"message","eventId":"evt_test","threadId":"thr_test","at":"2026-11-13T09:00:00Z",
             "messageId":"msg_test","type":"answer","sender":{"kind":"person"}}
            """);
        Assert.Null(Assert.IsType<ThreadMessageAdded>(old).Retarget);
        Assert.DoesNotContain("retarget", MessageBusJson.Serialize(old));
    }
}
