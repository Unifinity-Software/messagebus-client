using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Security;
using MessageBus.Cli.Server;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Http;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class ServerSendGateTests
{
    private const string Origin = "https://eu.stamps.example.test";
    private const string Machine = "mch_00000000000000000000000000";
    private const string Legacy = """{"product":"messagebus.ai","signIn":{"microsoft":true},"stamp":{"stampId":"eu","geography":"eu","azureRegion":"westeurope","url":"https://eu.stamps.example.test","aliases":[]}}""";
    private const string Isolated = """{"product":"messagebus.ai","stamp":{"stampId":"eu","geography":"eu","azureRegion":"westeurope","url":"https://eu.stamps.example.test","aliases":[]},"signIn":{"authority":{"version":1,"stampId":"eu","issuer":"https://eu.stamps.example.test","audience":"https://eu.stamps.example.test"},"machineSignature":{"version":2,"required":true}}}""";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Actual_pipeline_signs_final_target_body_and_actual_workspace_after_anonymous_durable_preparation(int workspaceSource)
    {
        using var fixture = new Fixture();
        fixture.Discovery = Isolated;
        if (workspaceSource == 2) fixture.Store.Save(fixture.Store.Load() with { WorkspaceId = null });
        fixture.OnAuthenticated = _ =>
        {
            Assert.True(File.Exists(fixture.Store.ServerBindingsPath));
            // Transport runs under the profile lock. Read fixture bytes rather than reacquiring it.
            using var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fixture.Store.ConfigPath));
            Assert.Equal(Origin + "/gateway/", config.RootElement.GetProperty("serverBinding").GetProperty("selectedEndpoint").GetString());
            return new(HttpStatusCode.OK);
        };
        using var client = fixture.Client();
        var body = "{ \"x\": 1 }\n"u8.ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/items/a%20b?x=%2F&y=one+two") { Content = new ByteArrayContent(body) };
        if (workspaceSource == 1) request.Headers.Add(MbusHeaders.Workspace, "w_actual");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var sent = Assert.Single(fixture.Authenticated);
        var workspace = workspaceSource == 2 ? null : workspaceSource == 1 ? "w_actual" : "w_saved";
        Assert.Equal(workspace, sent.Header(MbusHeaders.Workspace));
        Assert.Equal("2", sent.Header(MbusHeaders.SignatureVersion));
        Assert.Equal("/gateway/api/items/a%20b?x=%2F&y=one+two", sent.Uri.PathAndQuery);
        Assert.Equal(body, sent.Body);
        // Independent verifier assembles the frozen literal domain, without either production canonical builder.
        var canonical = string.Join('\n', "mbus-machine-v2", Origin, "eu", Machine, workspace ?? "", "POST",
            "/gateway/api/items/a%20b?x=%2F&y=one+two", sent.Header(MbusHeaders.Timestamp), sent.Header(MbusHeaders.Nonce),
            Convert.ToHexStringLower(SHA256.HashData(body)));
        Assert.True(Verify(fixture.Key, sent, canonical));
        var mutated = canonical.Split('\n');
        mutated[4] = "w_foreign";
        Assert.False(Verify(fixture.Key, sent, string.Join('\n', mutated)));
        Assert.False(Verify(fixture.Key, sent, canonical.Replace(Origin, "https://ca.stamps.example.test", StringComparison.Ordinal)));
        var anonymous = Assert.Single(fixture.Anonymous);
        Assert.Equal("/gateway/api/server", anonymous.Uri.AbsolutePath);
        Assert.Empty(anonymous.Headers);
        Assert.Empty(anonymous.Body);
    }

    [Fact]
    public async Task Explicit_transaction_prepares_without_reacquiring_and_foreign_store_context_refuses_before_discovery()
    {
        using var fixture = new Fixture();
        using var other = new Fixture();
        fixture.Discovery = Isolated;
        using var gate = new ServerSendGate(fixture.Host(), fixture.Store, new Uri(Origin + "/gateway"));
        var target = new Uri(Origin + "/gateway/api/items");
        await other.Store.WithProfileSecurityLockAsync(async transaction =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => gate.PrepareSendAsync(target,
                TestContext.Current.CancellationToken, transaction));
            return true;
        }, TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Anonymous);
        await fixture.Store.WithSessionLockAsync(async transaction =>
        {
            var prepared = await gate.PrepareSendAsync(target, TestContext.Current.CancellationToken, transaction);
            Assert.NotNull(prepared.Binding);
            Assert.NotNull(transaction.Load().ServerBinding);
            await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.ConsumeAsync(target,
                _ => Task.FromResult(true), TestContext.Current.CancellationToken));
            await prepared.ConsumeAsync(target, _ => Task.FromResult(true), TestContext.Current.CancellationToken, transaction);
            return true;
        }, TestContext.Current.CancellationToken);
        Assert.Single(fixture.Anonymous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_send_rediscovering_blocks_stripping_even_for_reused_clients_and_api_keys(bool apiKey)
    {
        using var fixture = new Fixture();
        fixture.Discovery = Isolated;
        using var client = fixture.Client(apiKey);
        using var first = await client.GetAsync("api/first", TestContext.Current.CancellationToken);
        fixture.Discovery = Legacy;
        await Assert.ThrowsAsync<ServerBindingException>(() => client.GetAsync("api/second", TestContext.Current.CancellationToken));
        Assert.Single(fixture.Authenticated);
        Assert.Equal(2, fixture.Anonymous.Count);
        Assert.All(fixture.Anonymous, request => Assert.Empty(request.Headers));
        // Restarting the client and store cannot erase the learned floor.
        using var restarted = ServerConnection.CreateAuthenticatedClient(fixture.Host(), fixture.Settings(apiKey),
            new ConfigStore(fixture.Store.Directory, "file"), "mcp");
        await Assert.ThrowsAsync<ServerBindingException>(() => restarted.GetAsync("mcp", TestContext.Current.CancellationToken));
        Assert.Single(fixture.Authenticated);
    }

    [Fact]
    public async Task Never_pinned_descriptive_legacy_keeps_exact_v1_and_discovery_outage_never_grants_a_send()
    {
        using var fixture = new Fixture();
        using var client = fixture.Client();
        using var response = await client.GetAsync("mcp?x=%2F", TestContext.Current.CancellationToken);
        var sent = Assert.Single(fixture.Authenticated);
        Assert.Null(sent.Header(MbusHeaders.SignatureVersion));
        var canonical = string.Join('\n', "GET", "/gateway/mcp?x=%2F", sent.Header(MbusHeaders.Timestamp),
            sent.Header(MbusHeaders.Nonce), Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())));
        Assert.True(Verify(fixture.Key, sent, canonical));
        fixture.DiscoveryStatus = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<ServerBindingException>(() => client.GetAsync("api/no-send", TestContext.Current.CancellationToken));
        Assert.Single(fixture.Authenticated);
    }

    [Theory]
    [InlineData("https://foreign.example.test/gateway/api/items")]
    [InlineData(Origin + "/other/api/items")]
    public async Task Off_endpoint_absolute_requests_refuse_before_discovery_or_machine_headers(string destination)
    {
        using var fixture = new Fixture();
        using var client = fixture.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, destination) { Content = new StringContent("owned-fixture-body") };
        await Assert.ThrowsAsync<ServerBindingException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Anonymous);
        Assert.Empty(fixture.Authenticated);
        Assert.Empty(request.Headers);
    }

    [Theory]
    [InlineData("https://foreign.example.test/gateway")]
    [InlineData(Origin + "/different-base")]
    public void Server_override_refuses_before_key_load_and_does_not_reuse_equal_api_key_fingerprint(string destination)
    {
        using var fixture = new Fixture();
        const string key = "fixture-api-key";
        fixture.Machine.Env[EnvVars.ApiKey] = key;
        var stored = fixture.Store.Load() with { EnrolledWithApiKey = MessageBus.Cli.Runner.RunnerKeyEnrollment.Fingerprint(key) };
        var settings = CliSettings.Resolve(fixture.Host(), stored, destination);
        Assert.Equal(key, settings.ApiKey);
        // A key-load attempt would fail with CliException instead of the terminal endpoint refusal.
        File.WriteAllText(fixture.Store.KeyPath, "invalid fixture PEM");
        Assert.Throws<ServerBindingException>(() => ServerConnection.CreateSigner(fixture.Host(), settings, fixture.Store));
        Assert.Throws<ServerBindingException>(() => ServerConnection.CreateAuthenticatedClient(fixture.Host(), settings, fixture.Store, "mcp"));
        Assert.Empty(fixture.Anonymous);
        Assert.Empty(fixture.Authenticated);
    }

    [Fact]
    public async Task Provisioned_first_contact_stripping_and_missing_explicit_home_refuse_before_credentials()
    {
        using var fixture = new Fixture();
        fixture.Machine.Env[ServerBindingExpectation.EnvironmentVariable] = """{"version":1,"transportOrigin":"https://eu.stamps.example.test","stampId":"eu","issuer":"https://eu.stamps.example.test","audience":"https://eu.stamps.example.test","authorityVersion":1,"minimumMachineSignatureVersion":2,"machineSignatureRequired":true}""";
        using var client = fixture.Client(true);
        await Assert.ThrowsAsync<ServerBindingException>(() => client.GetAsync("api/no-home", TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Anonymous);
        fixture.Machine.Env[EnvVars.Home] = fixture.Store.Directory;
        await Assert.ThrowsAsync<ServerBindingException>(() => client.GetAsync("api/stripped", TestContext.Current.CancellationToken));
        Assert.Single(fixture.Anonymous);
        Assert.Empty(fixture.Authenticated);
    }

    [Fact]
    public async Task Failed_current_discovery_invalidates_outstanding_context_and_context_cannot_be_reused()
    {
        using var fixture = new Fixture();
        using var gate = new ServerSendGate(fixture.Host(), fixture.Store, new Uri(Origin + "/gateway"));
        using var secondGate = new ServerSendGate(fixture.Host(), fixture.Store, new Uri(Origin + "/other-base"));
        var target = new Uri(Origin + "/gateway/api/items");
        var prepared = await gate.PrepareSendAsync(target, TestContext.Current.CancellationToken);
        fixture.DiscoveryStatus = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<ServerBindingException>(() => secondGate.PrepareSendAsync(new Uri(Origin + "/other-base/api/items"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ServerBindingException>(() => prepared.ConsumeAsync(target,
            _ => Task.FromResult(true), TestContext.Current.CancellationToken));
        fixture.DiscoveryStatus = HttpStatusCode.OK;
        prepared = await gate.PrepareSendAsync(target, TestContext.Current.CancellationToken);
        await prepared.ConsumeAsync(target, _ => Task.FromResult(true), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ServerBindingException>(() => prepared.ConsumeAsync(target,
            _ => Task.FromResult(true), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Buffered_legacy_send_refuses_after_successful_pin_in_gate_or_separate_store(bool apiKey, bool pinThroughGate)
    {
        using var fixture = new Fixture();
        using var client = fixture.Client(apiKey);
        using var content = new BufferedBarrierContent(TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/items") { Content = content };
        var sending = client.SendAsync(request, TestContext.Current.CancellationToken);
        try
        {
            await content.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Single(fixture.Anonymous); // A has prepared legacy and is held in body buffering.
            fixture.Discovery = Isolated;
            if (pinThroughGate)
            {
                using var gateB = new ServerSendGate(fixture.Host(), fixture.Store, new Uri(Origin + "/gateway"));
                await gateB.PrepareSendAsync(new Uri(Origin + "/gateway/api/other"), TestContext.Current.CancellationToken);
            }
            else
            {
                // No ServerSendGate participates, so no in-process generation can change. This is
                // the durable publication another process using this profile would perform.
                var otherStore = new ConfigStore(fixture.Store.Directory, "file");
                await otherStore.WithProfileSecurityLockAsync(tx => Task.FromResult(tx.PrepareBinding(
                    ServerEndpoint.Parse(Origin + "/gateway"), ServerDiscoveryParser.Parse(Isolated))),
                    TestContext.Current.CancellationToken);
            }
            Assert.True(File.Exists(fixture.Store.ServerBindingsPath));
        }
        finally { content.Release.TrySetResult(); }
        await Assert.ThrowsAsync<ServerBindingException>(() => sending);
        Assert.Empty(fixture.Authenticated);
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains(MbusHeaders.Signature));
    }

    [Fact]
    public async Task Consumption_orders_actual_send_before_competing_durable_pin_and_rejects_escaped_signing()
    {
        using var fixture = new Fixture();
        using var gate = new ServerSendGate(fixture.Host(), fixture.Store, new Uri(Origin + "/gateway"));
        var target = new Uri(Origin + "/gateway/api/items");
        var prepared = await gate.PrepareSendAsync(target, TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServerSendAuthorization? escaped = null;
        var sending = prepared.ConsumeAsync(target, async authorization =>
        {
            escaped = authorization;
            entered.SetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            return new MachineRequestSigner(fixture.Key, Machine, TimeProvider.System).CreateHeaders(authorization, request, []);
        }, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var otherStore = new ConfigStore(fixture.Store.Directory, "file");
        var pin = otherStore.WithProfileSecurityLockAsync(tx => Task.FromResult(tx.PrepareBinding(
            ServerEndpoint.Parse(Origin + "/gateway"), ServerDiscoveryParser.Parse(Isolated))), TestContext.Current.CancellationToken);
        try { Assert.False(pin.IsCompleted); }
        finally { release.TrySetResult(); }
        var headers = await sending;
        Assert.DoesNotContain(headers, h => h.Key == MbusHeaders.SignatureVersion);
        await pin;
        using var later = new HttpRequestMessage(HttpMethod.Get, target);
        Assert.Throws<ServerBindingException>(() => new MachineRequestSigner(fixture.Key, Machine, TimeProvider.System)
            .CreateHeaders(escaped!, later, []));
    }

    [Fact]
    public async Task Explicit_consumption_reserves_transaction_against_concurrent_pin_and_waits_before_lock_release()
    {
        using var fixture = new Fixture();
        using var gate = new ServerSendGate(fixture.Host(), fixture.Store, new Uri(Origin + "/gateway"));
        var target = new Uri(Origin + "/gateway/api/items");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkedOperations = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? sending = null;
        var outer = fixture.Store.WithSessionLockAsync(async transaction =>
        {
            var prepared = await gate.PrepareSendAsync(target, TestContext.Current.CancellationToken, transaction);
            sending = prepared.ConsumeAsync(target, async _ =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(TestContext.Current.CancellationToken);
                return true;
            }, TestContext.Current.CancellationToken, transaction);
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Throws<InvalidOperationException>(() => transaction.PrepareBinding(
                ServerEndpoint.Parse(Origin + "/gateway"), ServerDiscoveryParser.Parse(Isolated)));
            Assert.Throws<InvalidOperationException>(() => transaction.LoadBindingFloor());
            checkedOperations.SetResult();
            // Even an accidentally unawaited send cannot let outer completion release its lock early.
            return true;
        }, TestContext.Current.CancellationToken);
        await checkedOperations.Task.WaitAsync(TestContext.Current.CancellationToken);
        try { Assert.False(outer.IsCompleted); }
        finally { release.TrySetResult(); }
        await outer;
        Assert.True(await sending!);
        Assert.Null(fixture.Store.Load().ServerBinding);
    }

    [Theory]
    [InlineData(301, false, false)]
    [InlineData(302, false, false)]
    [InlineData(303, false, false)]
    [InlineData(307, false, false)]
    [InlineData(308, false, false)]
    [InlineData(301, true, false)]
    [InlineData(302, true, false)]
    [InlineData(303, true, false)]
    [InlineData(307, true, false)]
    [InlineData(308, true, false)]
    [InlineData(301, false, true)]
    [InlineData(302, false, true)]
    [InlineData(303, false, true)]
    [InlineData(307, false, true)]
    [InlineData(308, false, true)]
    [InlineData(301, true, true)]
    [InlineData(302, true, true)]
    [InlineData(303, true, true)]
    [InlineData(307, true, true)]
    [InlineData(308, true, true)]
    public async Task Real_http_discovery_and_authenticated_redirects_never_contact_second_target(int status, bool machineCredential, bool sameOrigin)
    {
        using var fixture = new Fixture();
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetUrl = $"http://127.0.0.1:{((IPEndPoint)target.LocalEndpoint).Port}/stolen";
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var server = new Uri($"http://127.0.0.1:{((IPEndPoint)origin.LocalEndpoint).Port}");
        if (sameOrigin) targetUrl = new Uri(server, "/stolen").AbsoluteUri;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var redirectDiscovery = false;
        var requests = new List<string>();
        var serving = ServeAsync();
        try
        {
            fixture.Store.Save(new CliConfig
            {
                Server = server.AbsoluteUri, MachineId = machineCredential ? Machine : null,
                WorkspaceId = machineCredential ? "w_saved" : null,
            });
            var settings = fixture.Settings(!machineCredential) with { Server = server };
            using var client = ServerConnection.CreateAuthenticatedClient(fixture.Host(
                () => new SocketsHttpHandler(), () => new SocketsHttpHandler()), settings, fixture.Store, "cli");
            client.Timeout = TimeSpan.FromSeconds(5);
            await Assert.ThrowsAsync<ServerBindingException>(() => client.GetAsync("api/items", stop.Token));
            Assert.False(target.Pending());
            Assert.Equal(2, requests.Count); // anonymous discovery, then credential-bearing request, with no replay.
            redirectDiscovery = true;
            await Assert.ThrowsAsync<ServerBindingException>(() => client.GetAsync("api/items", stop.Token));
            Assert.False(target.Pending());
            Assert.Equal(3, requests.Count); // redirected discovery cannot authorize another credential send.
            Assert.DoesNotContain(requests, line => line.Contains("/stolen", StringComparison.Ordinal));
        }
        finally
        {
            stop.Cancel();
            origin.Stop();
            try { await serving; } catch (OperationCanceledException) { } catch (SocketException) { }
        }

        async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                using var connection = await origin.AcceptTcpClientAsync(stop.Token);
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var first = await reader.ReadLineAsync(stop.Token) ?? "";
                var headers = new List<string>();
                for (var line = await reader.ReadLineAsync(stop.Token); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync(stop.Token)) headers.Add(line);
                requests.Add(first);
                var discovery = first.StartsWith("GET /api/server ", StringComparison.Ordinal);
                if (discovery) Assert.DoesNotContain(headers, h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase) || h.StartsWith("X-Mbus-", StringComparison.OrdinalIgnoreCase));
                else Assert.Contains(headers, h => h == (machineCredential
                    ? "Authorization: MbusMachine " + Machine : "Authorization: MbusKey fixture-api-key"));
                var reply = discovery && !redirectDiscovery
                    ? $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(Legacy)}\r\nConnection: close\r\n\r\n{Legacy}"
                    : $"HTTP/1.1 {status} Redirect\r\nLocation: {targetUrl}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), stop.Token);
            }
        }
    }

    private static bool Verify(MachineKey key, RecordedRequest request, string canonical)
    {
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKeyBase64), out _);
        return verifier.VerifyData(Encoding.UTF8.GetBytes(canonical), Base64Url.DecodeFromChars(request.Header(MbusHeaders.Signature)!),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private sealed class Fixture : IDisposable
    {
        public TestMachine Machine { get; } = new();
        public MachineKey Key { get; } = MachineKey.Create();
        public ConfigStore Store { get; }
        public List<RecordedRequest> Anonymous { get; } = [];
        public List<RecordedRequest> Authenticated { get; } = [];
        public string Discovery { get; set; } = Legacy;
        public HttpStatusCode DiscoveryStatus { get; set; } = HttpStatusCode.OK;
        public Func<RecordedRequest, HttpResponseMessage> OnAuthenticated { get; set; } = _ => new(HttpStatusCode.OK);
        public Fixture()
        {
            Store = new ConfigStore(Machine.MbusFolder, "file");
            Store.Save(new CliConfig { Server = Origin + "/gateway", MachineId = ServerSendGateTests.Machine, WorkspaceId = "w_saved" });
            Store.SaveKeyPem(Key.ExportPrivateKeyPem());
        }
        public CliSettings Settings(bool apiKey = false) => new()
        { Stored = Store.Load(), Server = new Uri(Origin + "/gateway"), MachineName = "fixture", ApiKey = apiKey ? "fixture-api-key" : null };
        public HttpClient Client(bool apiKey = false) => ServerConnection.CreateAuthenticatedClient(Host(), Settings(apiKey), Store, "mcp");
        public CliHost Host(Func<HttpMessageHandler>? authenticated = null, Func<HttpMessageHandler>? anonymous = null)
        {
            var basis = Machine.CreateHost();
            return new CliHost
            {
                Out = basis.Out, Error = basis.Error, In = basis.In,
                OpenStandardInput = basis.OpenStandardInput, OpenStandardOutput = basis.OpenStandardOutput,
                GetEnvironmentVariable = basis.GetEnvironmentVariable, HomeDirectory = basis.HomeDirectory,
                MachineName = basis.MachineName, MbusCommand = basis.MbusCommand, Browser = basis.Browser,
                Skills = basis.Skills, McpServer = basis.McpServer, Time = basis.Time,
                CreateHttpHandler = authenticated ?? (() => new Recorder(Authenticated, OnAuthenticated)),
                CreateDiscoveryHttpHandler = anonymous ?? (() => new Recorder(Anonymous, _ =>
                    new HttpResponseMessage(DiscoveryStatus) { Content = new StringContent(Discovery) })),
            };
        }
        public void Dispose() { Key.Dispose(); Machine.Dispose(); }
    }

    private sealed class Recorder(List<RecordedRequest> records, Func<RecordedRequest, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest(request.Method, request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
                request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            records.Add(recorded);
            return answer(recorded);
        }
    }

    private sealed class BufferedBarrierContent(CancellationToken ct) : HttpContent
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            await stream.WriteAsync("fixture body"u8.ToArray(), ct);
        }
    }
}
