using MessageBus.Contracts.Mcp;
using Xunit;

namespace MessageBus.Contracts.Tests;

public sealed class VerificationCatalogTests
{
    [Fact]
    public void Remote_catalog_requires_explicit_handler_opt_in_and_local_send_keeps_request_schema()
    {
        var ordinary = McpToolCatalog.Tools(local: false);
        Assert.DoesNotContain(ordinary, tool => tool.Name == McpToolNames.SendVerified);
        Assert.DoesNotContain(ordinary, tool => tool.Name == McpToolNames.SendManyVerified);

        var installed = McpToolCatalog.Tools(local: false, only: null, includeVerified: true);
        Assert.Contains(installed, tool => tool.Name == McpToolNames.SendVerified);
        Assert.Contains(installed, tool => tool.Name == McpToolNames.SendManyVerified);
        var filtered = McpToolCatalog.Tools(local: false, new HashSet<string> { McpToolNames.Send }, includeVerified: true);
        Assert.Equal(McpToolNames.Send, Assert.Single(filtered).Name);

        var local = McpToolCatalog.Tools(local: true);
        Assert.DoesNotContain(local, tool => tool.Name == McpToolNames.SendVerified);
        Assert.DoesNotContain(local, tool => tool.Name == McpToolNames.SendManyVerified);
        var send = Assert.Single(local, tool => tool.Name == McpToolNames.Send);
        Assert.True(send.InputSchema.GetProperty("properties").TryGetProperty("verification", out _));
        var batch = Assert.Single(local, tool => tool.Name == McpToolNames.SendMany);
        Assert.Contains("send", batch.InputSchema.GetRawText());
    }
}
