using System.Buffers.Text;
using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Security;
using MessageBus.Cli.Server;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Http;
using MessageBus.Runner.Transport;
using MessageBus.Tests.Shared;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class RunnerTransportBindingTests
{
    private const string Origin = "https://eu.runner.example.test";
    private const string Machine = "mch_00000000000000000000000000";
    private const string Legacy = """{"product":"messagebus.ai","signIn":{"microsoft":true}}""";
    private const string Isolated = """{"product":"messagebus.ai","stamp":{"stampId":"eu","geography":"eu","azureRegion":"westeurope","url":"https://eu.runner.example.test","aliases":[]},"signIn":{"authority":{"version":1,"stampId":"eu","issuer":"https://eu.runner.example.test","audience":"https://eu.runner.example.test"},"machineSignature":{"version":2,"required":true}}}""";

    [Theory]
    [InlineData(301, false)]
    [InlineData(302, false)]
    [InlineData(303, false)]
    [InlineData(307, false)]
    [InlineData(308, false)]
    [InlineData(301, true)]
    [InlineData(302, true)]
    [InlineData(303, true)]
    [InlineData(307, true)]
    [InlineData(308, true)]
    public async Task Real_upgrade_redirect_never_contacts_target_or_replays_credentials(int status, bool sameOrigin)
    {
        using var machine = new TestMachine();
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        target.Start();
        var selected = $"http://127.0.0.1:{((IPEndPoint)origin.LocalEndpoint).Port}/gateway";
        var redirect = sameOrigin ? selected + "/stolen" : $"http://127.0.0.1:{((IPEndPoint)target.LocalEndpoint).Port}/stolen";
        machine.Env[EnvVars.Server] = selected;
        machine.Env[EnvVars.ApiKey] = "fixture-runner-key";
        machine.Env[EnvVars.Home] = machine.MbusFolder;
        var host = new EnvironmentRunnerHostContext(name => machine.Env.GetValueOrDefault(name), machine.Home);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var requests = new List<string>();
        var targetRequests = new List<string>();
        var serving = Serve(origin, false);
        var targetServing = Serve(target, true);
        Task<ReverseConnectionStop>? running = null;
        try
        {
            var options = RunnerTransport.ClientOptions(new Uri(selected), _ => { }) with
            {
                ExecuteConnect = host.ConnectAsync,
                IsTerminalPreparationException = RunnerHandshake.IsTerminal,
            };
            running = new ReverseConnectionClient(options).RunAsync(stop.Token);
            var finished = await Task.WhenAny(running, serving, targetServing);
            if (!ReferenceEquals(finished, running)) await finished; // Surface recorder failures immediately.
            var result = await running;
            Assert.Equal(ReverseConnectionStopReason.Refused, result.Reason);
            Assert.Equal(status, result.StatusCode);
            Assert.Equal(new[] { "GET /gateway/api/server HTTP/1.1", "GET /gateway/api/runner/connect HTTP/1.1" }, requests);
            Assert.Empty(targetRequests);
        }
        finally
        {
            stop.Cancel();
            origin.Stop();
            target.Stop();
            await Task.WhenAll(serving, targetServing, (Task?)running ?? Task.CompletedTask);
        }

        async Task Serve(TcpListener listener, bool redirected)
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient accepted;
                try { accepted = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { return; }
                catch (SocketException) when (stop.IsCancellationRequested) { return; }
                using var connection = accepted;
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var first = await reader.ReadLineAsync(stop.Token) ?? "";
                (redirected ? targetRequests : requests).Add(first);
                var headers = new List<string>();
                for (var line = await reader.ReadLineAsync(stop.Token); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync(stop.Token)) headers.Add(line);
                var discovery = first.Contains("/api/server ", StringComparison.Ordinal);
                if (discovery)
                    Assert.DoesNotContain(headers, h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase) || h.StartsWith("X-Mbus-", StringComparison.OrdinalIgnoreCase));
                else if (!redirected)
                {
                    Assert.Contains("Authorization: MbusKey fixture-runner-key", headers);
                    Assert.Contains(headers, h => h.Equals("Sec-WebSocket-Protocol: mbus.v1", StringComparison.OrdinalIgnoreCase));
                }
                var response = redirected || first.Contains("/stolen ", StringComparison.Ordinal)
                    ? "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : discovery ? $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(Legacy)}\r\nConnection: close\r\n\r\n{Legacy}"
                    : $"HTTP/1.1 {status} Redirect\r\nLocation: {redirect}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response), stop.Token);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Built_in_CLI_reconnect_signs_current_domain_and_refuses_security_regression(bool isolated)
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MachineKeyReason);
        using var machine = new TestMachine();
        using var key = MachineKey.Create();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = Origin + "/gateway", MachineId = Machine, WorkspaceId = "w_saved", KeyStore = "file" });
        store.SaveKeyPem(key.ExportPrivateKeyPem());
        var discovery = isolated ? Isolated : Legacy;
        var anonymous = new List<Uri>();
        machine.Server.On(HttpMethod.Get, "/gateway/api/server", request =>
            {
                Assert.Empty(request.Headers);
                anonymous.Add(request.Uri);
                return new(HttpStatusCode.OK) { Content = new StringContent(discovery) };
            });
        var common = new CommonOptions();
        var root = new RootCommand();
        root.Options.Add(common.Server);
        IRunnerHostContext host = new CliRunnerHostContext(CommandContext.Load(machine.CreateHost(), root.Parse([]), common));
        var nonces = new List<string>();
        var timestamps = new List<string>();
        var competitors = new List<Task>();
        var options = RunnerTransport.ClientOptions(new Uri(Origin + "/gateway"), _ => { }) with
        {
            TimeProvider = machine.Time,
            ReconnectDelay = _ => TimeSpan.FromSeconds(1),
            IsTerminalPreparationException = ex => RunnerHandshake.IsTerminal(ex) || ex is Xunit.Sdk.XunitException,
            PrepareRequest = (request, _) =>
            {
                request.SetHeader(MbusHeaders.Workspace, "w_actual");
                return ValueTask.CompletedTask;
            },
            ExecuteConnect = (request, _, ct) => host.ConnectAsync(request, async token =>
            {
                Assert.Equal("w_actual", request.GetHeader(MbusHeaders.Workspace));
                Assert.Equal(isolated ? "2" : null, request.GetHeader(MbusHeaders.SignatureVersion));
                Assert.Equal("wss", request.Uri.Scheme);
                Assert.Equal("/gateway/api/runner/connect", request.PathAndQuery);
                var canonical = isolated ? string.Join('\n', "mbus-machine-v2", Origin, "eu", Machine, "w_actual", "GET",
                    request.PathAndQuery, request.GetHeader(MbusHeaders.Timestamp), request.GetHeader(MbusHeaders.Nonce),
                    Convert.ToHexStringLower(SHA256.HashData([])))
                    : string.Join('\n', "GET", request.PathAndQuery, request.GetHeader(MbusHeaders.Timestamp),
                        request.GetHeader(MbusHeaders.Nonce), Convert.ToHexStringLower(SHA256.HashData([])));
                using var verifier = ECDsa.Create();
                verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKeyBase64), out var bytesRead);
                Assert.True(verifier.VerifyData(Encoding.UTF8.GetBytes(canonical), Base64Url.DecodeFromChars(request.GetHeader(MbusHeaders.Signature)!),
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
                nonces.Add(request.GetHeader(MbusHeaders.Nonce)!);
                timestamps.Add(request.GetHeader(MbusHeaders.Timestamp)!);
                // A competing pin cannot enter the profile while the actual handshake is in flight.
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var competing = store.WithProfileSecurityLockAsync(_ => { entered.SetResult(); return Task.FromResult(true); }, token);
                Assert.False(entered.Task.IsCompleted);
                // No unobserved task: wait for it after this send releases its reservation.
                competitors.Add(competing);
                await Task.CompletedTask;
                if (nonces.Count == 2) discovery = isolated ? Legacy : """{"product":"messagebus.ai","signIn":{"authority":null}}""";
                throw new HttpRequestException("selected-origin fixture outage");
            }, ct),
            OnStatus = status =>
            {
                if (status.State == ReverseConnectionState.Reconnecting) machine.Time.Advance(status.RetryDelay!.Value);
            },
        };
        var result = await new ReverseConnectionClient(options).RunAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(competitors);
        Assert.Equal(ReverseConnectionStopReason.Refused, result.Reason);
        Assert.Equal(2, nonces.Distinct().Count());
        Assert.Equal(2, timestamps.Distinct().Count());
        Assert.Equal(3, anonymous.Count);
        Assert.All(anonymous, uri => Assert.Equal(Origin + "/gateway/api/server", uri.AbsoluteUri));
        Assert.Equal(isolated, File.Exists(store.ServerBindingsPath));
    }

    [Fact]
    public async Task Independent_environment_HTTP_guards_overrides_stripping_and_off_endpoint_before_caller()
    {
        using var machine = new TestMachine();
        machine.Env[EnvVars.Server] = Origin + "/gateway";
        machine.Env[EnvVars.ApiKey] = "fixture-runner-key";
        machine.Env[EnvVars.Home] = machine.MbusFolder;
        var discovery = Isolated;
        var callerRequests = new List<Uri>();
        var anonymousCount = 0;
        var host = new EnvironmentRunnerHostContext(name => machine.Env.GetValueOrDefault(name), machine.Home,
            () => new Handler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/api/server", StringComparison.Ordinal))
                {
                    Assert.Empty(request.Headers);
                    anonymousCount++;
                    return new(HttpStatusCode.OK) { Content = new StringContent(discovery) };
                }
                callerRequests.Add(request.RequestUri);
                Assert.Equal("MbusKey fixture-runner-key", request.Headers.Authorization!.ToString());
                return new(HttpStatusCode.OK);
            }));
        using var client = host.CreateServiceClient();
        Assert.Empty(client.DefaultRequestHeaders);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/caller");
        request.Headers.Authorization = new("Bearer", "untrusted-override");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        discovery = Legacy;
        await Assert.ThrowsAsync<ServerBindingException>(() => client.GetAsync("api/caller", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ServerBindingException>(() => client.GetAsync("https://ca.runner.example.test/api/caller", TestContext.Current.CancellationToken));
        Assert.Single(callerRequests);
        Assert.Equal(2, anonymousCount);
        Assert.True(File.Exists(Path.Combine(machine.MbusFolder, "server-bindings.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provisioned_environment_refuses_missing_home_or_stripped_first_contact_before_caller(bool durableHome)
    {
        using var machine = new TestMachine();
        machine.Env[EnvVars.Server] = Origin;
        machine.Env[EnvVars.ApiKey] = "fixture-runner-key";
        machine.Env[ServerBindingExpectation.EnvironmentVariable] = """{"version":1,"transportOrigin":"https://eu.runner.example.test","stampId":"eu","issuer":"https://eu.runner.example.test","audience":"https://eu.runner.example.test","authorityVersion":1,"minimumMachineSignatureVersion":2,"machineSignatureRequired":true}""";
        if (durableHome) machine.Env[EnvVars.Home] = machine.MbusFolder;
        var destinations = new List<Uri>();
        var host = new EnvironmentRunnerHostContext(name => machine.Env.GetValueOrDefault(name), machine.Home,
            () => new Handler(request =>
            {
                Assert.Empty(request.Headers);
                destinations.Add(request.RequestUri!);
                return new(HttpStatusCode.OK) { Content = new StringContent(Legacy) };
            }));
        using var client = host.CreateServiceClient();
        await Assert.ThrowsAsync<ServerBindingException>(() => client.GetAsync("api/caller", TestContext.Current.CancellationToken));
        if (durableHome) Assert.Equal(Origin + "/api/server", Assert.Single(destinations).AbsoluteUri);
        else Assert.Empty(destinations);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
