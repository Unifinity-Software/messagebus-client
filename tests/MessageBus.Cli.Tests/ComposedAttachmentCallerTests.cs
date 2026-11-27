using System.Collections.Concurrent;
using System.CommandLine;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Keys;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Server;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MessageBus.Tests.Shared;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed partial class ComposedAttachmentCallerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Legacy = """{"product":"messagebus.ai","signIn":{"microsoft":true},"capabilities":[]}""";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Public_socket_factory_roundtrip_preserves_ciphertext_and_plaintext(bool environment, bool absolute, bool directBlob = false)
    {
        await using var f = new Fixture(directBlob: directBlob);
        using var api = environment
            ? new EnvironmentRunnerHostContext(n => f.Machine.Env.GetValueOrDefault(n), f.Machine.Home, f.Handler).CreateServiceClient()
            : f.Api();
        using var links = AttachmentTransfers.CreateHttpClient();
        var attachments = new AttachmentClient(api, links, "ws_test");
        f.AbsoluteLink = absolute;
        var reference = await attachments.UploadAsync(new MemoryStream("attachment plaintext"u8.ToArray()),
            new(new AgentTarget { AgentId = "agt_reader" }, "file.txt", AttachmentFormat.File), f.WorkspaceKey, Ct);
        using var output = new MemoryStream();
        await attachments.DownloadAsync(reference, f.Download(reference.Id), output, f.Ring, Ct);
        Assert.Equal("attachment plaintext", Encoding.UTF8.GetString(output.ToArray()));
        f.AssertCiphertext(reference);
        Assert.Equal(new[] { HttpMethod.Put, HttpMethod.Get }, f.Blobs.Select(r => r.Method));
        Assert.All(f.Blobs, f.AssertBlob);
        Assert.False(File.Exists(f.Store.ServerBindingsPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadletters_command_reseals_body_and_attachment_and_checks_binding_after_link(bool isolated)
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MachineKeyReason);
        await using var f = new Fixture(isolated);
        f.Machine.Env.Remove(EnvVars.ApiKey);
        var source = await f.SealSource();
        var body = BodyCrypto.Seal(new MessageBody { Summary = "recover the original answer" }, f.DeviceKey);
        var letter = new DeadLetter
        {
            Id = "dead_fixture", Role = "writer", Reason = DeadLetterReason.Expired,
            DeadLetteredAt = DateTimeOffset.UnixEpoch, ExpiresAt = DateTimeOffset.MaxValue,
            Envelope = f.Delivery.Envelope with
            {
                Target = new RoleTarget { Role = "writer" }, Body = body.ToSealedBody().Body,
                Encryption = body.Encryption, Attachments = [source],
            },
        };
        f.Machine.Server.On(HttpMethod.Get, "/gateway/api/w/ws_test/deadletters", _ => FakeServer.Json<IReadOnlyList<DeadLetter>>([letter]));
        ReplayDeadLetterInput? replayed = null;
        f.Machine.Server.On(HttpMethod.Post, "/gateway/api/w/ws_test/deadletters/dead_fixture/replay", r =>
        { replayed = MessageBusJson.Deserialize<ReplayDeadLetterInput>(r.BodyText); return new(HttpStatusCode.OK); });
        var options = new CommonOptions();
        var root = new RootCommand { options.Server, DeadLettersCommand.Create(f.Host, options) };
        if (isolated)
        {
            // DeadLetters owns its public socket transport. Refuse after key/body recovery and
            // link issuance, before that transport can contact the isolated URI.
            f.AfterDownloadLink = () => f.Discovery = Legacy;
            Assert.Equal(1, await root.Parse(["deadletters", "replay", "dead_fixture", "--to-role", "reader"]).InvokeAsync(cancellationToken: Ct));
            Assert.Empty(f.Blobs);
            Assert.Null(replayed);
            f.AssertStrippedWithoutSend();
            return;
        }
        Assert.Equal(0, await root.Parse(["deadletters", "replay", "dead_fixture", "--to-role", "reader"]).InvokeAsync(cancellationToken: Ct));
        Assert.NotNull(replayed);
        Assert.Equal("reader", replayed.ToRole);
        var moved = Assert.Single(replayed.Attachments!);
        f.AssertCiphertext(moved, f.RoleKey);
        Assert.Equal(f.RoleKey.KeyId, moved.Encryption!.KeyId);
        Assert.Equal("attachment plaintext", await f.Open(moved));
        var sealedReply = replayed.SealedBody!;
        Assert.Equal(f.RoleKey.KeyId, sealedReply.Encryption.KeyId);
        Assert.Equal("recover the original answer", BodyCrypto.Open(Convert.FromBase64String(sealedReply.Body.Data!), sealedReply.Encryption, f.Ring).Summary);
        Assert.All(f.Blobs, f.AssertBlob);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_stdio_factory_uploads_and_fetches_decrypted_file_and_refuses_fresh_stripping(bool isolated)
    {
        await using var f = new Fixture(isolated);
        using var api = f.Api();
        var input = new Pipe();
        var output = new Pipe();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        // A real stdio MCP transport; the isolated fixture supplies only the terminal blob transport.
        var options = f.Options(api) with
        {
            Input = input.Reader.AsStream(), Output = output.Writer.AsStream(),
            Links = isolated ? f.TestLinks : null,
        };
        var serving = LocalServer.RunAsync(options, stop.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(input.Writer.AsStream(), output.Reader.AsStream()), cancellationToken: Ct);
            var result = await Call(client, McpToolNames.Settle, f.SettleWithFile());
            Assert.False(result.IsError == true, result.StructuredContent + f.Diagnostics);
            var sent = Assert.Single(f.Settles);
            var reference = Assert.Single(sent.Attachments!);
            f.AssertCiphertext(reference);
            Assert.Equal("kept answer payload", f.OpenReply(sent).Summary);
            var fetched = await Call(client, McpToolNames.FetchAttachment, new FetchAttachmentInput
            { ThreadId = f.Delivery.Envelope.ThreadId, DeliveryId = f.Delivery.DeliveryId, AttachmentId = reference.Id, Path = Path.Combine(f.Run.Root, "fetched.txt") });
            Assert.False(fetched.IsError == true, fetched.StructuredContent + f.Diagnostics);
            Assert.Equal("attachment plaintext", await File.ReadAllTextAsync(Path.Combine(f.Run.Root, "fetched.txt"), Ct));
            Assert.All(f.Blobs, f.AssertBlob);
            if (isolated)
            {
                var transfers = f.Blobs.Count;
                f.AfterUploadLink = () => f.Discovery = Legacy;
                // Clear the previous local settlement so this attempt exercises packaging again.
                File.Delete(f.Run.SettleFile);
                var refused = await Call(client, McpToolNames.Settle, f.SettleWithFile());
                Assert.True(refused.IsError);
                Assert.Equal(transfers, f.Blobs.Count);
                Assert.Single(f.Settles);
                f.AssertStrippedWithoutSend();
            }
        }
        finally { await stop.CancelAsync(); await serving; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Kept_answer_actual_factories_preserve_sealed_reply_and_gate_final_POST(bool runnerFactory, bool isolated)
    {
        await using var f = new Fixture(isolated);
        using var api = f.Api();
        var kept = f.SettleWithFile() with
        {
            Attach = [], Reply = null,
            SealedReply = BodyCrypto.Seal(new MessageBody { Summary = "kept answer payload" }, f.WorkspaceKey).ToSealedBody(),
        };
        await File.WriteAllTextAsync(KeptAnswer.KeptFile(f.Run.SettleFile), MessageBusJson.Serialize(kept), Ct);
        var sender = runnerFactory ? RunToolsServer.KeptAnswerSender(f.Context)! : (_, _, ct) => LocalServer.SendKeptAnswerAsync(f.Options(api), ct);
        Assert.True(await sender(f.Run, f.Delivery, Ct));
        var sent = Assert.Single(f.Settles);
        Assert.True(sent.ReplyOnly);
        Assert.Equal("kept answer payload", f.OpenReply(sent).Summary);
        Assert.Null(sent.Attempt); // This old-shaped capability response never enables the new attempt field.
        Assert.False(File.Exists(KeptAnswer.KeptFile(f.Run.SettleFile)));
        Assert.True(File.Exists(f.Run.SettleFile));
        if (!isolated) return;
        File.Delete(f.Run.SettleFile);
        await File.WriteAllTextAsync(KeptAnswer.KeptFile(f.Run.SettleFile), MessageBusJson.Serialize(kept), Ct);
        // A separate LocalTools instance must revalidate even though the same factory and
        // profile already sent a valid kept answer and hold its binding snapshot.
        f.StripBeforeMcpPost = true;
        await Assert.ThrowsAnyAsync<Exception>(() => sender(f.Run, f.Delivery, Ct));
        Assert.Single(f.Settles);
        Assert.False(File.Exists(f.Run.SettleFile));
        Assert.True(File.Exists(KeptAnswer.KeptFile(f.Run.SettleFile)));
        f.AssertStrippedWithoutSend();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runner_tools_actual_factory_packages_file_or_refuses_stripping_after_link(bool isolated)
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MachineKeyReason);
        await using var f = new Fixture(isolated, machineAuthentication: true);
        var tools = RunToolsServer.Create(f.Context)!;
        await using var session = await tools.StartAsync(new RunToolsRequest(f.Run, f.Delivery, new PermissionProfile { Name = "worker" }, p => p), Ct);
        if (isolated) f.AfterUploadLink = () => f.Discovery = Legacy;
        var json = JsonSerializer.SerializeToNode(f.SettleWithFile(), MessageBusJson.Options)!.AsObject();
        var result = await PipeRunTools.CallAsync(session.Variables[RunToolsVariables.File], McpToolNames.Settle, json, Ct);
        if (isolated)
        {
            Assert.False(result.Succeeded);
            Assert.Empty(f.Blobs);
            Assert.Empty(f.Settles);
            f.AssertStrippedWithoutSend();
            return;
        }
        Assert.True(result.Succeeded, result.Error?.Message + f.Diagnostics);
        var sent = Assert.Single(f.Settles);
        var reference = Assert.Single(sent.Attachments!);
        f.AssertCiphertext(reference);
        Assert.Equal("attachment plaintext", await f.Open(reference));
        Assert.Equal("kept answer payload", f.OpenReply(sent).Summary);
        Assert.All(f.Blobs, f.AssertBlob);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Code_return_Create_uploads_exact_git_head_or_refuses_after_link(bool isolated)
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MachineKeyReason);
        Assert.SkipWhen(CommandLocator.Find("git", Environment.GetEnvironmentVariable, []) is null, "git isn't installed.");
        await using var f = new Fixture(isolated, machineAuthentication: true);
        await Git.RunAsync(f.Run.WorkTree, ["init", "--quiet", "--initial-branch=main"], Ct);
        await File.WriteAllTextAsync(Path.Combine(f.Run.WorkTree, "change.txt"), "returned code", Ct);
        await Git.RunAsync(f.Run.WorkTree, ["add", "."], Ct);
        await Git.RunAsync(f.Run.WorkTree, ["-c", "user.name=Test", "-c", "user.email=test@example.test", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Fixture"], Ct);
        var head = await Git.CommitIdAsync(f.Run.WorkTree, "HEAD", Ct);
        var send = RunCodeReturn.Create(f.Context)!;
        if (isolated)
        {
            // The static production Links client must remain untouched on a binding refusal.
            f.AfterUploadLink = () => f.Discovery = Legacy;
            await Assert.ThrowsAsync<AttachmentTransferException>(() => send(f.Run, f.Delivery, "main", null, Ct));
            Assert.Empty(f.Blobs);
            Assert.Empty(f.Settles);
            f.AssertStrippedWithoutSend();
            return;
        }
        Assert.True(await send(f.Run, f.Delivery, "main", null, Ct));
        var sent = Assert.Single(f.Settles);
        Assert.Equal(head, Assert.Single(sent.Result.Refs).Sha);
        var reference = Assert.Single(sent.Attachments!);
        f.AssertCiphertext(reference);
        var bundle = Path.Combine(f.Run.Root, "returned.bundle");
        await File.WriteAllBytesAsync(bundle, await f.OpenBytes(reference), Ct);
        Assert.Equal(head + " refs/heads/main", (await Git.RunAsync(f.Run.WorkTree, ["bundle", "list-heads", bundle], Ct)).Trim());
        Assert.NotNull(f.OpenReply(sent).Summary);
        Assert.All(f.Blobs, f.AssertBlob);
    }

    private static Task<CallToolResult> Call<T>(McpClient client, string name, T input)
    {
        var json = JsonSerializer.SerializeToElement(input, MessageBusJson.Options);
        return client.CallToolAsync(new CallToolRequestParams
        { Name = name, Arguments = json.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) }, Ct).AsTask();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        private readonly Task _serving;
        private readonly KeyPair _device = KeyPair.GenerateP256();
        private readonly KeyPair _recipient = KeyPair.GenerateP256();
        private readonly KeyPair _role = KeyPair.GenerateP256();
        private readonly bool _isolated;
        private readonly ConcurrentDictionary<string, byte[]> _ciphertexts = new();
        private readonly Dictionary<string, UploadLinkRequest> _uploads = new();
        private readonly ConcurrentQueue<RecordedRequest> _apiRequests = new();
        private readonly ConcurrentQueue<Exception> _fixtureFailures = new();
        private int _next;
        private string _last = "att_source";
        public TestMachine Machine { get; } = new();
        public ConfigStore Store { get; }
        public RunWorkspace Run { get; }
        public TaskDelivery Delivery { get; }
        public KeyRing Ring { get; } = new();
        public PublicKeyInfo WorkspaceKey { get; }
        public PublicKeyInfo RoleKey { get; }
        public PublicKeyInfo DeviceKey { get; }
        public string MachineId { get; }
        public string Diagnostics => string.Join(Environment.NewLine, _fixtureFailures.Select(e => e.ToString()));
        public string Endpoint { get; }
        private string BlobEndpoint { get; }
        public string Discovery { get; set; }
        public bool AbsoluteLink { get; set; }
        public bool StripBeforeMcpPost { get; set; }
        public int StrippedObservations { get; private set; }
        private int? _credentialsAtStripping;
        public Action? AfterUploadLink { get; set; }
        public Action? AfterDownloadLink { get; set; }
        public ConcurrentQueue<RecordedRequest> Blobs { get; } = new();
        public List<SettleInput> Settles { get; } = [];
        public CliHost Host { get; }
        public CommandContext Context { get; }
        public HttpClient TestLinks { get; }

        public Fixture(bool isolated = false, bool directBlob = false, bool machineAuthentication = false)
        {
            _isolated = isolated;
            _listener.Start();
            BlobEndpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/gateway";
            Endpoint = isolated ? "https://eu.composed.example.test/gateway" : directBlob ? "http://legacy.composed.example.test/gateway" : BlobEndpoint;
            if (isolated) BlobEndpoint = Endpoint;
            var origin = new Uri(Endpoint).GetLeftPart(UriPartial.Authority);
            Discovery = isolated ? $$$"""{"product":"messagebus.ai","stamp":{"stampId":"eu","geography":"eu","azureRegion":"westeurope","url":"{{{origin}}}","aliases":[]},"signIn":{"authority":{"version":1,"stampId":"eu","issuer":"{{{origin}}}","audience":"{{{origin}}}"},"machineSignature":{"version":2,"required":true}},"capabilities":[]}""" : Legacy;
            Machine.Env[EnvVars.Server] = Endpoint;
            Machine.Env[EnvVars.Home] = Machine.MbusFolder;
            Machine.Env[EnvVars.ApiKey] = "fixture-key";
            if (machineAuthentication) Machine.Env.Remove(EnvVars.ApiKey);
            Machine.Env[ConfigStore.KeyStoreVariable] = "file";
            Store = new ConfigStore(Machine.MbusFolder, "file");
            MachineId = ContractIds.New(ContractIds.Machine, Machine.Time.GetUtcNow());
            Store.Save(new CliConfig { Server = Endpoint, WorkspaceId = "ws_test", MachineId = MachineId });
            Store.SaveKeyPem(_device.ExportPem());
            WorkspaceKey = Key(_recipient, "ek_workspace", KeyScope.Workspace);
            RoleKey = Key(_role, "ek_reader", KeyScope.Role, "reader");
            DeviceKey = Key(_device, "ek_device", KeyScope.Machine, MachineId);
            Ring.Add(WorkspaceKey.KeyId, KeyPair.FromPem(_recipient.ExportPem()));
            Ring.Add(RoleKey.KeyId, KeyPair.FromPem(_role.ExportPem()));
            Ring.Add(DeviceKey.KeyId, KeyPair.FromPem(_device.ExportPem()));
            Run = RunWorkspace.Create(Path.Combine(Machine.Home, "runs"), "dlv_composed", 1);
            Directory.CreateDirectory(Run.WorkTree);
            File.WriteAllText(Path.Combine(Run.WorkTree, "file.txt"), "attachment plaintext");
            Delivery = new TaskDelivery
            {
                DeliveryId = "dlv_composed", Attempt = 3, AgentId = "agt_fixture", HeartbeatSeconds = 30, LeaseExpiresAt = DateTimeOffset.MaxValue,
                RunEnvironment = new RunEnvironment { Checkout = CheckoutMode.Empty },
                Envelope = new MessageEnvelope
                {
                    Id = "msg_fixture", Type = "task.implement", Source = "agent://fixture", Time = DateTimeOffset.UnixEpoch,
                    WorkspaceId = "ws_test", ThreadId = "thr_composed", Target = new AgentTarget { AgentId = "agt_fixture" },
                    Sender = new SenderIdentity { Kind = SenderKind.Person, SubjectId = "person_fixture" },
                    Body = new BodyRef { SizeBytes = 0, Sha256 = new string('0', 64) },
                },
            };
            var basis = Machine.CreateHost();
            Host = new CliHost
            {
                Out = basis.Out, Error = basis.Error, In = basis.In, OpenStandardInput = basis.OpenStandardInput,
                OpenStandardOutput = basis.OpenStandardOutput, GetEnvironmentVariable = basis.GetEnvironmentVariable,
                HomeDirectory = basis.HomeDirectory, MachineName = basis.MachineName, MbusCommand = basis.MbusCommand,
                Browser = basis.Browser, Skills = basis.Skills, McpServer = basis.McpServer, Time = Machine.Time,
                CreateHttpHandler = Handler, CreateDiscoveryHttpHandler = () => new AsyncHandler(async (request, ct) =>
                {
                    Assert.Empty(request.Headers);
                    if (Discovery == Legacy && isolated)
                    {
                        StrippedObservations++;
                        _credentialsAtStripping ??= _apiRequests.Count;
                    }
                    return await Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Discovery) });
                }),
            };
            var common = new CommonOptions();
            Context = CommandContext.Load(Host, new RootCommand { common.Server }.Parse([]), common);
            TestLinks = AttachmentTransfers.CreateTestHttpClient(new AsyncHandler(async (request, ct) =>
            {
                var recorded = new RecordedRequest(request.Method, request.RequestUri!, request.Headers.ToDictionary(p => p.Key, p => string.Join(",", p.Value), StringComparer.OrdinalIgnoreCase),
                    request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct));
                return Blob(recorded);
            }));
            Routes();
            _serving = Serve();
        }

        public HttpMessageHandler Handler() => new AsyncHandler(async (request, ct) =>
        {
            try { return await RespondAsync(request, ct); }
            catch (Exception ex)
            {
                // LocalTools returns a short MCP error. Preserve the original fixture assertion
                // and stack so a failing composed test identifies the actual wire mismatch.
                _fixtureFailures.Enqueue(ex);
                throw;
            }
        });
        private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.Authorization is not null)
            {
                Assert.Equal(new Uri(Endpoint).Authority, request.RequestUri!.Authority);
                Assert.StartsWith("/gateway/", request.RequestUri.AbsolutePath);
                _apiRequests.Enqueue(new RecordedRequest(request.Method, request.RequestUri,
                    request.Headers.ToDictionary(p => p.Key, p => string.Join(",", p.Value), StringComparer.OrdinalIgnoreCase),
                    request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct)));
                if (request.Headers.Authorization.Scheme == "MbusMachine")
                {
                    var version = request.Headers.TryGetValues("X-Mbus-Signature-Version", out var values) ? Assert.Single(values) : null;
                    Assert.Equal(_isolated ? "2" : null, version);
                }
            }
            if (request.RequestUri!.AbsolutePath == "/gateway/api/server")
            {
                // Environment-runner discovery uses this factory as its dedicated anonymous transport.
                if (request.Headers.Authorization is null) Assert.Empty(request.Headers);
                var observed = Discovery;
                // The kept-answer capability lookup succeeds, but cannot authorize the next MCP POST.
                if (StripBeforeMcpPost && request.Headers.Authorization is not null) Discovery = Legacy;
                return new(HttpStatusCode.OK) { Content = new StringContent(observed) };
            }
            Assert.NotNull(request.Headers.Authorization);
            using var invoker = new HttpMessageInvoker(Machine.Server, false);
            return await invoker.SendAsync(request, ct);
        }
        public HttpClient Api() => ServerConnection.CreateAuthenticatedClient(Host, Context.Settings, Store, "mcp");
        public LocalServerOptions Options(HttpClient api) => LocalServer.Options(api, "ws_test", MachineId, Store.Directory,
            Stream.Null, Stream.Null, TextWriter.Null, "fixture", _ => null) with
        {
            Delivery = Delivery, SettleFile = Run.SettleFile, Scope = RunToolScope.For(Delivery, Run.Root),
            WorkingDirectory = Run.WorkTree, TempDirectory = Run.Root, MachineKeyPem = Store.LoadKeyPem, Time = Machine.Time,
        };
        public SettleInput SettleWithFile() => new()
        {
            ThreadId = Delivery.Envelope.ThreadId, DeliveryId = Delivery.DeliveryId,
            Result = new SettleResult { Status = SettleStatus.Succeeded }, Reply = new MessageBody { Summary = "kept answer payload" },
            Attach = [new AttachSpec { Format = AttachmentFormat.File, Path = Path.Combine(Run.WorkTree, "file.txt") }],
        };
        private string Link(string id) => AbsoluteLink || BlobEndpoint != Endpoint
            ? BlobEndpoint + "/api/blob/" + id + "?sig=fixture" : "api/blob/" + id + "?sig=fixture";
        public DownloadLink Download(string id) => new() { Id = id, Url = Link(id), ExpiresAt = DateTimeOffset.MaxValue };
        private void Routes()
        {
            Machine.Server.On(HttpMethod.Get, "/gateway/api/caller", _ => Json("""{"subjectId":"person_fixture","permissions":[]}"""));
            Machine.Server.On(HttpMethod.Get, "/gateway/api/w/ws_test/keys", _ => FakeServer.Json<IReadOnlyList<PublicKeyInfo>>([DeviceKey, WorkspaceKey, RoleKey]));
            var grant = GrantCrypto.Create("ws_test", WorkspaceKey, _recipient, DeviceKey, DeviceKey, _device, "person_fixture", Machine.Time.GetUtcNow());
            Machine.Server.On(HttpMethod.Get, "/gateway/api/w/ws_test/keys/grants", _ => FakeServer.Json<IReadOnlyList<KeyGrant>>([grant]));
            Machine.Server.On(HttpMethod.Get, "/gateway/api/w/ws_test/keys/rotation", r =>
            {
                KeyRotationState workspace = new() { Scope = KeyScope.Workspace, Key = WorkspaceKey };
                KeyRotationState reader = new() { Scope = KeyScope.Role, Owner = "reader", Key = RoleKey };
                // The service filters this route. Returning workspace first for a role query
                // made RecipientAsync select the wrong key despite a valid role directory entry.
                return FakeServer.Json<IReadOnlyList<KeyRotationState>>(r.Uri.Query switch
                {
                    "" => [workspace, reader],
                    "?scope=workspace" => [workspace],
                    "?scope=role&owner=reader" => [reader],
                    _ => throw new InvalidOperationException("Unexpected key rotation query: " + r.Uri.Query),
                });
            });
            Machine.Server.On(HttpMethod.Get, "/gateway/api/w/ws_test/threads/thr_composed/messages", _ =>
                Json(new JsonArray(JsonSerializer.SerializeToNode(Delivery.Envelope with
                { Attachments = Settles.LastOrDefault()?.Attachments ?? [] }, MessageBusJson.Options)).ToJsonString()));
            Machine.Server.On(HttpMethod.Post, "/gateway/api/w/ws_test/deliveries/dlv_composed/attachment-links", _ =>
                FakeServer.Json<IReadOnlyList<DownloadLink>>((Settles.LastOrDefault()?.Attachments ?? []).Select(a => Download(a.Id)).ToArray()));
            Machine.Server.On(HttpMethod.Post, "/gateway/api/w/ws_test/attachments/upload-link", r =>
            {
                var upload = MessageBusJson.Deserialize<UploadLinkRequest>(r.BodyText)!;
                Assert.True(upload.SizeBytes > 0);
                var id = "att_" + Interlocked.Increment(ref _next);
                _last = id;
                _uploads[id] = upload;
                Machine.Server.On(HttpMethod.Post, "/gateway/api/w/ws_test/attachments/" + id + "/download-link", _ =>
                { AfterDownloadLink?.Invoke(); return FakeServer.Json(Download(id)); });
                AfterUploadLink?.Invoke();
                return FakeServer.Json(new UploadLink { AttachmentId = _last, Url = Link(_last), MaxBytes = long.MaxValue, ExpiresAt = DateTimeOffset.MaxValue });
            });
            Machine.Server.On(HttpMethod.Post, "/gateway/api/w/ws_test/attachments/att_source/download-link", _ =>
            { AfterDownloadLink?.Invoke(); return FakeServer.Json(Download("att_source")); });
            Machine.Server.On(HttpMethod.Post, "/gateway/mcp", r =>
            {
                var request = JsonNode.Parse(r.BodyText)!;
                if (request["id"] is null) return new(HttpStatusCode.Accepted);
                var method = (string?)request["method"];
                // Current SDKs probe discovery before initialize. This legacy-shaped MCP
                // fixture refuses that optional method exactly as the live older service does.
                if (method == "server/discover")
                    return Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(),
                        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } }.ToJsonString());
                JsonNode result;
                if (method == "initialize") result = JsonNode.Parse("""{"protocolVersion":"2025-11-25","serverInfo":{"name":"fixture","version":"1"},"capabilities":{"tools":{}}}""")!;
                else if (method == "tools/list") result = JsonNode.Parse("""{"tools":[]}""")!;
                else
                {
                    Assert.Equal("tools/call", method);
                    Assert.Equal(McpToolNames.Settle, (string?)request["params"]?["name"]);
                    Settles.Add(MessageBusJson.Deserialize<SettleInput>(request["params"]!["arguments"]!.ToJsonString())!);
                    result = JsonNode.Parse("""{"content":[{"type":"text","text":"{\"status\":\"working\",\"replyMessageId\":\"msg_reply\"}"}]}""")!;
                }
                return Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
            });
        }
        public async Task<AttachmentRef> SealSource()
        {
            using var plain = new MemoryStream("attachment plaintext"u8.ToArray());
            using var cipher = new MemoryStream();
            var sealedFile = await MessageCrypto.SealAsync(plain, cipher, DeviceKey, Ct);
            _ciphertexts["att_source"] = cipher.ToArray();
            return new AttachmentRef { Id = "att_source", Name = "file.txt", Format = AttachmentFormat.File,
                SizeBytes = sealedFile.CiphertextLength, Sha256 = sealedFile.Sha256, Encryption = sealedFile.Encryption };
        }
        public void AssertCiphertext(AttachmentRef reference, PublicKeyInfo? recipient = null)
        {
            Assert.Equal((recipient ?? WorkspaceKey).KeyId, reference.Encryption!.KeyId);
            var bytes = _ciphertexts[reference.Id];
            Assert.Equal(reference.SizeBytes, bytes.LongLength);
            Assert.Equal(reference.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.Equal(reference.SizeBytes, _uploads[reference.Id].SizeBytes);
            Assert.Equal(reference.Sha256, _uploads[reference.Id].Sha256);
            Assert.NotEqual("attachment plaintext", Encoding.UTF8.GetString(bytes));
        }
        public void AssertBlob(RecordedRequest r)
        {
            Assert.Equal(new Uri(BlobEndpoint).Authority, r.Uri.Authority);
            Assert.StartsWith("/gateway/api/blob/", r.Uri.AbsolutePath);
            Assert.Equal("?sig=fixture", r.Uri.Query);
            Assert.DoesNotContain(r.Headers.Keys, h => h.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || h.StartsWith("X-Mbus-", StringComparison.OrdinalIgnoreCase) || h.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                || h.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase) || h.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase));
        }
        public void AssertStrippedWithoutSend()
        {
            Assert.True(StrippedObservations > 0);
            Assert.Equal(_credentialsAtStripping, _apiRequests.Count);
        }
        public MessageBody OpenReply(SettleInput input)
        {
            Assert.Equal(WorkspaceKey.KeyId, input.SealedReply!.Encryption.KeyId);
            return BodyCrypto.Open(Convert.FromBase64String(input.SealedReply.Body.Data!), input.SealedReply.Encryption, Ring);
        }
        public async Task<string> Open(AttachmentRef reference) => Encoding.UTF8.GetString(await OpenBytes(reference));
        public async Task<byte[]> OpenBytes(AttachmentRef reference)
        {
            using var cipher = new MemoryStream(_ciphertexts[reference.Id]);
            using var plain = new MemoryStream();
            await MessageCrypto.OpenAsync(cipher, plain, reference.Encryption!, Ring, Ct);
            return plain.ToArray();
        }
        private HttpResponseMessage Blob(RecordedRequest request)
        {
            Blobs.Enqueue(request);
            var id = request.Uri.Segments.Last();
            if (request.Method == HttpMethod.Put) _ciphertexts[id] = request.Body;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(_ciphertexts[id]) };
        }
        private async Task Serve()
        {
            try
            {
                while (true)
                {
                    using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var stream = socket.GetStream();
                    var bytes = new List<byte>();
                    var one = new byte[1];
                    while (!Encoding.ASCII.GetString(bytes.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    { await stream.ReadExactlyAsync(one, _stop.Token); bytes.Add(one[0]); }
                    var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
                    var first = lines[0].Split(' ');
                    var headers = lines.Skip(1).Where(l => l.Contains(':')).Select(l => l.Split(':', 2)).ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    var body = new byte[headers.TryGetValue("Content-Length", out var length) ? int.Parse(length, System.Globalization.CultureInfo.InvariantCulture) : 0];
                    await stream.ReadExactlyAsync(body, _stop.Token);
                    var actualHost = headers["Host"];
                    Assert.Equal(new Uri(BlobEndpoint).Authority, actualHost);
                    using var response = Blob(new RecordedRequest(new HttpMethod(first[0]), new Uri("http://" + actualHost + first[1]), headers, body));
                    var content = await response.Content.ReadAsByteArrayAsync(_stop.Token);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(content, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _serving;
            _listener.Stop(); _stop.Dispose(); TestLinks.Dispose(); Run.Dispose(); Ring.Dispose(); _device.Dispose(); _recipient.Dispose(); _role.Dispose(); Machine.Dispose();
        }
        private static PublicKeyInfo Key(KeyPair pair, string id, KeyScope scope, string? owner = null) => new()
        { KeyId = id, Scope = scope, Owner = owner, Version = 1, Algorithm = pair.PublicKey.Algorithm,
            PublicKey = pair.PublicKey.ToBase64(), Fingerprint = pair.PublicKey.Fingerprint, CreatedAt = DateTimeOffset.UnixEpoch };
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
