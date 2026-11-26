using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class ExhibitsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Route = "/api/w/ws_test/jobs/job_test/steps/design/exhibits";
    private static StepContext Step(bool locked = false) => new()
    {
        JobId = "job_test", StepId = "design", StepName = "Design", Round = 2,
        Inputs = [], AllowedOutcomes = ["done"], KeepExhibitsLocked = locked,
    };

    [Fact]
    public async Task Tool_uploads_captioned_file_and_carries_ref_across_connections_into_settle()
    {
        using var h = new ApprovalImpactTests.Harness();
        await using var runner = new RunnerHarness();
        using var run = RunWorkspace.Create(h.Machine.Home, "dlv_exhibit", 1);
        Directory.CreateDirectory(run.WorkTree);
        Support(h.Machine.Server);
        var delivery = runner.Task("dlv_exhibit") with { StepContext = Step() };
        var options = h.Options with
        {
            Delivery = delivery, SettleFile = run.SettleFile, WorkingDirectory = run.WorkTree,
            Scope = RunToolScope.For(delivery, run.Root), TempDirectory = run.Root,
        };
        var tools = new PipeRunTools(async (request, stream, ct) =>
            await LocalServer.RunAsync(request.WithExhibits(options with { Input = stream, Output = stream }), ct), TextWriter.Null);
        await using var session = await tools.StartAsync(new RunToolsRequest(run, delivery, new PermissionProfile { Name = "worker" }, p => p), Ct);
        await File.WriteAllTextAsync(Path.Combine(run.WorkTree, "plan.md"), "Proposed implementation", Ct);
        var result = await PipeRunTools.CallAsync(session.Variables[RunToolsVariables.File], "add_exhibit",
            new JsonObject { ["path"] = "plan.md", ["caption"] = "Implementation plan", ["kind"] = "plan" }, Ct);
        Assert.True(result.Succeeded, result.Error?.Message);
        var uploaded = h.Machine.Server.Single(HttpMethod.Post, Route);
        using var metadata = Metadata(uploaded);
        Assert.Equal("Implementation plan", metadata.RootElement.GetProperty("caption").GetString());
        Assert.Equal("plan", metadata.RootElement.GetProperty("kind").GetString());
        Assert.Equal(2, metadata.RootElement.GetProperty("round").GetInt32());
        Assert.False(metadata.RootElement.GetProperty("locked").GetBoolean());
        Assert.Equal("Proposed implementation", Encoding.UTF8.GetString(Content(uploaded)));
        Assert.Equal("MbusKey test", uploaded.Header("Authorization"));

        var settled = await PipeRunTools.CallAsync(session.Variables[RunToolsVariables.File], McpToolNames.Settle,
            new JsonObject { ["threadId"] = delivery.Envelope.ThreadId, ["result"] = new JsonObject
            {
                ["status"] = "succeeded", ["refs"] = new JsonArray(new JsonObject
                { ["kind"] = "exhibit", ["id"] = "ex_test", ["exhibit"] = new JsonObject { ["name"] = "wrong.txt", ["mediaType"] = "text/plain", ["size"] = 1 } }),
            } }, Ct);
        Assert.True(settled.Succeeded, settled.Error?.Message);
        var recorded = MessageBusJson.Deserialize<SettleInput>(await File.ReadAllTextAsync(run.SettleFile, Ct))!;
        var reference = Assert.Single(recorded.Result.Refs);
        Assert.Equal(RefKinds.Exhibit, reference.Kind);
        Assert.Equal("ex_test", reference.Id);
        Assert.Equal(new ExhibitRef { Name = "plan.md", MediaType = "text/markdown", Size = 23, Caption = "Implementation plan" }, reference.Exhibit);
    }

    [Fact]
    public async Task Locked_folder_pickup_sends_self_describing_ciphertext_that_opens_with_workspace_key()
    {
        using var machine = new TestMachine();
        using var http = new HttpClient(machine.Server, false) { BaseAddress = new Uri(FakeServer.Url + "/") };
        using var key = KeyPair.GenerateP256();
        var info = new PublicKeyInfo { KeyId = "ek_workspace", Scope = KeyScope.Workspace, Owner = "-", Version = 1,
            Algorithm = key.Algorithm, PublicKey = key.PublicKey.ToBase64(), Fingerprint = key.PublicKey.Fingerprint, CreatedAt = DateTimeOffset.UnixEpoch };
        using var ring = new KeyRing().Add(info.KeyId, key);
        Support(machine.Server);
        var exhibits = new Exhibits(http, "ws_test", Step(true), machine.Home, _ => Task.FromResult(info), machine.Home);
        var folder = Directory.CreateDirectory(Path.Combine(machine.Home, ".mbus", "exhibits")).FullName;
        var original = Encoding.UTF8.GetBytes("A private workflow diagram");
        await File.WriteAllBytesAsync(Path.Combine(folder, "diagram.svg"), original, Ct);
        var reference = Assert.Single(await exhibits.UploadFolderAsync(folder, Ct));
        Assert.Equal("diagram.svg", reference.Exhibit!.Caption);
        var uploaded = machine.Server.Single(HttpMethod.Post, Route);
        using var metadata = Metadata(uploaded);
        Assert.True(metadata.RootElement.GetProperty("locked").GetBoolean());
        Assert.Equal(original.Length, metadata.RootElement.GetProperty("size").GetInt64());
        var bytes = Content(uploaded);
        Assert.StartsWith("mbus-sealed-exhibit/1\n", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("A private workflow diagram", uploaded.BodyText);
        using var input = new MemoryStream(bytes);
        Assert.Equal("mbus-sealed-exhibit/1", Encoding.ASCII.GetString((await PipeRunTools.ReadLineAsync(input, 100, Ct))!));
        var encryption = MessageBusJson.Deserialize<EncryptionInfo>(Encoding.UTF8.GetString((await PipeRunTools.ReadLineAsync(input, 16384, Ct))!))!;
        Assert.Equal(info.KeyId, encryption.KeyId);
        using var opened = new MemoryStream();
        await MessageCrypto.OpenAsync(input, opened, encryption, ring, Ct);
        Assert.Equal(original, opened.ToArray());
        Assert.Empty(Directory.EnumerateFiles(machine.Home, "exhibit-*"));
    }

    [Theory]
    [InlineData("report.txt", false)]
    [InlineData("report.json", true)]
    [InlineData("report.svg", false)]
    [InlineData("report.unknown", true)]
    public async Task Secret_in_text_is_refused_before_any_upload(string name, bool locked)
    {
        using var machine = new TestMachine();
        using var http = new HttpClient(machine.Server, false) { BaseAddress = new Uri(FakeServer.Url + "/") };
        var exhibits = new Exhibits(http, "ws_test", Step(locked), machine.Home,
            _ => throw new InvalidOperationException("Must scan before resolving keys"), machine.Home);
        await File.WriteAllTextAsync(Path.Combine(machine.Home, name), "Credential: ghp_" + new string('A', 36), Ct);
        var error = await Assert.ThrowsAsync<McpToolException>(() => exhibits.UploadAsync(name, ct: Ct));
        Assert.Equal(McpErrorCodes.InvalidInput, error.Error.Code);
        Assert.Contains("github-token", error.Message);
        Assert.Empty(machine.Server.Requests);
        Assert.Empty(exhibits.References);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Service_size_refusal_is_preserved_without_a_client_limit(bool json)
    {
        using var machine = new TestMachine();
        using var http = new HttpClient(machine.Server, false) { BaseAddress = new Uri(FakeServer.Url + "/") };
        Support(machine.Server);
        const string message = "This step allows exhibits up to 12 bytes.";
        machine.Server.On(HttpMethod.Post, Route, _ => new(HttpStatusCode.RequestEntityTooLarge)
        { Content = new StringContent(json ? "{\"message\":\"" + message + "\"}" : message) });
        var exhibits = new Exhibits(http, "ws_test", Step(), machine.Home, _ => throw new InvalidOperationException(), machine.Home);
        await File.WriteAllTextAsync(Path.Combine(machine.Home, "report.txt"), "A document longer than 12 bytes.", Ct);
        var error = await Assert.ThrowsAsync<McpToolException>(() => exhibits.UploadAsync("report.txt", ct: Ct));
        Assert.Equal(message, error.Error.Message);
        Assert.Equal(McpErrorCodes.TooLarge, error.Error.Code);
        machine.Server.Single(HttpMethod.Post, Route);
        Assert.Empty(exhibits.References);
    }

    [Fact]
    public async Task Tool_requires_workflow_context_and_paths_inside_the_working_folder()
    {
        using var h = new ApprovalImpactTests.Harness();
        await using var runner = new RunnerHarness();
        using var run = RunWorkspace.Create(h.Machine.Home, "dlv_exhibit", 1);
        Directory.CreateDirectory(run.WorkTree);
        var delivery = runner.Task("dlv_exhibit");
        var request = new RunToolsRequest(run, delivery, new PermissionProfile { Name = "worker" }, p => p);
        var options = request.WithExhibits(h.Options with { TempDirectory = run.Root });
        Assert.Null(options.AdditionalRunTool);
        await using var tools = new LocalTools(options);
        var result = await ApprovalImpactTests.CallAsync(tools, "add_exhibit", """{"path":"report.txt"}""");
        Assert.True(result.IsError);
        Assert.Contains("Exhibits belong to workflow steps.", result.StructuredContent.ToString());
        var exhibits = new Exhibits(h.Http, "ws_test", Step(), run.WorkTree, _ => throw new InvalidOperationException(), run.Root);
        await File.WriteAllTextAsync(Path.Combine(run.Root, "outside.txt"), "Outside work folder", Ct);
        await Assert.ThrowsAsync<McpToolException>(() => exhibits.UploadAsync("../outside.txt", ct: Ct));
        Assert.Empty(h.Machine.Server.Requests);
    }

    private static void Support(FakeServer server)
    {
        server.On(HttpMethod.Get, "/api/server", _ => FakeServer.Json(new ServerCapabilitiesResponse
        { Product = "messagebus.ai", Capabilities = [WorkflowCapabilities.Workflows] }));
        server.On(HttpMethod.Post, Route, _ => new(HttpStatusCode.Created) { Content = new StringContent("""{"id":"ex_test"}""") });
    }

    private static JsonDocument Metadata(RecordedRequest request)
    {
        var text = request.BodyText;
        var start = text.IndexOf('{');
        return JsonDocument.Parse(text[start..text.IndexOf("\r\n", start, StringComparison.Ordinal)]);
    }

    private static byte[] Content(RecordedRequest request)
    {
        var text = Encoding.Latin1.GetString(request.Body);
        var part = text.IndexOf("name=content", StringComparison.Ordinal);
        if (part < 0) part = text.IndexOf("name=\"content\"", StringComparison.Ordinal);
        Assert.True(part >= 0, text);
        var start = text.IndexOf("\r\n\r\n", part, StringComparison.Ordinal) + 4;
        var end = text.LastIndexOf("\r\n--", StringComparison.Ordinal);
        return request.Body[start..end];
    }
}
