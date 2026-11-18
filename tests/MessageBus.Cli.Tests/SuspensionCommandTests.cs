using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Runner;
using Xunit;
using MessageBus.Tests.Shared;

namespace MessageBus.Cli.Tests;

public sealed class SuspensionCommandTests
{
    private const string Refusal = """{"code":"workspace-suspended","message":"This workspace is suspended for review.","reference":"SUS-ABC234","supportEmail":"help@example.com"}""";

    [Theory]
    [InlineData("approvals", "/api/w/ws_test/approvals")]
    [InlineData("deadletters list", "/api/w/ws_test/deadletters")]
    [InlineData("machine setup git", ServiceRoutes.ToolSetupStart)]
    [InlineData("doctor", "/api/w/ws_test/machines/mch_test")]
    public async Task Commands_show_suspension_reference_and_support(string command, string path)
    {
        Assert.SkipWhen(InsideRun.Now && command is "deadletters list" or "machine setup git", InsideRun.MbusReason);
        using var machine = SignedIn();
        if (command == "doctor") machine.Env["MBUS_API_KEY"] = "mbk_test";
        machine.Server.On(command == "machine setup git" ? HttpMethod.Post : HttpMethod.Get, path,
            _ => new(HttpStatusCode.Forbidden) { Content = new StringContent(Refusal, Encoding.UTF8, "application/json") });
        var result = await machine.RunAsync(command.Split(' '));
        var output = result.Error + result.Output;
        Assert.Contains("SUS-ABC234", output);
        Assert.Contains("help@example.com", output);
        Assert.DoesNotContain("This workspace is suspended: This workspace is suspended", output);
        if (command == "doctor") Assert.Contains("suspended:", output);
    }

    [Fact]
    public async Task Script_command_preserves_mcp_refusal_and_has_a_distinct_exit_code()
    {
        using var machine = SignedIn();
        machine.Env["MBUS_API_KEY"] = "mbk_test";
        machine.Server.On(HttpMethod.Post, ApiRoutes.Mcp, request =>
        {
            var message = JsonNode.Parse(request.BodyText)!;
            if (message["id"] is not { } id) return new(HttpStatusCode.Accepted);
            if (message["method"]!.GetValue<string>() is not ("initialize" or "tools/call"))
                return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } }.ToJsonString(), Encoding.UTF8, "application/json") };
            JsonObject result = message["method"]!.GetValue<string>() == "initialize"
                ? new() { ["protocolVersion"] = message["params"]!["protocolVersion"]!.DeepClone(), ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }, ["serverInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" } }
                : new() { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = """{"code":"workspace-suspended","message":"This workspace is suspended for review. Reference: SUS-ABC234. Contact: help@example.com"}""" }) };
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString(), Encoding.UTF8, "application/json") };
        });
        var run = await machine.RunAsync("threads", "--json");
        Assert.True(run.ExitCode == MessageBus.Cli.Scripts.ScriptExitCodes.WorkspaceSuspended, run.Error);
        Assert.Contains("SUS-ABC234", run.Error);
        Assert.Contains("help@example.com", run.Error);
        Assert.Contains("workspace-suspended", run.Output);
    }

    private static TestMachine SignedIn()
    {
        var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = FakeServer.Url, WorkspaceId = "ws_test", MachineId = "mch_test" });
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        store.SaveKeyPem(key.ExportPkcs8PrivateKeyPem());
        store.SaveSession(new CliSessionFile { Server = FakeServer.Url, AccessToken = "test-access", RefreshToken = "test-refresh", AccessExpiresAt = machine.Time.GetUtcNow().AddHours(1) });
        return machine;
    }
}
