using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Server;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class AttachmentTransferGateTests
{
    private const string Origin = "https://eu.attachments.example.test";
    private const string Legacy = """{"product":"messagebus.ai","signIn":{"microsoft":true}}""";
    private const string Isolated = """{"product":"messagebus.ai","stamp":{"stampId":"eu","geography":"eu","azureRegion":"westeurope","url":"https://eu.attachments.example.test","aliases":[]},"signIn":{"authority":{"version":1,"stampId":"eu","issuer":"https://eu.attachments.example.test","audience":"https://eu.attachments.example.test"},"machineSignature":{"version":2,"required":true}}}""";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Script_and_local_MCP_attachment_factories_inherit_gate_and_refuse_stripping_after_issuance(bool script)
    {
        using var f = new Fixture(false);
        var path = f.Machine.WriteHomeFile("fixture.txt", "fixture plaintext");
        var spec = new AttachSpec { Format = AttachmentFormat.File, Path = path };
        var options = LocalServer.Options(f.Api, "ws_test", null, f.Store.Directory,
            Stream.Null, Stream.Null, TextWriter.Null, "fixture") with { Links = f.Links };
        if (script)
        {
            await using var session = new ScriptSession(options);
            Assert.True((await session.AttachAsync(f.UploadSpec.Target, spec, null, Ct)).Succeeded);
            f.AfterLink = () => f.Discovery = Legacy;
            var refused = await session.AttachAsync(f.UploadSpec.Target, spec, null, Ct);
            Assert.Equal(McpErrorCodes.Forbidden, refused.Error?.Code);
            Assert.False(refused.Error?.Retryable);
        }
        else
        {
            await using var tools = new LocalTools(options);
            var result = await tools.AttachAsync(f.UploadSpec.Target, spec, null, Ct);
            Assert.False(result.IsError == true, result.StructuredContent.ToString());
            f.AfterLink = () => f.Discovery = Legacy;
            var refused = await tools.AttachAsync(f.UploadSpec.Target, spec, null, Ct);
            Assert.True(refused.IsError);
            Assert.Equal(McpErrorCodes.Forbidden, refused.StructuredContent!.Value.GetProperty("code").GetString());
        }
        Assert.Single(f.Transfers);
        Assert.All(f.Transfers, r => Assert.Null(r.Header("Authorization")));
    }

    [Fact]
    public async Task Runner_machine_bundle_factory_inherits_gate_for_supplied_links_before_download()
    {
        using var f = new Fixture(true);
        using var bundles = new MachineBundles(f.Api, f.Links, "ws_test", null, f.Store.Directory, TextWriter.Null);
        var bytes = "fixture code"u8.ToArray();
        f.SetCiphertext(bytes);
        var reference = Reference(bytes);
        var delivery = new TaskDelivery
        {
            DeliveryId = "dlv_fixture", Attempt = 1, LeaseExpiresAt = DateTimeOffset.MaxValue, AgentId = "agt_fixture",
            HeartbeatSeconds = 30, RunEnvironment = new RunEnvironment { Checkout = CheckoutMode.Empty },
            Links = [new DownloadLink { Id = reference.Id, Url = f.Link, ExpiresAt = DateTimeOffset.MaxValue }],
            Envelope = new MessageEnvelope
            {
                Id = "msg_fixture", Type = "task.implement", Source = "agent://fixture", Time = DateTimeOffset.UnixEpoch,
                WorkspaceId = "ws_test", ThreadId = "thr_fixture", Target = new AgentTarget { AgentId = "agt_fixture" },
                Sender = new SenderIdentity { Kind = SenderKind.Agent, AgentId = "agt_sender" },
                Body = new BodyRef { SizeBytes = 0, Sha256 = new string('0', 64) },
            },
        };
        var path = await bundles.FetchAsync(delivery, reference, f.Machine.Home, Ct);
        Assert.NotNull(path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Ct));
        f.Discovery = Legacy;
        Assert.Null(await bundles.FetchAsync(delivery, reference, f.Machine.Home, Ct));
        Assert.Single(f.Transfers);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Actual_factory_upload_download_roundtrip_uses_resolved_link_without_API_headers(bool environment, bool absolute)
    {
        using var f = new Fixture(environment);
        f.Link = absolute ? Origin + "/gateway/api/blob?sig=fixture" : "api/blob?sig=fixture";
        var reference = await f.Upload();
        using var output = new MemoryStream();
        await f.Attachments.DownloadAsync(reference, new DownloadLink { Id = reference.Id, Url = f.Link, ExpiresAt = DateTimeOffset.MaxValue }, output, f.Ring, Ct);
        Assert.Equal("fixture plaintext", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(2, f.Transfers.Count);
        Assert.All(f.Transfers, r =>
        {
            Assert.Equal(Origin + "/gateway/api/blob?sig=fixture", r.Uri.AbsoluteUri);
            Assert.DoesNotContain(r.Headers.Keys, name => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("X-Mbus-", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        });
        Assert.All(f.Anonymous, r => Assert.Empty(r.Headers));
        Assert.Equal(3, f.Anonymous.Count); // upload-link, upload, and download each require a fresh observation.
        Assert.NotEmpty(f.Transfers[0].Body);
        Assert.NotEqual("fixture plaintext", f.Transfers[0].BodyText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stripping_after_link_issuance_and_after_restart_refuses_actual_transfer(bool environment)
    {
        using var f = new Fixture(environment);
        f.AfterLink = () => f.Discovery = Legacy;
        await Assert.ThrowsAsync<AttachmentTransferException>(f.Upload);
        Assert.Empty(f.Transfers);
        using var restarted = f.CreateApi();
        var attachments = new AttachmentClient(restarted, f.Links, "ws_test");
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<AttachmentTransferException>(() => attachments.DownloadAsync(Reference([]),
            new DownloadLink { Id = "att_fixture", Url = f.Link, ExpiresAt = DateTimeOffset.MaxValue }, output, f.Ring, Ct));
        Assert.Empty(f.Transfers);
    }

    [Theory]
    [InlineData("https://ca.attachments.example.test/gateway/api/blob?sig=fixture")]
    [InlineData("https://example.blob.core.windows.net/file?sig=fixture")]
    [InlineData("https://eu.attachments.example.test/outside?sig=fixture")]
    [InlineData("https://eu.attachments.example.test/gateway/%2foutside?sig=fixture")]
    [InlineData("http://eu.attachments.example.test/gateway/api/blob?sig=fixture")]
    [InlineData("https://user@eu.attachments.example.test/gateway/api/blob?sig=fixture")]
    [InlineData("https://eu.attachments.example.test/gateway/api/blob?sig=fixture#fragment")]
    [InlineData("https://eu.attachments.example.test\\gateway\\api\\blob?sig=fixture")]
    [InlineData("file:///fixture")]
    public async Task Unsafe_or_foreign_isolated_link_never_reaches_transport(string link)
    {
        using var f = new Fixture(false);
        f.Link = link;
        await Assert.ThrowsAsync<AttachmentTransferException>(f.Upload);
        Assert.NotEmpty(f.Anonymous);
        Assert.Empty(f.Transfers);
    }

    [Fact]
    public async Task Never_pinned_old_server_keeps_direct_SAS_without_new_discovery_fields_or_catalogue()
    {
        using var f = new Fixture(false);
        f.Discovery = Legacy;
        f.Link = "https://storage.example.test/container/file?sig=fixture";
        await f.Upload();
        Assert.Equal(f.Link, Assert.Single(f.Transfers).Uri.AbsoluteUri);
        Assert.All(f.Anonymous, r => Assert.Equal("/gateway/api/server", r.Uri.AbsolutePath));
        Assert.False(File.Exists(f.Store.ServerBindingsPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transfer_still_refuses_ciphertext_size_or_hash_damage_before_opening(bool tooLarge)
    {
        using var f = new Fixture(false);
        var reference = await f.Upload();
        if (tooLarge) reference = reference with { SizeBytes = reference.SizeBytes - 1 };
        else reference = reference with { Sha256 = new string('0', 64) };
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<AttachmentTransferException>(() => f.Attachments.DownloadAsync(reference,
            new DownloadLink { Id = reference.Id, Url = f.Link, ExpiresAt = DateTimeOffset.MaxValue }, output, f.Ring, Ct));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task Defaults_added_during_binding_preparation_and_transport_diagnostics_never_escape()
    {
        using var api = new HttpClient { BaseAddress = new Uri(Origin + "/gateway/") };
        var contacted = false;
        using var links = new HttpClient(new AsyncHandler((_, _) =>
        {
            contacted = true;
            throw new HttpRequestException("secret query sig=fixture must not appear");
        }));
        AttachmentTransfers.Bind(api, (_, send, ct) =>
        {
            links.DefaultRequestHeaders.Add("X-Api-Key", "fixture");
            return send(ct);
        });
        var attachments = new AttachmentClient(api, links, "ws_test");
        using var ring = new KeyRing();
        using var output = new MemoryStream();
        var link = new DownloadLink { Id = "att_fixture", Url = "api/blob?sig=fixture", ExpiresAt = DateTimeOffset.MaxValue };
        await Assert.ThrowsAsync<AttachmentTransferException>(() => attachments.DownloadAsync(Reference([]), link, output, ring, Ct));
        Assert.False(contacted);
        using var unbound = new HttpClient { BaseAddress = api.BaseAddress };
        links.DefaultRequestHeaders.Clear();
        var error = await Assert.ThrowsAsync<AttachmentTransferException>(() => new AttachmentClient(unbound, links, "ws_test")
            .DownloadAsync(Reference([]), link, output, ring, Ct));
        Assert.DoesNotContain("sig=", error.ToString());
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Mbus-Workspace")]
    [InlineData("X-Api-Key")]
    [InlineData("Cookie")]
    public async Task Caller_defaults_are_refused_without_mutating_shared_client(string header)
    {
        using var f = new Fixture(false);
        f.Links.DefaultRequestHeaders.TryAddWithoutValidation(header, "fixture");
        await Assert.ThrowsAsync<AttachmentTransferException>(f.Upload);
        Assert.Empty(f.Transfers);
        Assert.True(f.Links.DefaultRequestHeaders.Contains(header));
    }

    [Fact(Timeout = 30_000)]
    public async Task Actual_send_keeps_profile_lock_reserved_until_transport_completes()
    {
        using var f = new Fixture(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var links = new HttpClient(new AsyncHandler(async (_, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return new(HttpStatusCode.Created);
        }));
        var attachment = new AttachmentClient(f.Api, links, "ws_test");
        var upload = attachment.UploadAsync(new MemoryStream("data"u8.ToArray()), f.UploadSpec, f.Recipient, Ct);
        await entered.Task.WaitAsync(Ct);
        var competing = f.Store.WithProfileSecurityLockAsync(tx => Task.FromResult(tx.LoadBindingFloor()), Ct);
        try { Assert.False(competing.IsCompleted); }
        finally { release.TrySetResult(); }
        await upload;
        Assert.NotEmpty((await competing).Bindings);
    }

    public static TheoryData<int, bool, bool, bool> RedirectCases => new(
        from status in new[] { 301, 302, 303, 307, 308 }
        from upload in new[] { false, true }
        from sameOrigin in new[] { false, true }
        from environment in new[] { false, true }
        select (status, upload, sameOrigin, environment));

    [Theory]
    [MemberData(nameof(RedirectCases))]
    public async Task Real_transfer_client_never_replays_signed_query_or_upload_body(int status, bool upload, bool sameOrigin, bool environment)
    {
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        source.Start(); target.Start();
        var origin = $"http://127.0.0.1:{((IPEndPoint)source.LocalEndpoint).Port}";
        var redirect = sameOrigin ? origin + "/replayed?sig=fixture"
            : $"http://127.0.0.1:{((IPEndPoint)target.LocalEndpoint).Port}/replayed?sig=fixture";
        using var machine = new TestMachine();
        machine.Env[EnvVars.Server] = origin;
        machine.Env[EnvVars.ApiKey] = "fixture-key";
        machine.Env[EnvVars.Home] = machine.MbusFolder;
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = origin, WorkspaceId = "ws_test" });
        var discoveries = 0;
        HttpMessageHandler Handler() => new AsyncHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/server")
            {
                Assert.Empty(request.Headers);
                discoveries++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Legacy) });
            }
            Assert.Equal("MbusKey", request.Headers.Authorization?.Scheme);
            return Task.FromResult(FakeServer.Json(new UploadLink
            { AttachmentId = "att_fixture", Url = origin + "/blob?sig=fixture", MaxBytes = long.MaxValue, ExpiresAt = DateTimeOffset.MaxValue }));
        });
        using var api = environment
            ? new EnvironmentRunnerHostContext(name => machine.Env.GetValueOrDefault(name), machine.Home, Handler).CreateServiceClient()
            : ServerConnection.CreateAuthenticatedClient(Host(machine.CreateHost(), Handler), new CliSettings
            { Stored = store.Load(), Server = new Uri(origin), ApiKey = "fixture-key", MachineName = "fixture" }, store, "cli");
        using var links = AttachmentTransfers.CreateHttpClient();
        links.Timeout = TimeSpan.FromSeconds(5);
        using var pair = KeyPair.GenerateP256();
        using var ring = new KeyRing();
        var attachments = new AttachmentClient(api, links, "ws_test");
        var serving = Task.Run(async () =>
        {
            using var socket = await source.AcceptTcpClientAsync(Ct);
            using var stream = socket.GetStream();
            // Read through headers without consuming buffered upload bytes.
            var bytes = new List<byte>();
            var one = new byte[1];
            while (!Encoding.ASCII.GetString(bytes.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
            { Assert.Equal(1, await stream.ReadAsync(one, Ct)); bytes.Add(one[0]); }
            var headers = Encoding.ASCII.GetString(bytes.ToArray());
            Assert.StartsWith((upload ? "PUT" : "GET") + " /blob?sig=fixture HTTP/1.1\r\n", headers);
            Assert.DoesNotContain("Authorization:", headers, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("X-Mbus-", headers, StringComparison.OrdinalIgnoreCase);
            var lengthLine = headers.Split("\r\n").FirstOrDefault(s => s.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            if (lengthLine is not null)
            {
                var body = new byte[int.Parse(lengthLine.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture)];
                await stream.ReadExactlyAsync(body, Ct);
                if (upload) Assert.NotEmpty(body);
            }
            var response = $"HTTP/1.1 {status} Redirect\r\nLocation: {redirect}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), Ct);
        }, Ct);
        var operation = upload
            ? (Task)attachments.UploadAsync(new MemoryStream("fixture"u8.ToArray()), new(new AgentTarget { AgentId = "agt_fixture" }, "fixture", AttachmentFormat.File), Recipient(pair), Ct)
            : attachments.DownloadAsync(Reference([]), new DownloadLink { Id = "att_fixture", Url = origin + "/blob?sig=fixture", ExpiresAt = DateTimeOffset.MaxValue }, new MemoryStream(), ring, Ct);
        var error = await Assert.ThrowsAsync<AttachmentTransferException>(() => operation);
        Assert.Equal((HttpStatusCode)status, error.Status);
        await serving;
        Assert.False(source.Pending());
        Assert.False(target.Pending());
        Assert.Equal(upload ? 2 : 1, discoveries);
    }

    private static AttachmentRef Reference(byte[] bytes) => new()
    { Id = "att_fixture", Name = "fixture", Format = AttachmentFormat.File, SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
    private static PublicKeyInfo Recipient(KeyPair pair) => new()
    { KeyId = "ek_fixture", Scope = KeyScope.Workspace, Version = 1, Algorithm = pair.PublicKey.Algorithm,
        PublicKey = pair.PublicKey.ToBase64(), Fingerprint = pair.PublicKey.Fingerprint, CreatedAt = DateTimeOffset.UnixEpoch };

    private sealed class Fixture : IDisposable
    {
        private readonly bool _environment;
        public TestMachine Machine { get; } = new();
        private readonly KeyPair _pair = KeyPair.GenerateP256();
        public KeyRing Ring { get; } = new();
        public ConfigStore Store { get; }
        public HttpClient Api { get; }
        public HttpClient Links { get; }
        public AttachmentClient Attachments { get; }
        public PublicKeyInfo Recipient => AttachmentTransferGateTests.Recipient(_pair);
        public AttachmentUpload UploadSpec { get; } = new(new AgentTarget { AgentId = "agt_fixture" }, "fixture", AttachmentFormat.File);
        public string Discovery { get; set; } = Isolated;
        public string Link { get; set; } = "api/blob?sig=fixture";
        public Action? AfterLink { get; set; }
        public List<RecordedRequest> Anonymous { get; } = [];
        public List<RecordedRequest> Transfers { get; } = [];
        private byte[] _ciphertext = [];
        public void SetCiphertext(byte[] bytes) => _ciphertext = bytes;
        public Fixture(bool environment)
        {
            _environment = environment;
            Machine.Env[EnvVars.Server] = Origin + "/gateway";
            Machine.Env[EnvVars.ApiKey] = "fixture-key";
            Machine.Env[EnvVars.Home] = Machine.MbusFolder;
            Store = new ConfigStore(Machine.MbusFolder, "file");
            Store.Save(new CliConfig { Server = Origin + "/gateway", WorkspaceId = "ws_test" });
            Ring.Add(Recipient.KeyId, KeyPair.FromPem(_pair.ExportPem()));
            Links = new HttpClient(new AsyncHandler(async (request, ct) =>
            {
                var recorded = await Record(request, ct); Transfers.Add(recorded);
                if (request.Method == HttpMethod.Put) _ciphertext = recorded.Body;
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(_ciphertext) };
            }));
            Api = CreateApi();
            Attachments = new AttachmentClient(Api, Links, "ws_test");
        }
        public HttpClient CreateApi()
        {
            if (_environment) return new EnvironmentRunnerHostContext(name => Machine.Env.GetValueOrDefault(name), Machine.Home, Handler).CreateServiceClient();
            return ServerConnection.CreateAuthenticatedClient(Host(Machine.CreateHost(), Handler), new CliSettings
            { Stored = Store.Load(), Server = new Uri(Origin + "/gateway"), ApiKey = "fixture-key", MachineName = "fixture" }, Store, "mcp");
        }
        private HttpMessageHandler Handler() => new AsyncHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/server", StringComparison.Ordinal))
            { Anonymous.Add(await Record(request, ct)); return new(HttpStatusCode.OK) { Content = new StringContent(Discovery) }; }
            Assert.Equal("MbusKey", request.Headers.Authorization?.Scheme);
            if (request.RequestUri.AbsolutePath.EndsWith("/api/caller", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"subjectId":"fixture","permissions":[]}""") };
            if (request.RequestUri.AbsolutePath.EndsWith("/keys", StringComparison.Ordinal))
                return FakeServer.Json<IReadOnlyList<PublicKeyInfo>>([Recipient]);
            if (request.RequestUri.AbsolutePath.EndsWith("/keys/rotation", StringComparison.Ordinal))
                return FakeServer.Json<IReadOnlyList<KeyRotationState>>([new() { Scope = KeyScope.Workspace, Key = Recipient }]);
            Assert.EndsWith("/attachments/upload-link", request.RequestUri.AbsolutePath);
            AfterLink?.Invoke();
            return FakeServer.Json(new UploadLink { AttachmentId = "att_fixture", Url = Link, MaxBytes = long.MaxValue, ExpiresAt = DateTimeOffset.MaxValue });
        });
        public Task<AttachmentRef> Upload() => Attachments.UploadAsync(new MemoryStream("fixture plaintext"u8.ToArray()), UploadSpec, Recipient, Ct);
        public void Dispose() { Api.Dispose(); Links.Dispose(); Ring.Dispose(); _pair.Dispose(); Machine.Dispose(); }
    }
    private static CliHost Host(CliHost basis, Func<HttpMessageHandler> handler) => new()
    {
        Out = basis.Out, Error = basis.Error, In = basis.In, OpenStandardInput = basis.OpenStandardInput,
        OpenStandardOutput = basis.OpenStandardOutput, GetEnvironmentVariable = basis.GetEnvironmentVariable,
        HomeDirectory = basis.HomeDirectory, MachineName = basis.MachineName, MbusCommand = basis.MbusCommand,
        Browser = basis.Browser, Skills = basis.Skills, McpServer = basis.McpServer, CreateHttpHandler = handler,
    };
    private static async Task<RecordedRequest> Record(HttpRequestMessage request, CancellationToken ct) => new(request.Method, request.RequestUri!,
        request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
        request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct));
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
