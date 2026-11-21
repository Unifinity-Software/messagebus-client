using System.Buffers.Text;
using System.Collections.Concurrent;
using System.CommandLine;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Security;
using MessageBus.Cli.Server;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Runner.Transport;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class IsolatedHttpsJourneyTests(ITestOutputHelper testOutput)
{
    private const string MachineId = "mch_00000000000000000000000000";

    [Theory]
    [InlineData("before-root")]
    [InlineData("after-root")]
    public void Failed_certificate_setup_disposes_timer_registration_and_preserves_primary_failure(string failAt)
    {
        using var stop = new CancellationTokenSource();
        var endedOutput = new EndedOutput();
        var primary = new IOException("Owned fixture setup failure");
        var error = Assert.Throws<IOException>(() => new IsolatedHttpsServer(stop.Token, endedOutput,
            phase => { if (phase == failAt) throw primary; }));
        Assert.Same(primary, error);
        var attempts = endedOutput.Attempts;
        Assert.True(attempts > 0);
        stop.Cancel();
        Assert.Equal(attempts, endedOutput.Attempts);
    }

    private sealed class EndedOutput : ITestOutputHelper
    {
        internal int Attempts { get; private set; }
        public string Output => "";
        public void Write(string message) => Fail();
        public void Write(string format, params object?[] args) => Fail();
        public void WriteLine(string message) => Fail();
        public void WriteLine(string format, params object?[] args) => Fail();
        private void Fail() { Attempts++; throw new InvalidOperationException("Test output is no longer active."); }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_TLS_factory_transfers_ciphertext_then_refuses_stripping_after_link(bool environment)
    {
        var Ct = TestContext.Current.CancellationToken;
        await using var server = new IsolatedHttpsServer(Ct, testOutput);
        server.Trace("creating machine profile and signing key");
        using var machine = new TestMachine();
        using var signingKey = MachineKey.Create();
        var store = Profile(machine, server, signingKey);
        machine.Env[EnvVars.Server] = server.Selected;
        machine.Env[EnvVars.ApiKey] = "fixture-key";
        machine.Env[EnvVars.Home] = machine.MbusFolder;
        machine.Env[RunnerEnvVars.Workspace] = "ws_fixture";
        var host = Host(machine, server);
        server.Trace(environment ? "creating API-key client" : "loading machine signer and creating API client");
        using var api = environment
            ? new EnvironmentRunnerHostContext(n => machine.Env.GetValueOrDefault(n), machine.Home, server.Handler).CreateServiceClient()
            : ServerConnection.CreateAuthenticatedClient(host, new CliSettings
            { Stored = store.Load(), Server = new Uri(server.Selected), MachineName = "fixture" }, store, "cli");
        Assert.Equal(server.Selected + "/", api.BaseAddress!.AbsoluteUri);
        server.Trace("creating transfer client and encryption keys");
        // This calls the production factory's real SocketsHttpHandler path, with only its private root replaced.
        using var links = AttachmentTransfers.CreateHttpClient(() => new WebProxy(), server.ChainPolicy());
        using var recipientKey = KeyPair.GenerateP256();
        using var ring = new KeyRing();
        var recipient = new PublicKeyInfo
        {
            KeyId = "ek_fixture", Scope = KeyScope.Workspace, Version = 1,
            Algorithm = recipientKey.PublicKey.Algorithm, PublicKey = recipientKey.PublicKey.ToBase64(),
            Fingerprint = recipientKey.PublicKey.Fingerprint, CreatedAt = DateTimeOffset.UnixEpoch,
        };
        ring.Add(recipient.KeyId, KeyPair.FromPem(recipientKey.ExportPem()));
        var attachments = new AttachmentClient(api, links, "ws_fixture", machine.Home);
        var upload = new AttachmentUpload(new AgentTarget { AgentId = "agt_fixture" }, "fixture.txt", AttachmentFormat.File);
        var ciphertext = Array.Empty<byte>();
        var issued = 0;
        var transfers = new ConcurrentQueue<IsolatedHttpsServer.Request>();
        var link = "api/blob?sig=fixture%2Btoken&part=one%20two";
        server.Respond = async (request, stream, ct) =>
        {
            if (request.RawTarget.EndsWith("/attachments/upload-link", StringComparison.Ordinal))
            {
                Assert.Equal("/gateway%20scope/api/w/ws_fixture/attachments/upload-link", request.RawTarget);
                Assert.Equal("POST", request.Method);
                if (environment)
                {
                    Assert.Equal("MbusKey fixture-key", request.Header("Authorization"));
                    Assert.Equal("ws_fixture", request.Header(MbusHeaders.Workspace));
                }
                else VerifySignature(request, server.Origin, signingKey, "ws_fixture");
                issued++;
                if (issued == 2) server.Stripped = true;
                var response = $$"""{"attachmentId":"att_fixture","url":"{{link}}","maxBytes":1048576,"expiresAt":"2099-01-01T00:00:00Z"}""";
                await IsolatedHttpsServer.ReplyAsync(stream, Encoding.UTF8.GetBytes(response), ct);
                return;
            }
            Assert.Equal("/gateway%20scope/api/blob?sig=fixture%2Btoken&part=one%20two", request.RawTarget);
            IsolatedHttpsServer.AssertCredentialFree(request);
            transfers.Enqueue(request);
            if (request.Method == "PUT") ciphertext = request.Body;
            else Assert.Equal("GET", request.Method);
            await IsolatedHttpsServer.ReplyAsync(stream, request.Method == "GET" ? ciphertext : [], ct);
        };

        using var plaintext = new MemoryStream("private fixture plaintext"u8.ToArray());
        server.Trace("uploading ciphertext");
        var reference = await attachments.UploadAsync(plaintext, upload, recipient, Ct);
        server.Trace("upload complete");
        Assert.NotEmpty(ciphertext);
        Assert.NotEqual(plaintext.ToArray(), ciphertext);
        Assert.Equal(ciphertext.LongLength, reference.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(ciphertext)), reference.Sha256);
        Assert.NotNull(reference.Encryption);
        using var output = new MemoryStream();
        server.Trace("downloading ciphertext");
        await attachments.DownloadAsync(reference, new DownloadLink
        { Id = reference.Id, Url = link, ExpiresAt = DateTimeOffset.MaxValue }, output, ring, Ct);
        Assert.Equal(plaintext.ToArray(), output.ToArray());
        server.Trace("download complete; uploading with stripped discovery");
        using var refusedPlaintext = new MemoryStream("refused fixture plaintext"u8.ToArray());
        await Assert.ThrowsAsync<AttachmentTransferException>(() => attachments.UploadAsync(refusedPlaintext, upload, recipient, Ct));
        Assert.Equal(2, issued);
        Assert.Equal(new[] { "PUT", "GET" }, transfers.Select(r => r.Method));
        Assert.Equal(5, server.Requests.Count(r => r.RawTarget.EndsWith("/api/server", StringComparison.Ordinal)));
        Assert.True(File.Exists(store.ServerBindingsPath));
        server.Trace("attachment assertions complete");
    }

    [Fact(Timeout = 30_000)]
    public async Task Real_WSS_reconnect_rediscovers_and_signs_each_upgrade_then_refuses_stripping()
    {
        var Ct = TestContext.Current.CancellationToken;
        await using var server = new IsolatedHttpsServer(Ct, testOutput);
        using var machine = new TestMachine();
        using var key = MachineKey.Create();
        var store = Profile(machine, server, key);
        var common = new CommonOptions();
        var root = new RootCommand();
        root.Options.Add(common.Server);
        IRunnerHostContext host = new CliRunnerHostContext(CommandContext.Load(Host(machine, server), root.Parse([]), common));
        var upgrades = new ConcurrentQueue<IsolatedHttpsServer.Request>();
        server.Respond = async (request, stream, ct) =>
        {
            Assert.Equal("/gateway%20scope/api/runner/connect", request.RawTarget);
            VerifySignature(request, server.Origin, key, "ws_actual");
            upgrades.Enqueue(request);
            if (upgrades.Count == 2) server.Stripped = true;
            await IsolatedHttpsServer.UpgradeAsync(request, stream, ct);
        };
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        stopping.CancelAfter(TimeSpan.FromSeconds(20));
        var connected = 0;
        ReverseConnectionClient? client = null;
        var options = RunnerTransport.ClientOptions(new Uri(server.Selected), _ => { }) with
        {
            CertificateChainPolicy = server.ChainPolicy(),
            TimeProvider = machine.Time,
            ReconnectDelay = _ => TimeSpan.FromSeconds(1),
            ExecuteConnect = host.ConnectAsync,
            IsTerminalPreparationException = RunnerHandshake.IsTerminal,
            PrepareRequest = (request, _) =>
            {
                request.SetHeader(MbusHeaders.Workspace, "ws_actual");
                return ValueTask.CompletedTask;
            },
            OnStatus = status =>
            {
                if (status.State == ReverseConnectionState.Connected)
                {
                    connected++;
                    Assert.True(client!.DropConnection("fixture reconnect"));
                }
                if (status.State == ReverseConnectionState.Reconnecting) machine.Time.Advance(status.RetryDelay!.Value);
            },
        };
        client = new ReverseConnectionClient(options);
        var result = await client.RunAsync(stopping.Token);
        Assert.Equal(ReverseConnectionStopReason.Refused, result.Reason);
        Assert.Equal(2, connected); // Both actual 101 responses were accepted by ClientWebSocket.
        Assert.Equal(2, upgrades.Count);
        Assert.Equal(2, upgrades.Select(r => r.Header(MbusHeaders.Nonce)).Distinct().Count());
        Assert.Equal(2, upgrades.Select(r => r.Header(MbusHeaders.Timestamp)).Distinct().Count());
        Assert.Equal(new[]
        {
            "/gateway%20scope/api/server", "/gateway%20scope/api/runner/connect",
            "/gateway%20scope/api/server", "/gateway%20scope/api/runner/connect",
            "/gateway%20scope/api/server",
        }, server.Requests.Select(r => r.RawTarget));
        Assert.True(File.Exists(store.ServerBindingsPath));
    }

    [Fact(Timeout = 30_000)]
    public async Task Safe_factory_reaches_private_root_HTTPS_through_real_credential_free_CONNECT()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new IsolatedHttpsServer(ct, testOutput);
        await using var proxy = new FixtureConnectProxy(new Uri(server.Origin), ct);
        using var links = AttachmentTransfers.CreateHttpClient(() => new WebProxy(proxy.Address)
        {
            Credentials = new NetworkCredential("fixture-proxy", "fixture-password"),
        }, server.ChainPolicy());
        using var response = await links.GetAsync(server.Selected + "/api/server", ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal(server.Discovery, await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(1, proxy.Connects);
        var request = Assert.Single(server.Requests);
        Assert.Equal("/gateway%20scope/api/server", request.RawTarget);
        IsolatedHttpsServer.AssertCredentialFree(request);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Private_root_seam_still_rejects_untrusted_chain_and_wrong_hostname(bool wrongHostname)
    {
        var Ct = TestContext.Current.CancellationToken;
        await using var server = new IsolatedHttpsServer(Ct, testOutput);
        server.ExpectTlsRejection = true;
        using var links = AttachmentTransfers.CreateHttpClient(() => new WebProxy(), server.ChainPolicy(trustRoot: wrongHostname));
        var destination = wrongHostname ? server.Origin.Replace("localhost", "127.0.0.1", StringComparison.Ordinal) : server.Origin;
        await Assert.ThrowsAsync<HttpRequestException>(() => links.GetAsync(destination + "/gateway%20scope/api/server", Ct));
        Assert.Empty(server.Requests); // The HTTP request, including any signed query, never crosses failed TLS.
    }

    private static ConfigStore Profile(TestMachine machine, IsolatedHttpsServer server, MachineKey key)
    {
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = server.Selected, WorkspaceId = "ws_fixture", MachineId = MachineId, KeyStore = "file" });
        store.SaveKeyPem(key.ExportPrivateKeyPem());
        return store;
    }

    private static CliHost Host(TestMachine machine, IsolatedHttpsServer server)
    {
        var basis = machine.CreateHost();
        return new()
        {
            Out = basis.Out, Error = basis.Error, In = basis.In, OpenStandardInput = basis.OpenStandardInput,
            OpenStandardOutput = basis.OpenStandardOutput, GetEnvironmentVariable = basis.GetEnvironmentVariable,
            HomeDirectory = basis.HomeDirectory, MachineName = basis.MachineName, MbusCommand = basis.MbusCommand,
            Browser = basis.Browser, Skills = basis.Skills, McpServer = basis.McpServer, Time = machine.Time,
            CreateHttpHandler = server.Handler, CreateDiscoveryHttpHandler = server.Handler,
        };
    }

    private static void VerifySignature(IsolatedHttpsServer.Request request, string origin, MachineKey key, string workspace)
    {
        Assert.Equal("2", request.Header(MbusHeaders.SignatureVersion));
        Assert.Equal("MbusMachine " + MachineId, request.Header("Authorization"));
        Assert.Equal(workspace, request.Header(MbusHeaders.Workspace));
        var nonce = request.Header(MbusHeaders.Nonce)!;
        Assert.Equal(22, nonce.Length);
        var nonceBytes = Base64Url.DecodeFromChars(nonce);
        Assert.Equal(16, nonceBytes.Length);
        Assert.Equal(nonce, Base64Url.EncodeToString(nonceBytes));
        var timestamp = request.Header(MbusHeaders.Timestamp)!;
        Assert.True(long.TryParse(timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds));
        Assert.True(seconds >= 0);
        var signature = Base64Url.DecodeFromChars(request.Header(MbusHeaders.Signature)!);
        Assert.Equal(64, signature.Length);
        Assert.Equal(request.Header(MbusHeaders.Signature), Base64Url.EncodeToString(signature));
        var canonical = string.Join('\n', "mbus-machine-v2", origin, "eu", MachineId, workspace, request.Method,
            request.RawTarget, request.Header(MbusHeaders.Timestamp), nonce, Convert.ToHexStringLower(SHA256.HashData(request.Body)));
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKeyBase64), out _);
        Assert.True(verifier.VerifyData(Encoding.UTF8.GetBytes(canonical), signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
}
