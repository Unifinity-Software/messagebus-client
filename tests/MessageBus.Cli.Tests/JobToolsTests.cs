using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Mcp;
using MessageBus.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class JobToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] WorkflowTools =
    [
        McpToolNames.ListWorkflows, McpToolNames.StartJob, McpToolNames.JobStatus,
        McpToolNames.FindJob, McpToolNames.EnsureJob, McpToolNames.CompleteJob,
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_gates_the_six_workflow_names(bool supported)
    {
        using var machine = new TestMachine();
        SetCapability(machine, supported);
        using var http = Client(machine);
        await using var tools = Tools(machine, http);
        var listed = await tools.ListToolsAsync(null, Ct);

        Assert.All(WorkflowTools, name => Assert.Equal(supported, listed.Any(tool => tool.Name == name)));
        Assert.All(new[] { McpToolNames.FindProject, McpToolNames.EnsureProject, McpToolNames.CompleteProject }, name =>
        {
            var alias = Assert.Single(listed, tool => tool.Name == name);
            Assert.Equal(supported, alias.Description!.Contains("goes away in the next release", StringComparison.Ordinal));
        });
        if (!supported)
        {
            var call = await Call(tools, McpToolNames.StartJob, """{"workItem":"gh:owner/repo#42","workflow":"Feature"}""");
            var error = Assert.Throws<McpToolException>(() => McpToolCatalog.ReadResult<StartJobResult>(call));
            Assert.Equal(McpErrorCodes.Unsupported, error.Error.Code);
            Assert.All(machine.Server.Requests, request => Assert.Equal("/api/server", request.Uri.AbsolutePath));
        }
    }

    [Fact]
    public async Task Starts_a_job_by_workflow_name_and_returns_the_dashboard_link()
    {
        using var machine = new TestMachine();
        SetCapability(machine, true);
        machine.Server
            .On(HttpMethod.Get, "/api/w/ws_test/workflows", _ =>
                Json("""[{"id":"wf_feature","name":"Feature","description":"Implement an issue","state":"published","publishedVersion":3,"settings":{"runsOn":"workspace"}}]"""))
            .On(HttpMethod.Post, "/api/w/ws_test/jobs", _ =>
                Json("""{"id":"job_test","state":"running","workflow":{"id":"wf_feature","name":"Feature","version":3}}""", HttpStatusCode.Created));
        using var http = Client(machine);
        await using var tools = Tools(machine, http);

        var result = McpToolCatalog.ReadResult<StartJobResult>(await Call(tools, McpToolNames.StartJob,
            """{"workItem":"gh:owner/repo#42","workflow":"fEaTuRe","instructions":"Add tests.","limitUsd":5.50,"runsOn":"owner-runners"}"""));

        Assert.Equal("job_test", result.JobId);
        Assert.Equal(FakeServer.Url + "/jobs/job_test", result.Url);
        Assert.Equal("running", result.State);
        Assert.Equal("wf_feature", result.Workflow!.Value.GetProperty("id").GetString());
        var request = machine.Server.Single(HttpMethod.Post, "/api/w/ws_test/jobs");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(request.BodyText), JsonNode.Parse(
            """{"workItem":"gh:owner/repo#42","workflowId":"wf_feature","instructions":"Add tests.","limitUsd":5.50,"runsOn":"owner-runners"}""")));
        Assert.Equal(3, machine.Server.Requests.Count);
    }

    [Fact]
    public async Task Status_projects_step_runs_and_reports_a_workspace_404()
    {
        using var machine = new TestMachine();
        SetCapability(machine, true);
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/jobs/job_test", _ => Json("""
            {"id":"job_test","state":"running","spentUsd":1.2,"limitUsd":3,"path":{},
             "activeStepIds":["review"],"steps":[{"stepId":"review","round":2,"state":"waiting","agent":"reviewer",
             "machineId":"mch_test","threadId":"thr_test","waitingOn":{"kind":"agent","role":"reviewer"}}]}
            """));
        using var http = Client(machine);
        await using var tools = Tools(machine, http);

        var call = await Call(tools, McpToolNames.JobStatus, """{"jobId":"job_test"}""");
        var result = McpToolCatalog.ReadResult<JobStatusResult>(call);
        Assert.Equal(1.2m, result.SpentUsd);
        Assert.Equal(3m, result.LimitUsd);
        Assert.Equal("review", Assert.Single(result.ActiveStepIds));
        var step = Assert.Single(result.Steps);
        Assert.Equal(2, step.Round);
        Assert.Equal("mch_test", step.MachineId);
        Assert.Equal("agent", step.WaitingOn!.Value.GetProperty("kind").GetString());
        Assert.False(call.StructuredContent!.Value.TryGetProperty("path", out _));
        Assert.False(call.StructuredContent.Value.GetProperty("steps")[0].TryGetProperty("threadId", out _));

        var missing = await Call(tools, McpToolNames.JobStatus, """{"jobId":"job_missing"}""");
        var error = Assert.Throws<McpToolException>(() => McpToolCatalog.ReadResult<JobStatusResult>(missing));
        Assert.Equal("Workflows are not switched on for this workspace.", error.Error.Message);
    }

    [Theory]
    [InlineData(McpToolNames.FindProject, McpToolNames.FindJob, """{"key":"gh:owner/repo#42"}""")]
    [InlineData(McpToolNames.EnsureProject, McpToolNames.EnsureJob, """{"key":"gh:owner/repo#42","title":"Example"}""")]
    [InlineData(McpToolNames.CompleteProject, McpToolNames.CompleteJob, """{"projectId":"prj_test","summary":"Done"}""")]
    public async Task Hands_on_names_share_the_project_handler(string projectTool, string jobTool, string args)
    {
        using var machine = new TestMachine();
        SetCapability(machine, true);
        machine.Server.On(HttpMethod.Post, "/mcp", request =>
        {
            var rpc = JsonNode.Parse(request.BodyText)!;
            if (rpc["id"] is not { } id) return new(HttpStatusCode.Accepted);
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
            switch ((string?)rpc["method"])
            {
                case "server/discover":
                    response["error"] = JsonNode.Parse("""{"code":-32601,"message":"Method not found"}""");
                    break;
                case "initialize":
                    response["result"] = JsonNode.Parse("""{"capabilities":{"tools":{}},"serverInfo":{"name":"test","version":"1"}}""");
                    response["result"]!["protocolVersion"] = rpc["params"]!["protocolVersion"]!.DeepClone();
                    break;
                case "tools/call":
                    Assert.Equal(projectTool, (string?)rpc["params"]!["name"]);
                    response["result"] = JsonNode.Parse("""
                        {"content":[],"structuredContent":{"project":{"projectId":"prj_test","workspaceId":"ws_test",
                         "key":"gh:owner/repo#42","title":"Example","startedBy":{"kind":"person","displayName":"Example"},
                         "status":"completed","createdAt":"2026-01-01T00:00:00Z"}}}
                        """);
                    break;
                default:
                    throw new InvalidOperationException(request.BodyText);
            }
            return Json(response.ToJsonString());
        });
        using var http = Client(machine);
        await using var tools = Tools(machine, http);

        var original = await Call(tools, projectTool, args);
        var alias = await Call(tools, jobTool, args);

        Assert.False(original.IsError == true, original.StructuredContent?.GetRawText());
        Assert.False(alias.IsError == true, alias.StructuredContent?.GetRawText());
        Assert.Equal("prj_test", original.StructuredContent!.Value.GetProperty("project").GetProperty("projectId").GetString());
        Assert.Equal(original.StructuredContent.Value.GetRawText(), alias.StructuredContent!.Value.GetRawText());
    }

    private static Task<CallToolResult> Call(LocalTools tools, string name, string args)
    {
        using var doc = JsonDocument.Parse(args);
        return tools.CallAsync("test", name, doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()), Ct);
    }

    private static void SetCapability(TestMachine machine, bool supported) =>
        machine.Server.On(HttpMethod.Get, "/api/server", _ => Json(supported
            ? """{"product":"messagebus.ai","capabilities":["workflows"]}"""
            : """{"product":"messagebus.ai","capabilities":[]}"""));

    private static HttpClient Client(TestMachine machine) =>
        new(machine.Server, disposeHandler: false) { BaseAddress = new Uri(FakeServer.Url) };

    private static LocalTools Tools(TestMachine machine, HttpClient http) => new(new LocalServerOptions
    {
        Http = http, Links = http, WorkspaceId = "ws_test", ConfigDirectory = machine.Home,
        Input = Stream.Null, Output = Stream.Null, Time = machine.Time,
    });

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };
}
