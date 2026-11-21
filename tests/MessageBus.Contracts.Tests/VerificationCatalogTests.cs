using MessageBus.Contracts.Mcp;
using Xunit;

namespace MessageBus.Contracts.Tests;

public sealed class VerificationCatalogTests
{
    [Theory]
    [InlineData(false, McpCatalogFeatures.None, false)]
    [InlineData(false, McpCatalogFeatures.VerifiedTasks, true)]
    [InlineData(false, McpCatalogFeatures.Retarget | McpCatalogFeatures.RoleProposals, false)]
    [InlineData(false, McpCatalogFeatures.VerifiedTasks | McpCatalogFeatures.Retarget | McpCatalogFeatures.RoleProposals, true)]
    [InlineData(true, McpCatalogFeatures.None, true)]
    [InlineData(true, McpCatalogFeatures.VerifiedTasks, true)]
    [InlineData(true, McpCatalogFeatures.Retarget | McpCatalogFeatures.RoleProposals, true)]
    public void Catalog_exposes_only_supported_tools_and_optional_request_schemas(bool local, McpCatalogFeatures features, bool hasRequest)
    {
        var tools = McpToolCatalog.Tools(local, only: null, features);
        Assert.Equal(!local && hasRequest, tools.Any(tool => tool.Name == McpToolNames.SendVerified));
        Assert.Equal(!local && hasRequest, tools.Any(tool => tool.Name == McpToolNames.SendManyVerified));
        var send = Assert.Single(tools, tool => tool.Name == McpToolNames.Send);
        Assert.Equal(hasRequest, send.InputSchema.GetProperty("properties").TryGetProperty("verification", out _));
        Assert.Equal(hasRequest, send.Description!.Contains("requiredChecks", StringComparison.Ordinal));
        var batch = Assert.Single(tools, tool => tool.Name == McpToolNames.SendMany);
        Assert.Contains("send", batch.InputSchema.GetProperty("properties").GetProperty("messages").GetProperty("items").GetProperty("description").GetString());
        if (!local && hasRequest)
        {
            var verified = Assert.Single(tools, tool => tool.Name == McpToolNames.SendVerified);
            Assert.True(verified.InputSchema.GetProperty("properties").TryGetProperty("verification", out _));
        }
    }

    [Theory]
    [InlineData(false, McpCatalogFeatures.None, false)]
    [InlineData(false, McpCatalogFeatures.VerifiedTasks, true)]
    [InlineData(false, McpCatalogFeatures.Retarget | McpCatalogFeatures.RoleProposals, false)]
    [InlineData(true, McpCatalogFeatures.None, false)]
    [InlineData(true, McpCatalogFeatures.VerifiedTasks, false)]
    public void Name_filter_cannot_enable_a_feature_or_expose_service_tools_locally(bool local, McpCatalogFeatures features, bool hasVerified)
    {
        var tools = McpToolCatalog.Tools(local, new HashSet<string> { McpToolNames.Send, McpToolNames.SendVerified }, features);
        Assert.Equal(hasVerified ? 2 : 1, tools.Count);
        Assert.Contains(tools, tool => tool.Name == McpToolNames.Send);
        Assert.Equal(hasVerified, tools.Any(tool => tool.Name == McpToolNames.SendVerified));
        Assert.Empty(McpToolCatalog.Tools(local, new HashSet<string>(), features));
    }

    [Fact]
    public void Published_default_overloads_hide_remote_verification_and_keep_local_requests()
    {
        var remote = McpToolCatalog.Tools(local: false);
        Assert.DoesNotContain(remote, tool => tool.Name is McpToolNames.SendVerified or McpToolNames.SendManyVerified);
        Assert.False(Assert.Single(remote, tool => tool.Name == McpToolNames.Send).InputSchema.GetProperty("properties").TryGetProperty("verification", out _));
        Assert.False(McpToolCatalog.Schema(typeof(SendInput), local: false).GetProperty("properties").TryGetProperty("verification", out _));
        Assert.True(McpToolCatalog.Schema(typeof(SendInput), local: true).GetProperty("properties").TryGetProperty("verification", out _));
    }
}
