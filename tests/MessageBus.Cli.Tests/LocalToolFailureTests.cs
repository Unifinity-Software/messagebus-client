using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Mcp;
using MessageBus.Mcp;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class LocalToolFailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_runner_is_told_about_permanent_refusals_but_not_retryable_connection_failures()
    {
        using var machine = new TestMachine();
        using var http = new HttpClient(new Unreachable()) { BaseAddress = new Uri("http://127.0.0.1:9/") };
        var refusals = new List<(string Tool, McpError Error)>();
        await using var tools = new LocalTools(new LocalServerOptions
        {
            Http = http, Links = http, WorkspaceId = "ws_test", ConfigDirectory = machine.Home,
            Input = Stream.Null, Output = Stream.Null, Refused = (name, error) =>
            {
                refusals.Add((name, error));
                throw new InvalidOperationException("A broken reporter cannot break the tool result.");
            },
            Scope = new RunToolScope
            {
                DeliveryId = "dlv_mine", ThreadId = "thr_mine", Root = machine.Home,
                Tools = new HashSet<string> { McpToolNames.Settle, McpToolNames.Directory },
            },
        });
        var refused = await tools.CallAsync("codex", McpToolNames.Settle,
            new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["deliveryId"] = System.Text.Json.JsonSerializer.SerializeToElement("dlv_someone_else"),
            }, Ct);
        Assert.True(refused.IsError);
        var reported = Assert.Single(refusals);
        Assert.Equal(McpToolNames.Settle, reported.Tool);
        Assert.Equal("This task's run can only act on its own delivery (dlv_mine).", reported.Error.Message);
        var disconnected = await tools.CallAsync("codex", McpToolNames.Directory, null, Ct);
        Assert.True(disconnected.IsError);
        Assert.Single(refusals);
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused.");
    }

    [Fact]
    public void A_failure_reason_is_never_empty_and_names_the_innermost_cause_on_one_line()
    {
        // A session once got "directory failed on this machine: " from an exception with no message.
        Assert.Equal("InvalidOperationException", LocalTools.Reason(new InvalidOperationException("")));
        Assert.Equal("The pipe is broken.", LocalTools.Reason(new HttpRequestException("", new IOException("The pipe is broken.\n   at Somewhere()"))));
        Assert.Equal("outer", LocalTools.Reason(new InvalidOperationException("outer", new IOException(" "))));
    }
}
