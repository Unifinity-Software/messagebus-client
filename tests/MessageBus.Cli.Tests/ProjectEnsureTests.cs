using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Scripts;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Mcp;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class ProjectEnsureTests
{
    [Theory]
    [InlineData("owner-runners", "workspace", null, false)]
    [InlineData("owner-runners", null, null, false)]
    [InlineData("workspace", null, null, false)]
    [InlineData("owner-runners", "owner-runners", null, false)]
    [InlineData("owner-runners", "owner-runners", " ", false)]
    [InlineData("owner-runners", "owner-runners", "microsoft:owner", true)]
    [InlineData(null, "owner-runners", "microsoft:owner", true)]
    public async Task Explicit_scope_requires_a_matching_response_and_owner_pin(
        string? requested, string? returned, string? owner, bool succeeds)
    {
        var response = ProjectResponse(returned, owner);
        using var machine = Machine(response);
        string[] scopeArgs = requested is null ? [] : ["--execution-scope", requested];

        var run = await machine.RunAsync(["project", "ensure", "file:plan.md", "--title", "Plan", "--json", .. scopeArgs]);

        var call = Assert.Single(ToolCalls(machine));
        Assert.Equal(McpToolNames.EnsureProject, (string?)call["name"]);
        Assert.Equal(requested, (string?)call["arguments"]!["executionScope"]);
        Assert.Equal(requested is not null, call["arguments"]!.AsObject().ContainsKey("executionScope"));
        var output = JsonNode.Parse(run.Output)!;
        if (succeeds)
        {
            Assert.Equal(ScriptExitCodes.Ok, run.ExitCode);
            Assert.Equal(returned, (string?)output["project"]!["executionScope"]);
            Assert.Equal(owner, (string?)output["project"]!["executionOwnerSubjectId"]);
        }
        else
        {
            Assert.Equal(ScriptExitCodes.Failed, run.ExitCode);
            Assert.Contains("requested project execution scope", run.Error);
            Assert.Null(output["project"]);
        }
    }

    [Theory]
    [InlineData(false, "inb_test", false)]
    [InlineData(true, null, false)]
    [InlineData(true, " ", false)]
    [InlineData(true, "inb_test", true)]
    public async Task Claims_require_acquisition_and_a_coordinator_inbox(bool acquired, string? inbox, bool succeeds)
    {
        var response = ProjectResponse("owner-runners", "microsoft:owner");
        response["coordinationAcquired"] = acquired;
        response["coordinatorInboxId"] = inbox;
        using var machine = Machine(response);

        var run = await machine.RunAsync("project", "ensure", "file:plan.md", "--title", "Plan",
            "--execution-scope", "owner-runners", "--claim", "gh:acme/api#42");

        if (succeeds)
        {
            Assert.Equal(ScriptExitCodes.Ok, run.ExitCode);
            Assert.Contains($"Claims acquired. To resume, repeat the claims with --coordinator-inbox {inbox}.", run.Output);
        }
        else
        {
            Assert.Equal(ScriptExitCodes.Failed, run.ExitCode);
            Assert.Empty(run.Output);
            Assert.Contains("did not confirm acquisition", run.Error);
        }
        Assert.Equal([McpToolNames.OpenInbox, McpToolNames.EnsureProject], ToolCalls(machine).Select(call => (string?)call["name"]));
    }

    [Fact]
    public async Task A_server_refusal_keeps_its_error()
    {
        using var machine = Machine(JsonNode.Parse("""{"code":"conflict","message":"The claims already belong to another inbox."}""")!, isError: true);

        var run = await machine.RunAsync("project", "ensure", "file:plan.md", "--title", "Plan",
            "--execution-scope", "owner-runners", "--claim", "gh:acme/api#42", "--json");

        Assert.Equal(ScriptExitCodes.Conflict, run.ExitCode);
        Assert.Equal("The claims already belong to another inbox.", (string?)JsonNode.Parse(run.Output)!["error"]!["message"]);
    }

    private static JsonObject ProjectResponse(string? scope, string? owner)
    {
        var result = JsonNode.Parse("""
            {"project":{"projectId":"prj_test","workspaceId":"ws_test","key":"file:plan.md","title":"Plan",
            "startedBy":{"kind":"agent"},"status":"active","createdAt":"2026-11-13T09:00:00Z"}}
            """)!.AsObject();
        var project = result["project"]!.AsObject();
        if (scope is not null) project["executionScope"] = scope;
        if (owner is not null) project["executionOwnerSubjectId"] = owner;
        return result;
    }

    private static IEnumerable<JsonNode> ToolCalls(TestMachine machine) =>
        machine.Server.Requests.Where(request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == ApiRoutes.Mcp)
            .Select(request => JsonNode.Parse(request.BodyText)!)
            .Where(message => (string?)message["method"] == "tools/call")
            .Select(message => message["params"]!);

    private static TestMachine Machine(JsonNode response, bool isError = false)
    {
        var machine = new TestMachine();
        new ConfigStore(machine.MbusFolder, "file").Save(new CliConfig { Server = FakeServer.Url, WorkspaceId = "ws_test" });
        machine.Env["MBUS_API_KEY"] = "mbk_test";
        machine.Server.On(HttpMethod.Post, ApiRoutes.Mcp, request =>
        {
            var message = JsonNode.Parse(request.BodyText)!;
            if (message["id"] is not { } id) return new(HttpStatusCode.Accepted);
            var method = (string?)message["method"];
            var envelope = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
            JsonNode result;
            if (method == "initialize")
            {
                result = JsonNode.Parse("""{"capabilities":{"tools":{}},"serverInfo":{"name":"test","version":"1"}}""")!;
                result["protocolVersion"] = message["params"]!["protocolVersion"]!.DeepClone();
            }
            else if (method == "tools/call")
            {
                var tool = (string?)message["params"]!["name"];
                var outcome = tool switch
                {
                    McpToolNames.OpenInbox => JsonNode.Parse("""
                        {"inboxId":"inb_test","replyTo":{"kind":"inbox","inboxId":"inb_test"},"expiresAt":"2026-11-14T09:00:00Z"}
                        """)!,
                    McpToolNames.EnsureProject => response,
                    _ => throw new InvalidOperationException($"Unexpected tool {tool}."),
                };
                result = new JsonObject
                {
                    ["isError"] = tool == McpToolNames.EnsureProject && isError,
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = outcome.ToJsonString() }),
                };
            }
            else
            {
                envelope["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
                return new(HttpStatusCode.OK) { Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json") };
            }

            envelope["result"] = result;
            return new(HttpStatusCode.OK) { Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json") };
        });
        return machine;
    }
}
